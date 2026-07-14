namespace Appa;

using System.Diagnostics;

// The semantic pipeline: source files to a fully lowered IrModule, ready to print.
// Front-end orchestration lives here so both the CLI (Program.cs) and a test project
// can drive a build in-process, without shelling out to the appa binary.
/// <summary>
/// Orchestrates the compiler's front-end: parsing, import resolution, name/type
/// resolution, and IR lowering. Called by the CLI build command and by tests.
/// </summary>
internal static class Pipeline
{
    #region Project discovery

    /// <summary>
    /// Finds the project file marked @environment in the project root.
    /// Parses the top-level *.g files and returns the first one carrying the marker.
    /// </summary>
    public static string? DiscoverEnv(string projectRoot)
    {
        foreach (var f in Directory.GetFiles(projectRoot, "*.g").OrderBy(x => x, StringComparer.Ordinal))
            try
            {
                var prog = new Parser(new Lexer(File.ReadAllText(f)).Tokenize()).ParseProgram();
                if (prog.Items.OfType<EnvironmentDecl>().Any()) return Path.GetFullPath(f);
            }
            catch (ParseException) { }
        return null;
    }

    /// <summary>
    /// Returns the entry point path (src/main.g convention), or null if it does not exist.
    /// </summary>
    public static string? DiscoverEntry(string projectRoot)
    {
        string p = Path.Combine(projectRoot, "src", "main.g");
        return File.Exists(p) ? Path.GetFullPath(p) : null;
    }

    /// <summary>
    /// Returns the libgata directory from the appa install, or null if not found.
    /// </summary>
    public static string? FindLibgata()
    {
        if (Directory.Exists(AppaPaths.LibgataDir) && Directory.GetFiles(AppaPaths.LibgataDir, "*.g").Any())
            return AppaPaths.LibgataDir;
        return null;
    }

    /// <summary>
    /// Resolves an unquoted library import name to a file path in the libgata directory.
    /// Reports a diagnostic and returns an empty string if the module file is missing.
    /// </summary>
    public static string ResolveLibgata(string name, string libgataDir, string fromFile,
                                        DiagnosticBag diag, TextSpan span)
    {
        string candidate = Path.Combine(libgataDir, name + ".g");
        if (File.Exists(candidate)) return candidate;
        diag.Error(Codes.File, fromFile, span, $"cannot find library module '{name}' ({name}.g) in {libgataDir}");
        return "";
    }

    #endregion

    #region Build pipeline

    /// <summary>
    /// Runs the full front-end and lowering pipeline over a parsed program set and
    /// returns the lowered module, its name sourcemap, and the scanned capability set.
    /// </summary>
    public static (IrModule Module, IReadOnlyDictionary<string, string> Sourcemap, CapabilityScan Caps) BuildModule(
        List<(string path, Appa.Program prog)> programs,
        Dictionary<string, HashSet<string>> visible, Mode mode, DiagnosticBag diag)
    {
        Mangler.ResetDense();
        Mangler.ResetGenericDisplay();
        new Monomorphizer(diag).Process(programs);
        var collected = new SymbolCollector(diag).Collect(programs.Select(t => (t.path, t.prog)).ToList());
        var module = new TypeResolver(collected.Sym, collected.HasInit,
                                      collected.PreDefinedStructs, collected.OpaqueFieldClasses, visible,
                                      releaseMode: mode == Mode.Release, diag)
                         .Resolve(programs.Select(t => (t.prog, t.path)).ToList());
        // The backend assumes well-typed IR. If the front-end reported any error, stop
        // here: lowering an ill-typed program would otherwise fault.
        if (diag.HasErrors)
            return (module, new Dictionary<string, string>(), new CapabilityScan(module));
        module = new Desugar(collected.Sym, diag).Run(module);
        // Infer the capability set here: interpolation is now explicit calls, names are
        // still readable, and ARC has not yet sprayed retain/release everywhere.
        var caps = new CapabilityScan(module).Run();
        module = new Dce(module).Run();
        var (dense, sourcemap) = new Densifier(module).Run();
        module = new Ownership(dense).Run();
        return (module, sourcemap, caps);
    }

    // Parse each file once and walk the module graph from its parsed import decls.
    // Files are returned in dependency order; imports records each file's directly-imported
    // files for scope resolution.
    /// <summary>
    /// Parses the given entry files and follows their imports transitively, returning
    /// the parsed programs in dependency order along with the per-file import graph.
    /// </summary>
    public static (List<(string path, Appa.Program prog)> programs, List<string> attempted,
            Dictionary<string, List<string>> imports, DiagnosticBag diag)
        Transpile(List<string> inputFiles, string projectRoot, string libgataDir)
    {
        var sources = new SourceSet();
        var diag = new DiagnosticBag(sources);
        var ordered = new List<(string path, Appa.Program prog)>();
        var attempted = new List<string>();
        var imports = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Resolve(string path, string? from, TextSpan fromSpan)
        {
            path = Path.GetFullPath(path);
            if (!visited.Add(path)) return;
            if (!File.Exists(path))
            {
                diag.Error(Codes.File, from ?? path, fromSpan, $"file not found: '{path}'");
                attempted.Add(path);
                return;
            }
            string src = File.ReadAllText(path);
            sources.Add(path, src);

            Appa.Program? prog = null;
            try { prog = new Parser(new Lexer(src).Tokenize()).ParseProgram(); }
            catch (ParseException ex) { diag.Error(ex.Code, path, ex.Span, ex.Message); }

            var edges = new List<string>();
            if (prog != null)
                foreach (var imp in prog.Items.OfType<ImportDecl>())
                {
                    string resolved = imp.IsPath
                        ? Path.GetFullPath(Path.Combine(projectRoot, imp.Name))
                        : ResolveLibgata(imp.Name, libgataDir, path, diag, imp.Span);
                    if (resolved == "") continue;
                    resolved = Path.GetFullPath(resolved);
                    edges.Add(resolved);
                    Resolve(resolved, path, imp.Span);
                }
            imports[path] = edges;
            if (prog != null) ordered.Add((path, prog));
            attempted.Add(path);
        }

        foreach (var f in inputFiles) Resolve(Path.GetFullPath(f), null, TextSpan.None);
        return (ordered, attempted, imports, diag);
    }

    /// <summary>
    /// For each file, computes the set of files whose top-level names it may reference:
    /// itself plus the transitive closure of its imports.
    /// </summary>
    public static Dictionary<string, HashSet<string>> VisibleModules(Dictionary<string, List<string>> imports)
    {
        var visible = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in imports.Keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };
            var stack = new Stack<string>([file]);
            while (stack.Count > 0)
                foreach (var dep in imports.GetValueOrDefault(stack.Pop(), []))
                    if (seen.Add(dep)) stack.Push(dep);
            visible[file] = seen;
        }
        return visible;
    }

    /// <summary>
    /// Validates that exactly one @environment file takes part in the build.
    /// </summary>
    public static void ValidateEnvironment(List<(string path, Appa.Program prog)> programs, DiagnosticBag diag)
    {
        var envs = programs
            .SelectMany(t => t.prog.Items.OfType<EnvironmentDecl>().Select(e => (t.path, e.Span)))
            .ToList();
        if (envs.Count == 0)
            diag.Error(Codes.File, "", TextSpan.None, "no @environment file in the build");
        else
            foreach (var (path, span) in envs.Skip(1))
                diag.Error(Codes.File, path, span, "multiple @environment files; exactly one is allowed");
    }

    /// <summary>
    /// Validates that every _env_* floor bind referenced in the lowered IR is provided
    /// by the active environment's @preamble. Turns missing-bind link errors into diagnostics.
    /// </summary>
    public static void ValidateFloor(IrModule module, DiagnosticBag diag)
    {
        var probe = new EnvProbe(module.Symbols);
        probe.Run(module);

        // The topology launcher calls _env_proc_create/_env_thread_spawn directly - that's
        // IR-directed codegen not visible to EnvProbe's expression walk. Only matters when
        // a launcher will actually be emitted (dual-realm: kernel + user both present).
        if (module.Processes.Count > 0 && module.HasKernelRealm && module.HasUserRealm)
        {
            probe.Refs.Add(module.Symbols.IntrinsicOrNull(Roles.EnvProcCreate) ?? "_env_proc_create");
            probe.Refs.Add(module.Symbols.IntrinsicOrNull(Roles.EnvThreadSpawn) ?? "_env_thread_spawn");
            if (module.Processes.Any(p => p.Mode == "background"))
                probe.Refs.Add(module.Symbols.IntrinsicOrNull(Roles.EnvProcHide) ?? "_env_proc_hide");
        }
        if (probe.Refs.Count == 0) return;

        string env = NativeC.Mask(string.Join("\n", module.NativeBlocks
            .Where(nb => nb.Section == NativeSection.Preamble)
            .SelectMany(nb => new[] { nb.KernelC, nb.UserC })));

        foreach (var name in probe.Refs.OrderBy(n => n, StringComparer.Ordinal))
            if (!System.Text.RegularExpressions.Regex.IsMatch(env, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b"))
                diag.Error(Codes.MissingFloorBind, "<environment>", TextSpan.None,
                    $"the active environment's @preamble provides no definition of '{name}'; add one (the environment file, not your Gata source, is incomplete)");
    }

    /// <summary>
    /// Validates the build's realm structure against its target. GatOS requires exactly one
    /// kernel block with exactly one entry func (any number of user blocks). Hosted forbids
    /// any kernel block and requires exactly one user block with exactly one entry func. With
    /// no target (loose --pure-transpile mode), the target-agnostic GatOS-shaped rule applies,
    /// since there's no target signal to react to.
    /// </summary>
    public static void ValidateStructure(List<(string path, Appa.Program prog)> programs, Target? target, DiagnosticBag diag)
    {
        if (diag.HasErrors) return;

        var kernelBlocks = new List<(string file, TextSpan span)>();
        var userBlocks = new List<(string file, TextSpan span, TopLevel[] items)>();

        foreach (var (path, prog) in programs)
            foreach (var item in prog.Items)
                if (item is ContextDecl c && c.Kind == "kernel")
                    kernelBlocks.Add((path, c.Span));
                else if (item is ContextDecl u && u.Kind == "user")
                    userBlocks.Add((path, u.Span, u.Items));

        if (target == Target.Hosted)
        {
            foreach (var (file, span) in kernelBlocks)
                diag.Error(Codes.KernelBlockInHosted, file, span, "a 'kernel { }' block is not allowed in a Hosted build");

            if (userBlocks.Count == 0)
            {
                diag.Error(Codes.MissingUserRealm, "", TextSpan.None, "no 'user { }' block found in any .g file - a Hosted build requires exactly one");
                return;
            }
            foreach (var (file, span, _) in userBlocks.Skip(1))
                diag.Error(Codes.DuplicateUserRealm, file, span, "only one 'user { }' block may exist in a Hosted build");

            var entryFuncs = new List<(string file, TextSpan span)>();
            foreach (var inner in userBlocks[0].items)
                if (inner is FuncDecl { IsEntry: true } ef)
                    entryFuncs.Add((userBlocks[0].file, ef.Span));

            if (entryFuncs.Count == 0)
                diag.Error(Codes.MissingUserEntry, userBlocks[0].file, userBlocks[0].span,
                    "the 'user { }' block declares no 'entry func'");
            else
                foreach (var (file, span) in entryFuncs.Skip(1))
                    diag.Error(Codes.DuplicateUserEntry, file, span, "the 'user { }' block declares more than one 'entry func'");
            return;
        }

        // GatOS, or no target (loose transpile mode): the original kernel-block rule.
        var kernelEntryFuncs = new List<(string file, TextSpan span)>();
        foreach (var (path, prog) in programs)
            foreach (var item in prog.Items)
                if (item is ContextDecl c && c.Kind == "kernel")
                    foreach (var inner in c.Items)
                        if (inner is FuncDecl { IsEntry: true } ef)
                            kernelEntryFuncs.Add((path, ef.Span));

        if (kernelBlocks.Count == 0)
        {
            diag.Error(Codes.MissingEntryPoint, "", TextSpan.None, "no 'kernel { }' entry point found in any .g file");
            return;
        }

        foreach (var (file, span) in kernelBlocks.Skip(1))
            diag.Error(Codes.DuplicateContext, file, span, "only one 'kernel { }' block may exist in the project");

        if (kernelEntryFuncs.Count == 0)
            diag.Error(Codes.MissingEntryPoint, kernelBlocks[0].file, kernelBlocks[0].span,
                "the 'kernel { }' block declares no 'entry func'");
        else
            foreach (var (file, span) in kernelEntryFuncs.Skip(1))
                diag.Error(Codes.DuplicateName, file, span, "the 'kernel { }' block declares more than one 'entry func'");
    }

    /// <summary>
    /// Warns about reference cycles among managed classes that will not be freed by ARC.
    /// Uses Tarjan's SCC algorithm on the field-type graph.
    /// </summary>
    public static void WarnReferenceCycles(IrModule module)
    {
        var managed = module.Classes.Where(c => !c.IsModule).Select(c => c.Name).ToHashSet();
        var edges = new Dictionary<string, List<string>>();
        foreach (var c in module.Classes)
        {
            if (c.IsModule) continue;
            var outs = new List<string>();
            foreach (var f in c.Fields)
                if (f.Type is IrClassRef cr && managed.Contains(cr.ClassName))
                    outs.Add(cr.ClassName);
            edges[c.Name] = outs;
        }

        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var onStk = new HashSet<string>();
        var stack = new Stack<string>();
        int counter = 0;
        var cycles = new List<List<string>>();

        void Strong(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v); onStk.Add(v);
            foreach (var w in edges.GetValueOrDefault(v, []))
            {
                if (!index.ContainsKey(w)) { Strong(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStk.Contains(w)) low[v] = Math.Min(low[v], index[w]);
            }
            if (low[v] == index[v])
            {
                var comp = new List<string>();
                string w;
                do { w = stack.Pop(); onStk.Remove(w); comp.Add(w); } while (w != v);
                bool selfLoop = comp.Count == 1 && edges.GetValueOrDefault(comp[0], []).Contains(comp[0]);
                if (comp.Count > 1 || selfLoop) cycles.Add(comp);
            }
        }

        foreach (var v in edges.Keys)
            if (!index.ContainsKey(v)) Strong(v);

        foreach (var comp in cycles)
            Log.Warn($"Reference cycle among class(es) {{{string.Join(", ", comp.OrderBy(x => x).Select(Mangler.DisplayName))}}} " +
                     "will not be freed by reference counting (potential leak). " +
                     "Break it with a raw pointer field inside 'unsafe', or restructure ownership.");
    }

    /// <summary>
    /// Per-file pass/fail report for the Gata check phase.
    /// Redraws one "Checking [i/n] file" line in place as files clear.
    /// On the first file with errors (or warnings under --werror) prints diagnostics and exits.
    /// </summary>
    public static void ReportGataFiles(List<string> attempted, DiagnosticBag diag, bool warnAsError)
    {
        var known = new HashSet<string>(attempted);
        bool tty = !Console.IsOutputRedirected;
        void Fail(IEnumerable<Diagnostic> ds)
        {
            if (tty) Out.ClearRedraw();
            var list = ds.OrderBy(diag.LineOf).ToList();
            foreach (var d in list) Console.Error.WriteLine(diag.Render(d));
            Console.Error.WriteLine();
            Console.Error.WriteLine(CountSummary(
                list.Count(d => d.Severity == Severity.Error),
                list.Count(d => d.Severity == Severity.Warning)));
            Environment.Exit(1);
        }

        var sw = Stopwatch.StartNew();
        int i = 0;
        foreach (var path in attempted)
        {
            i++;
            var fileDiags = diag.All.Where(d => string.Equals(d.Loc.File, path, StringComparison.OrdinalIgnoreCase)).ToList();
            var errors = fileDiags.Where(d => d.Severity == Severity.Error).ToList();
            var warnings = fileDiags.Where(d => d.Severity == Severity.Warning).ToList();

            if (errors.Count > 0 || (warnAsError && warnings.Count > 0))
                Fail(errors.Concat(warnings));

            if (tty) Out.Redraw($"  {C.DIM}⠿ Checking [{i}/{attempted.Count}] {Path.GetFileName(path)}{C.NC}");
            if (warnings.Count > 0)
            {
                if (tty) Out.ClearRedraw();
                foreach (var w in warnings) Console.WriteLine(diag.Render(w));
            }
        }

        var orphan = diag.All.Where(d => d.Severity == Severity.Error && !known.Contains(d.Loc.File)).ToList();
        if (orphan.Count > 0) Fail(orphan);

        if (tty) Out.ClearRedraw();
        Spin.Done($"Checked {attempted.Count} file{(attempted.Count == 1 ? "" : "s")}", sw.Elapsed);
    }

    /// <summary>
    /// Formats an error/warning count as a human-readable summary line.
    /// </summary>
    public static string CountSummary(int errors, int warnings)
    {
        string e = errors > 0 ? $"{errors} error{(errors == 1 ? "" : "s")}" : "";
        string w = warnings > 0 ? $"{warnings} warning{(warnings == 1 ? "" : "s")}" : "";
        return e.Length > 0 && w.Length > 0 ? $"{e}, {w}" : e + w;
    }

    #endregion
}

// Collects every `_env_*` floor bind referenced anywhere in the lowered IR.
sealed class EnvProbe(SymbolTable sym) : IrRewriter
{
    public readonly HashSet<string> Refs = [];

    /// <summary>
    /// Collects _env_* names from static call expressions.
    /// </summary>
    protected override IrExpr RewriteExpr(IrExpr e)
    {
        if (e is IrStaticCall { CName: var c } && c.StartsWith("_env_")) Refs.Add(c);
        return base.RewriteExpr(e);
    }

    /// <summary>
    /// Collects debug/panic statements' bound C names (never a hardcoded literal -
    /// whatever libgata's @intrinsic(env_debug)/@intrinsic(env_panic) resolve to).
    /// </summary>
    protected override IrStmt RewriteStmt(IrStmt s)
    {
        if (s is IrDebug) Refs.Add(sym.IntrinsicOrNull(Roles.EnvDebug) ?? "_env_dbg");
        if (s is IrPanic) Refs.Add(sym.IntrinsicOrNull(Roles.EnvPanic) ?? "_env_panic");
        return base.RewriteStmt(s);
    }

    /// <summary>
    /// Runs the probe over the whole module and populates Refs.
    /// </summary>
    public new void Run(IrModule m)
    {
        foreach (var c in m.Classes)
        {
            foreach (var mm in c.Methods) if (mm.Body != null) RewriteStmt(mm.Body);
            foreach (var o in c.Operators) if (o.Body != null) RewriteStmt(o.Body);
        }
        foreach (var f in m.FreeFunctions) if (f.Body != null) RewriteStmt(f.Body);
    }
}
