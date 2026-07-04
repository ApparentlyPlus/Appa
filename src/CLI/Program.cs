using Appa;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

#region Entry point

// Windows consoles historically default to a non-UTF8 codepage; Unix terminals
// already default to UTF-8, so this only needs to run on Windows.
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0) { PrintHelp(); Environment.Exit(1); }
try
{
    switch (args[0])
    {
        case "setup": RunSetup(isUpdate: false); break;
        case "update": RunSetup(isUpdate: true); break;
        case "init": RunInit(args[1..]); break;
        case "build": RunBuild(args[1..]); break;
        case "--help":
        case "-h": PrintHelp(); break;
        default:
            Log.Error($"Unknown command '{args[0]}'");
            PrintHelp();
            Environment.Exit(1);
            break;
    }
}
catch (Exception ex)
{
    // Last-resort net: every expected failure already reports through Log/DiagnosticBag
    // and exits on its own. Reaching here means a genuine compiler-internal bug.
    Log.Error($"internal compiler error: {ex.Message}");
    Console.Error.WriteLine(ex);
    Environment.Exit(1);
}

#endregion

#region appa init

/// <summary>
/// Scaffolds a new GatOS project: a .gconf, an env.g copied from the installed
/// environment, and a starter src/main.g, then prints a short file tree.
/// </summary>
static void RunInit(string[] args)
{
    string name = args.ElementAtOrDefault(0) ?? "myproject";
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
        Fail($"'{name}' is not a valid project name");

    string projDir = Path.GetFullPath(name);
    if (Directory.Exists(projDir)) Fail($"directory '{name}' already exists");

    string envSrc = Path.Combine(AppaPaths.EnvsDir, "env.GatOS.g");
    if (!File.Exists(envSrc))
        Fail("GatOS environment not found. Run 'appa setup' first.");

    Directory.CreateDirectory(Path.Combine(projDir, "src"));
    File.Copy(envSrc, Path.Combine(projDir, "env.g"));
    File.WriteAllText(Path.Combine(projDir, $"{name}.gconf"), Templates.GatOSGconf(name));
    File.WriteAllText(Path.Combine(projDir, "src", "main.g"), Templates.GatOSMain(name));

    var entries = new (string Path, string Desc)[]
    {
        ($"{name}.gconf", "build configuration"),
        ("env.g", "platform environment (@environment)"),
        ("src/main.g", "entry point"),
    };
    int width = entries.Max(e => e.Path.Length);

    Console.WriteLine();
    Console.WriteLine($"Created {C.BOLD}{name}{C.NC} {C.DIM}(GatOS){C.NC}");
    Console.WriteLine();
    Console.WriteLine($"{C.DIM}{name}/{C.NC}");
    for (int i = 0; i < entries.Length; i++)
    {
        string branch = i == entries.Length - 1 ? "└─" : "├─";
        var (p, desc) = entries[i];
        Console.WriteLine($"{C.DIM}{branch}{C.NC} {p.PadRight(width)}  {C.DIM}{desc}{C.NC}");
    }
    Console.WriteLine();
    Console.WriteLine($"{C.CYAN}Next:{C.NC}");
    Console.WriteLine($"  cd {name}");
    Console.WriteLine($"  appa build");
    Console.WriteLine();
}

#endregion

#region appa build

// appa build [project | project.gconf] [--run] [--headless] [--timeout=<Xs>]
//            [--werror] [--stdlib <dir>]
//   --pure-transpile --env <env.g> --entry <file.g>   (file-level: no project dir)
//
// A project build auto-discovers its environment (the @environment file in the
// project dir) and its entry (src/main.g). --env/--entry also work standalone
// against a real manifest project, to point at a shared environment file without
// copying it into every project dir.
/// <summary>
/// Parses build arguments, transpiles and lowers the project, then dispatches to
/// the GatOS image builder or the hosted pure-transpile path.
/// </summary>
static void RunBuild(string[] args)
{
    string? manifestArg = null, envOverride = null, entryOverride = null, stdlibOverride = null;
    bool warnAsError = false, doRun = false, headless = false, pureTranspile = false, emitSourcemap = false;
    int? timeout = null;

    for (int i = 0; i < args.Length; i++)
        switch (args[i])
        {
            case "--env" when i+1 < args.Length: envOverride = args[++i]; break;
            case "--entry" when i+1 < args.Length: entryOverride = args[++i]; break;
            case "--stdlib" when i+1 < args.Length: stdlibOverride = args[++i]; break;
            case "--werror": warnAsError = true; break;
            case "--run": doRun = true; break;
            case "--headless": headless = true; break;
            case "--pure-transpile": pureTranspile = true; break;
            case "--emit-sourcemap": emitSourcemap = true; break;
            default:
                if (args[i].StartsWith("--timeout=")) timeout = ParseTimeout(args[i]["--timeout=".Length..]);
                else if (args[i].StartsWith("--")) Fail($"unknown option '{args[i]}'");
                else manifestArg = args[i];
                break;
        }

    bool looseTranspile = pureTranspile && envOverride != null && entryOverride != null;
    Manifest? manifest = null;
    if (!looseTranspile)
    {
        try
        {
            string? manifestPath =
                manifestArg == null ? ManifestReader.Discover(Directory.GetCurrentDirectory())
                : Directory.Exists(manifestArg) ? ManifestReader.Discover(manifestArg)
                : manifestArg;
            if (manifestPath != null) manifest = ManifestReader.Load(manifestPath);
        }
        catch (ManifestError e) { Fail(e.Message); }
        if (manifest == null)
            Fail("no <project>.gconf found - run 'appa init', or use --pure-transpile --env <file> --entry <file>");
    }
    else if (manifestArg != null)
        Log.Warn($"project argument '{manifestArg}' is ignored with --pure-transpile --env --entry (loose-file mode discovers nothing from a project)");

    string? envPath = envOverride ?? (manifest != null ? DiscoverEnv(manifest.Dir) : null);
    string? entryPath = entryOverride ?? (manifest != null ? DiscoverEntry(manifest.Dir) : null);
    if (envPath == null) Fail("no environment found - mark one project file @environment, or pass --env");
    if (entryPath == null) Fail("no entry point - expected src/main.g, or pass --entry");

    string projectRoot = manifest?.Dir ?? Path.GetDirectoryName(Path.GetFullPath(entryPath))!;
    string? stdlibDir = stdlibOverride ?? FindLibgata();
    if (stdlibDir == null) Fail("cannot find libgata - run 'appa setup' or pass --stdlib <dir>");
    foreach (var p in new[] { envPath, entryPath })
        if (!File.Exists(p)) Fail($"file not found: {p}");

    if (manifest != null)
        Console.WriteLine($"{C.BOLD}Building{C.NC} {manifest.ProjectName} {C.DIM}({manifest.Target}, {manifest.Mode.ToString().ToLowerInvariant()}){C.NC}");
    else
        Console.WriteLine($"{C.BOLD}Building{C.NC} {C.DIM}(--pure-transpile){C.NC}");
    Console.WriteLine();

    var inputFiles = new List<string> { Path.GetFullPath(envPath), Path.GetFullPath(entryPath) };
    var (programs, attempted, imports, diag) = Transpile(inputFiles, projectRoot, stdlibDir!);
    var visible = VisibleModules(imports);
    var (module, sourcemap, caps) = BuildModule(programs, visible, manifest?.Mode ?? Mode.Debug, diag);

    ValidateEnvironment(programs, diag);
    ValidateFloor(module, diag);
    ValidateStructure(programs, diag);
    if (!diag.HasErrors) WarnReferenceCycles(module);
    ReportGataFiles(attempted, diag, warnAsError);

    var output = Layout.Compose(new Emitter(module, diag).Build());

    if (diag.HasErrors)
    {
        foreach (var d in diag.All.Where(d => d.Severity == Severity.Error))
            Console.Error.WriteLine(diag.Render(d));
        Environment.Exit(1);
    }

    bool emitIso = !pureTranspile && manifest is { Target: Target.GatOS };
    if (!emitIso && (doRun || headless || timeout != null))
        Log.Warn("--run/--headless/--timeout only apply to a GatOS image build; ignoring (this build just writes C)");
    if (!emitIso)
    {
        string outDir = Path.Combine(projectRoot, "transpilation");
        WriteOutputs(output, outDir);
        if (emitSourcemap) WriteSourcemap(sourcemap, outDir);
        Console.WriteLine();
        Console.WriteLine($"{C.BOLD}Finished{C.NC} {C.DIM}→{C.NC} {outDir}{Path.DirectorySeparatorChar}");
        foreach (var f in output) Out.Child($"{C.DIM}{Path.Combine("transpilation", f.Name)}{C.NC}");
        return;
    }

    if (emitSourcemap) WriteSourcemap(sourcemap, projectRoot);
    var defines = CapabilityDefines(caps, manifest!);
    BuildGatOSImage(output, manifest!, projectRoot, defines, CapabilitiesNote(caps, manifest!), doRun, headless, timeout);
}

#endregion

#region Project discovery

/// <summary>
/// Finds the project file marked @environment in the project root.
/// Parses the top-level *.g files and returns the first one carrying the marker.
/// </summary>
static string? DiscoverEnv(string projectRoot)
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
static string? DiscoverEntry(string projectRoot)
{
    string p = Path.Combine(projectRoot, "src", "main.g");
    return File.Exists(p) ? Path.GetFullPath(p) : null;
}

/// <summary>
/// Returns the libgata directory from the appa install, or null if not found.
/// </summary>
static string? FindLibgata()
{
    if (Directory.Exists(AppaPaths.LibgataDir) && Directory.GetFiles(AppaPaths.LibgataDir, "*.g").Any())
        return AppaPaths.LibgataDir;
    return null;
}

/// <summary>
/// Resolves an unquoted library import name to a file path in the libgata directory.
/// Reports a diagnostic and returns an empty string if the module file is missing.
/// </summary>
static string ResolveLibgata(string name, string libgataDir, string fromFile,
                             DiagnosticBag diag, TextSpan span)
{
    string candidate = Path.Combine(libgataDir, name + ".g");
    if (File.Exists(candidate)) return candidate;
    diag.Error(Codes.File, fromFile, span, $"cannot find library module '{name}' ({name}.g) in {libgataDir}");
    return "";
}

#endregion

#region Utilities

/// <summary>
/// Writes all output files to a directory, creating it if necessary.
/// </summary>
static void WriteOutputs(IReadOnlyList<OutputFile> files, string dir)
{
    Directory.CreateDirectory(dir);
    foreach (var f in files) File.WriteAllText(Path.Combine(dir, f.Name), f.Content);
}

// The sourcemap: dense machine name to original readable C name, written as JSON by hand
// (no reflection-based serializer, AOT-safe).
/// <summary>
/// Writes the dense-to-readable name sourcemap as sourcemap.json in the given directory.
/// </summary>
static void WriteSourcemap(IReadOnlyDictionary<string, string> map, string dir)
{
    if (map.Count == 0) return;
    Directory.CreateDirectory(dir);
    var sb = new System.Text.StringBuilder("{\n");
    var items = map.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
    for (int i = 0; i < items.Count; i++)
        sb.Append($"  \"{items[i].Key}\": \"{items[i].Value}\"{(i < items.Count - 1 ? "," : "")}\n");
    sb.Append("}\n");
    File.WriteAllText(Path.Combine(dir, "sourcemap.json"), sb.ToString());
}

/// <summary>
/// Recursively copies a directory tree from src to dst.
/// </summary>
static void CopyDirectory(string src, string dst)
{
    foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(dir.Replace(src, dst));
    foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        File.Copy(file, file.Replace(src, dst), true);
}

/// <summary>
/// Parses a timeout argument of the form "30s", "5m", or "1h" into seconds.
/// Returns 60 if the format is not recognized.
/// </summary>
static int ParseTimeout(string val)
{
    var m = System.Text.RegularExpressions.Regex.Match(val, @"^(\d+)([smh])$");
    if (!m.Success) return 60;
    int n = int.Parse(m.Groups[1].Value);
    return m.Groups[2].Value switch { "m" => n * 60, "h" => n * 3600, _ => n };
}

/// <summary>
/// Reports a fatal configuration error and exits.
/// </summary>
[DoesNotReturn]
static void Fail(string message) { Log.Error(message); Environment.Exit(1); }

#endregion

#region Help

/// <summary>
/// Prints the top-level usage text: commands, build options, and examples.
/// </summary>
static void PrintHelp() => Console.WriteLine($$"""
{{C.GREEN}}appa{{C.NC}} - the Gata language compiler for GatOS

{{C.CYAN}}Usage:{{C.NC}}
  appa setup                      Install the GatOS toolchain, template, and libgata
  appa update                     Re-download and overwrite the installed GatOS bundle
  appa init [project]             Create a GatOS project
  appa build [project|.gconf]     Build the project described by its .gconf

{{C.YELLOW}}Build options:{{C.NC}}
  --stdlib  <dir>                 Override the libgata directory
  --werror                        Treat warnings as errors
  --pure-transpile                Emit C and stop (file-level: needs --env + --entry)
  --env <env.g>                   Environment file (overrides discovery; required for --pure-transpile)
  --entry <file.g>                Entry source (overrides discovery; required for --pure-transpile)
  --emit-sourcemap                 Write sourcemap.json (dense name -> readable name)
  --run / --headless / --timeout=<Xs>   Launch QEMU after a GatOS image build

  A project build auto-discovers its environment (the @environment file in the
  project dir) and entry (src/main.g) - no --env/--entry needed.

{{C.BLUE}}Examples:{{C.NC}}
  appa setup
  appa init myos && cd myos && appa build --run
  appa build --pure-transpile --env env.g --entry src/main.g
""");

#endregion

#region Build pipeline

// The semantic pipeline: AST programs to a fully lowered IrModule, ready to print.
// Each stage is a total transform - monomorphize generics, collect declarations,
// resolve+typecheck into typed IR, then the IR-to-IR lowering passes that peel ARC and
// desugaring out of the emitter. Front-end orchestration stays in RunBuild.
/// <summary>
/// Runs the full front-end and lowering pipeline over a parsed program set and
/// returns the lowered module, its name sourcemap, and the scanned capability set.
/// </summary>
static (IrModule Module, IReadOnlyDictionary<string, string> Sourcemap, CapabilityScan Caps) BuildModule(
    List<(string path, Appa.Program prog)> programs,
    Dictionary<string, HashSet<string>> visible, Mode mode, DiagnosticBag diag)
{
    Mangler.ResetDense();
    Mangler.ResetGenericDisplay();
    new Monomorphizer(diag).Process(programs.Select(t => (t.prog, t.path)).ToList());
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
static (List<(string path, Appa.Program prog)> programs, List<string> attempted,
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
        catch (ParseException ex) { diag.Error(Codes.File, path, ex.Span, ex.Message); }

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
static Dictionary<string, HashSet<string>> VisibleModules(Dictionary<string, List<string>> imports)
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
static void ValidateEnvironment(List<(string path, Appa.Program prog)> programs, DiagnosticBag diag)
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
static void ValidateFloor(IrModule module, DiagnosticBag diag)
{
    var probe = new EnvProbe();
    probe.Run(module);

    // The topology launcher calls _env_proc_create/_env_thread_spawn directly - that's
    // IR-directed codegen not visible to EnvProbe's expression walk. Only matters when
    // a launcher will actually be emitted (dual-realm: kernel + user both present).
    if (module.Processes.Count > 0 && module.HasKernelRealm && module.HasUserRealm)
    {
        probe.Refs.Add("_env_proc_create");
        probe.Refs.Add("_env_thread_spawn");
        if (module.Processes.Any(p => p.Mode == "background"))
            probe.Refs.Add("_env_proc_hide");
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
/// Validates that the build contains exactly one kernel block with exactly one entry func.
/// </summary>
static void ValidateStructure(List<(string path, Appa.Program prog)> programs, DiagnosticBag diag)
{
    if (diag.HasErrors) return;

    var kernelBlocks = new List<(string file, TextSpan span)>();
    var entryFuncs = new List<(string file, TextSpan span)>();

    foreach (var (path, prog) in programs)
        foreach (var item in prog.Items)
            if (item is ContextDecl c && c.Kind == "kernel")
            {
                kernelBlocks.Add((path, c.Span));
                foreach (var inner in c.Items)
                    if (inner is FuncDecl { IsEntry: true } ef)
                        entryFuncs.Add((path, ef.Span));
            }

    if (kernelBlocks.Count == 0)
    {
        diag.Error(Codes.MissingEntryPoint, "", TextSpan.None, "no 'kernel { }' entry point found in any .g file");
        return;
    }

    foreach (var (file, span) in kernelBlocks.Skip(1))
        diag.Error(Codes.DuplicateContext, file, span, "only one 'kernel { }' block may exist in the project");

    if (entryFuncs.Count == 0)
        diag.Error(Codes.MissingEntryPoint, kernelBlocks[0].file, kernelBlocks[0].span,
            "the 'kernel { }' block declares no 'entry func'");
    else
        foreach (var (file, span) in entryFuncs.Skip(1))
            diag.Error(Codes.DuplicateName, file, span, "the 'kernel { }' block declares more than one 'entry func'");
}

/// <summary>
/// Warns about reference cycles among managed classes that will not be freed by ARC.
/// Uses Tarjan's SCC algorithm on the field-type graph.
/// </summary>
static void WarnReferenceCycles(IrModule module)
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
static void ReportGataFiles(List<string> attempted, DiagnosticBag diag, bool warnAsError)
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
static string CountSummary(int errors, int warnings)
{
    string e = errors > 0 ? $"{errors} error{(errors == 1 ? "" : "s")}" : "";
    string w = warnings > 0 ? $"{warnings} warning{(warnings == 1 ? "" : "s")}" : "";
    return e.Length > 0 && w.Length > 0 ? $"{e}, {w}" : e + w;
}

/// <summary>
/// Runs an external process, optionally capturing its output and animating a spinner.
/// Reads must drain async before waiting to avoid a full-pipe deadlock.
/// </summary>
static (int ExitCode, string Stdout, string Stderr) Exec(
    string exe, string arguments, string? workDir,
    bool silent = false, bool capture = false, string? spinner = null)
{
    if (!silent && !capture && spinner == null)
        Console.WriteLine($"{C.BLUE}>>> {exe} {arguments}{C.NC}");

    var psi = new ProcessStartInfo(exe, arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = capture,
        RedirectStandardError = capture,
        WorkingDirectory = workDir ?? ""
    };

    using var proc = Process.Start(psi)!;
    var outTask = capture ? proc.StandardOutput.ReadToEndAsync() : null;
    var errTask = capture ? proc.StandardError.ReadToEndAsync() : null;

    if (spinner != null) Spin.WhileRunning(proc, spinner);
    proc.WaitForExit();
    return (proc.ExitCode, outTask?.Result ?? "", errTask?.Result ?? "");
}

#endregion

#region GatOS image build

// Resolved capability set, with every platform-transitive implication applied.
// Mirrors the implications in GatOS's kernel/caps.h exactly so that header's
// #ifdefs and what appa reports/emits here can never drift apart.
/// <summary>
/// Resolves the final Mem/Input/Threads/Discover capability set for a build,
/// combining the scanned capabilities with the manifest's discovery setting.
/// </summary>
static (bool Mem, bool Input, bool Threads, bool Discover) ResolveCaps(CapabilityScan caps, Manifest m)
{
    bool discover = m.CapabilityDiscovery == CapabilityDiscovery.On;
    bool mem = !discover || caps.Mem, input = !discover || caps.Input, threads = !discover || caps.Threads;

    // Threads pull in the whole multitasking stack, which allocates internally.
    mem = mem || threads;

    // The dashboard keyboard cycling is wired through the keyboard IRQ, which only
    // exists under GATA_CAP_INPUT: THREADS implies INPUT.
    input = input || threads;

    // USB hotplug watch runs as its own kernel thread: HOTPLUG implies THREADS.
    if (m.Keyboard == Keyboard.Hotplug) threads = true;

    // xHCI device enumeration allocates heap structures even for a one-time scan.
    if (m.Keyboard is Keyboard.External or Keyboard.Hotplug) { mem = true; input = true; }

    // ACPI/APIC/timer tick are needed whenever the scheduler (THREADS) or keyboard
    // (INPUT) needs IRQ routing; all three map tables/MMIO through the VMM.
    if (threads || input) mem = mem || threads || input;

    return (mem, input, threads, discover);
}

/// <summary>
/// Returns the -D macros for the GatOS gcc build, representing the resolved capability set.
/// MEM/INPUT/THREADS are inferred; FRAMEBUFFER and keyboard level come from the manifest.
/// </summary>
static List<string> CapabilityDefines(CapabilityScan caps, Manifest m)
{
    var r = ResolveCaps(caps, m);

    var d = new List<string>();
    if (r.Mem) d.Add("-DGATA_CAP_MEM");
    if (r.Input) d.Add("-DGATA_CAP_INPUT");
    if (r.Threads) d.Add("-DGATA_CAP_THREADS");
    d.Add(m.Output == Output.Serial ? "-DGATA_OUTPUT_SERIAL" : "-DGATA_CAP_FRAMEBUFFER");
    d.Add(m.Keyboard switch
    {
        Keyboard.External => "-DGATA_KBD_EXTERNAL",
        Keyboard.Hotplug => "-DGATA_KBD_HOTPLUG",
        _ => "-DGATA_KBD_DEFAULT",
    });

    return d;
}

/// <summary>
/// Returns the human-readable capability summary printed before "Finished".
/// </summary>
static string CapabilitiesNote(CapabilityScan caps, Manifest m)
{
    var r = ResolveCaps(caps, m);

    var on = new List<string>();
    if (r.Mem) on.Add("mem"); if (r.Input) on.Add("input"); if (r.Threads) on.Add("threads");
    on.Add(m.Output == Output.Serial ? "serial" : "framebuffer");
    string suffix = r.Discover ? "" : " (discovery off: assumed, not inferred)";
    return $"Capabilities: {string.Join(" ", on)}{suffix} (keyboard={m.Keyboard.ToString().ToLowerInvariant()})";
}

/// <summary>
/// Stages the GatOS template, compiles and links the kernel, builds the ISO,
/// copies it to the project build dir, and optionally runs QEMU.
/// </summary>
static void BuildGatOSImage(IReadOnlyList<OutputFile> output, Manifest manifest,
                            string projectRoot, List<string> defines, string capsNote,
                            bool doRun, bool headless, int? timeout)
{
    if (!Directory.Exists(AppaPaths.TemplateDir) || !Directory.GetDirectories(AppaPaths.TemplateDir).Any())
        Fail("GatOS template not found. Run 'appa setup' first.");
    if (!File.Exists(AppaPaths.Gcc()))
        Fail("Toolchain not found. Run 'appa setup' first.");

    string buildDir = Path.Combine(Path.GetTempPath(),
        $"appa-build-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
    Directory.CreateDirectory(buildDir);
    var total = Stopwatch.StartNew();

    try
    {
        Spin.Step("Prepared build workspace", () => CopyDirectory(AppaPaths.TemplateDir, buildDir));

        string kernelSrcDir = Path.Combine(buildDir, "src", "kernel");
        Directory.CreateDirectory(kernelSrcDir);
        WriteOutputs(output, kernelSrcDir);

        string targetsDir = Path.Combine(buildDir, "targets", "x86_64");
        if (!File.Exists(Path.Combine(targetsDir, "linker.ld")))
            Fail("Template is missing targets/x86_64/linker.ld.");

        var srcDir = Path.Combine(buildDir, "src");
        var objDir = Path.Combine(buildDir, "build");
        var distDir = Path.Combine(buildDir, "dist", "x86_64");
        var isoDir = Path.Combine(buildDir, "targets", "x86_64", "iso");
        Directory.CreateDirectory(objDir);
        Directory.CreateDirectory(distDir);

        var cFiles = Directory.GetFiles(srcDir, "*.c", SearchOption.AllDirectories).ToList();
        var asmFiles = Directory.GetFiles(srcDir, "*.S", SearchOption.AllDirectories).ToList();
        var objFiles = CompileAll(cFiles, asmFiles, srcDir, objDir, manifest.Mode, defines);

        string kernelBin = Path.Combine(distDir, "kernel.bin");
        LinkKernel(objFiles, kernelBin, targetsDir);

        string isoPath = MakeIso(kernelBin, isoDir, distDir, buildDir);

        string projectBuildDir = Path.Combine(projectRoot, "build");
        Directory.CreateDirectory(projectBuildDir);
        string outIso = Path.Combine(projectBuildDir, Path.GetFileName(isoPath));
        File.Copy(isoPath, outIso, true);
        Out.Note(capsNote);
        Console.WriteLine();
        Console.WriteLine($"{C.BOLD}Finished{C.NC} in {Spin.Fmt(total.Elapsed)} {C.DIM}→{C.NC} {outIso}");

        if (doRun)
        {
            string artifactsDir = Path.Combine(projectRoot, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            RunQemu(outIso, artifactsDir, headless, timeout);
        }
    }
    finally { try { Directory.Delete(buildDir, true); } catch { } }
}

// A translation unit is userspace iff it is ulibc or the emitted user process file.
/// <summary>
/// Returns true if the given translation unit path belongs to the userspace
/// realm rather than the kernel.
/// </summary>
static bool IsUserspace(string rel) =>
    rel.StartsWith("ulibc/") || rel == "kernel/uproc.c";

/// <summary>
/// Compiles all C and assembly files in parallel, reporting in-place progress.
/// On the first failure, stops scheduling new jobs and prints the failure block.
/// </summary>
static List<string> CompileAll(List<string> cFiles, List<string> asmFiles,
                               string srcDir, string objDir, Mode mode, List<string> defines)
{
    var modeFlags = GatosFlags.For(mode);
    bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    var jobs = new List<(string src, string obj, string[] flags)>();

    foreach (var src in cFiles)
    {
        string rel = Path.GetRelativePath(srcDir, src).Replace('\\', '/');
        string obj = Path.Combine(objDir, rel.Replace('/', '_') + ".o");
        var cflags = new List<string>(GatosFlags.Common) { $"-I{srcDir}" };
        cflags.AddRange(modeFlags);
        cflags.AddRange(defines);
        if (IsUserspace(rel))
            cflags.Add("-ffast-math");
        else
        {
            if (!isMac) cflags.Add("-flto");
            if (GatosFlags.InterruptPath.Contains(rel))
                cflags.AddRange(GatosFlags.FpuRestrictions);
        }
        jobs.Add((src, obj, cflags.ToArray()));
    }

    foreach (var src in asmFiles)
    {
        string rel = Path.GetRelativePath(srcDir, src);
        string obj = Path.Combine(objDir, rel.Replace(Path.DirectorySeparatorChar, '_') + ".o");
        var asmFlags = new List<string> { $"-I{srcDir}", "-D__ASSEMBLER__" };
        asmFlags.AddRange(defines);
        jobs.Add((src, obj, asmFlags.ToArray()));
    }

    (string Name, string Stderr)? failure = null;
    object gate = new();
    int completed = 0, total = jobs.Count;
    var sw = Stopwatch.StartNew();
    bool tty = !Console.IsOutputRedirected;
    Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
        (job, state) =>
        {
            if (failure != null) { state.Stop(); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(job.obj)!);
            var result = Exec(AppaPaths.Gcc(), $"-c {string.Join(' ', job.flags.Select(f => $"\"{f}\""))} \"{job.src}\" -o \"{job.obj}\"", null, silent: true, capture: true);
            string name = Path.GetFileName(job.src);
            lock (gate)
            {
                if (failure != null) { state.Stop(); return; }
                if (result.ExitCode != 0)
                {
                    failure = (name, result.Stderr.TrimEnd());
                    state.Stop();
                }
                else
                {
                    completed++;
                    if (tty) Out.Redraw($"  {C.DIM}⠿ Compiling [{completed}/{total}] {name}{C.NC}");
                }
            }
        });

    if (failure != null)
    {
        if (tty) Out.ClearRedraw();
        Console.Error.WriteLine($"{C.RED}failed:{C.NC} {failure.Value.Name}");
        Console.Error.WriteLine(failure.Value.Stderr);
        Environment.Exit(1);
    }

    if (tty) Out.ClearRedraw();
    Spin.Done($"Compiled {total} files", sw.Elapsed);
    return jobs.Select(j => j.obj).ToList();
}

/// <summary>
/// Links all object files into a kernel.bin using the cross-gcc linker script.
/// macOS links with ld directly (no LTO); every other host links through cross-gcc with LTO.
/// </summary>
static void LinkKernel(List<string> objFiles, string kernelBin, string targetsDir)
{
    string linkerScript = Path.Combine(targetsDir, "linker.ld");
    string objList = string.Join(' ', objFiles.Select(o => $"\"{o}\""));
    var sw = Stopwatch.StartNew();

    var r = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? Exec(AppaPaths.Gcc("x86_64-elf-ld"),
            $"-n -nostdlib --gc-sections -T\"{linkerScript}\" --no-relax -g -o \"{kernelBin}\" {objList}",
            null, capture: true, spinner: "Linking kernel.bin")
        : Exec(AppaPaths.Gcc(),
            $"-nostdlib -flto -g -Wl,-n,--gc-sections,--no-relax,-T\"{linkerScript}\" -o \"{kernelBin}\" {objList}",
            null, capture: true, spinner: "Linking kernel.bin");
    if (r.ExitCode != 0) { Log.Error($"Link failed:\n{r.Stderr}"); Environment.Exit(1); }

    Exec(AppaPaths.Gcc("x86_64-elf-strip"), $"\"{kernelBin}\"", null, capture: true);
    Spin.Done("Linked kernel.bin", sw.Elapsed);
}

/// <summary>
/// Creates a bootable ISO from a kernel.bin using grub-mkstandalone and grub-mkrescue.
/// Returns the path to the created ISO.
/// </summary>
static string MakeIso(string kernelBin, string isoDir, string distDir, string buildDir)
{
    string bootDir = Path.Combine(isoDir, "boot");
    string uefiDir = Path.Combine(isoDir, "EFI", "BOOT");
    string grubCfg = Path.Combine(isoDir, "boot", "grub", "grub.cfg");
    Directory.CreateDirectory(bootDir);
    Directory.CreateDirectory(uefiDir);
    var sw = Stopwatch.StartNew();

    File.Copy(kernelBin, Path.Combine(bootDir, "kernel.bin"), true);

    string uefiGrub = Path.Combine(uefiDir, "BOOTX64.EFI");
    var r1 = Exec(AppaPaths.GrubTool("grub-mkstandalone"),
        $"--directory=\"{Path.Combine(AppaPaths.GrubDir, "x86_64-efi")}\" " +
        $"--format=x86_64-efi --output=\"{uefiGrub}\" --locales= --fonts= " +
        $"\"boot/grub/grub.cfg={grubCfg}\"", null, capture: true, spinner: "Creating ISO image");
    if (r1.ExitCode != 0) { Log.Error($"grub-mkstandalone failed:\n{r1.Stderr}"); Environment.Exit(1); }

    string isoOut = Path.Combine(distDir, "GatOS.iso");
    bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    string mkrescueArgs = isWin
        ? $"-d \"{AppaPaths.GrubDir}\" -o \"{isoOut}\" \"{isoDir}\""
        : $"--xorriso=\"{AppaPaths.XorrisoExe}\" --fonts=unicode --themes= -o \"{isoOut}\" \"{isoDir}\"";

    var r2 = Exec(AppaPaths.GrubTool("grub-mkrescue"), mkrescueArgs,
        AppaPaths.GrubDir, capture: true, spinner: "Creating ISO image");
    if (r2.ExitCode != 0) { Log.Error($"grub-mkrescue failed:\n{r2.Stderr}"); Environment.Exit(1); }

    Spin.Done("Created ISO image", sw.Elapsed);
    return isoOut;
}

/// <summary>
/// Launches QEMU with the given ISO and waits for it to exit, optionally with a timeout.
/// </summary>
static void RunQemu(string isoPath, string artifactsDir, bool headless, int? timeout)
{
    Console.WriteLine($"Running QEMU [{(headless ? "headless" : "GUI")}]...");
    string debugLog = Path.Combine(artifactsDir, "debug.log");
    string userDebugLog = Path.Combine(artifactsDir, "user-debug.log");
    var qemuArgs = new System.Text.StringBuilder();
    qemuArgs.Append($"-cdrom \"{isoPath}\"");
    // 3 serial ports: COM1 (mon:stdio - boot markers + serial output), COM2 (GatOS debug.log),
    // COM3 (userspace debug channel).
    qemuArgs.Append($" -serial mon:stdio");
    qemuArgs.Append($" -serial \"file:{debugLog}\"");
    qemuArgs.Append($" -serial \"file:{userDebugLog}\"");
    qemuArgs.Append($" -cpu kvm64,+smep,+smap");
    if (headless) qemuArgs.Append(" -nographic");

    string exe = AppaPaths.QemuExe;
    string finalArgs = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                       exe.EndsWith(".AppImage")
        ? $"qemu-system-x86_64 {qemuArgs}"
        : qemuArgs.ToString();

    var psi = new ProcessStartInfo(exe, finalArgs)
    {
        UseShellExecute = false, RedirectStandardOutput = false,
        RedirectStandardError = false
    };
    using var proc = Process.Start(psi)!;
    if (timeout.HasValue)
    {
        bool exited = proc.WaitForExit(timeout.Value * 1000);
        // Linux AppImage launcher forks qemu-system-x86_64 as a child via FUSE/dwarfs;
        // killing just the launcher PID leaves the child running.
        if (!exited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        Log.Info("QEMU session ended.");
    }
    else proc.WaitForExit();
}

#endregion

#region appa setup / appa update

/// <summary>
/// Downloads and installs (or re-installs) the GatOS toolchain, libgata, template, and appa binary.
/// </summary>
static void RunSetup(bool isUpdate)
{
    Log.Info(isUpdate
        ? "Updating appa toolchain, libgata, and template (overwriting existing)..."
        : "Setting up appa toolchain and resources...");
    Log.Info($"Installation directory: {AppaPaths.Root}");

    // Re-running setup re-downloads everything. If already installed, confirm first
    // (interactive only; `update` is always intentional).
    if (!isUpdate && Directory.Exists(AppaPaths.ToolchainDir) && !Console.IsInputRedirected)
    {
        Console.Write($"{C.YELLOW}appa is already installed at {AppaPaths.Root}. Re-download and overwrite? [y/N]: {C.NC}");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
        { Log.Info("Setup cancelled - existing install left untouched."); return; }
    }

    bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // Ask about PATH first (setup only, interactive only). Putting appa on PATH is a
    // privileged, system-wide change, so we decide up front: if the user wants it but
    // we're not elevated, tell them to re-run with privileges and continue without it.
    bool wantsPath = false;
    if (!isUpdate && !Console.IsInputRedirected)
    {
        Console.Write($"{C.CYAN}Add appa to your PATH so you can run it from anywhere? [y/N]: {C.NC}");
        wantsPath = Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
        if (wantsPath && !Environment.IsPrivilegedProcess)
        {
            Log.Warn("Adding appa to PATH needs elevated privileges.");
            Log.Info(isWin
                ? "Re-run 'appa setup' from an Administrator terminal."
                : "Re-run 'sudo appa setup'.");
            Environment.Exit(1);
        }
    }

    Directory.CreateDirectory(AppaPaths.ToolchainDir);
    Directory.CreateDirectory(AppaPaths.LibgataDir);
    Directory.CreateDirectory(AppaPaths.TemplateDir);
    Directory.CreateDirectory(AppaPaths.BinDir);

    string tcZip = Path.Combine(Path.GetTempPath(), "appa_tc.zip");
    DownloadWithProgress(Urls.Toolchain(), tcZip, "toolchain");
    Log.Step("Extracting toolchain...");
    System.IO.Compression.ZipFile.ExtractToDirectory(tcZip, AppaPaths.ToolchainDir, true);
    File.Delete(tcZip);

    // The libgata zip carries two sibling folders: libgata/ (stdlib) and envs/ (env
    // templates for `appa init`), so it extracts into the appa root.
    string libZip = Path.Combine(Path.GetTempPath(), "appa_libgata.zip");
    DownloadWithProgress(Urls.Libgata, libZip, "libgata");
    Log.Step("Extracting libgata...");
    System.IO.Compression.ZipFile.ExtractToDirectory(libZip, AppaPaths.Root, true);
    File.Delete(libZip);

    string tmplZip = Path.Combine(Path.GetTempPath(), "appa_template.zip");
    DownloadWithProgress(Urls.Template, tmplZip, "GatOS template");
    Log.Step("Extracting GatOS template...");
    ExtractTemplate(tmplZip, AppaPaths.TemplateDir);
    File.Delete(tmplZip);

    if (!isWin)
    {
        Log.Step("Setting executable permissions...");
        Exec("chmod", $"-R +x \"{AppaPaths.PlatformToolchain}\"", null, silent: true);
    }

    if (isUpdate)
        UpdateAppaBinary(isWin, isMac);
    else
        InstallSelf(isWin);

    if (wantsPath && Environment.IsPrivilegedProcess)
        AddToPath(isWin);

    Log.Ok(isUpdate
        ? "Update complete. Toolchain, libgata, template, and appa are now up to date."
        : "Setup complete. Run 'appa init <project>' to create a new project.");
}

/// <summary>
/// Adds the appa bin directory to the system PATH.
/// Unix creates a symlink in /usr/local/bin; Windows appends to the machine PATH variable.
/// Requires elevated privileges (checked by caller).
/// </summary>
static void AddToPath(bool isWin)
{
    try
    {
        if (isWin)
        {
            string bin = AppaPaths.BinDir;
            string cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            bool present = cur.Split(';', StringSplitOptions.RemoveEmptyEntries)
                              .Any(p => string.Equals(p.TrimEnd('\\'), bin.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (present) { Log.Info("appa's bin directory is already on PATH."); return; }
            Environment.SetEnvironmentVariable("PATH", cur.TrimEnd(';') + ";" + bin, EnvironmentVariableTarget.Machine);
            Log.Ok($"Added {bin} to the system PATH. Open a new terminal for it to take effect.");
        }
        else
        {
            const string link = "/usr/local/bin/appa";
            var r = Exec("ln", $"-sf \"{AppaPaths.AppaBin}\" \"{link}\"", null, silent: true, capture: true);
            if (r.ExitCode == 0)
                Log.Ok($"Linked {link} → {AppaPaths.AppaBin}. 'appa' is now on your PATH.");
            else
                Log.Warn($"Could not create symlink {link}: {r.Stderr.Trim()}");
        }
    }
    catch (Exception ex) { Log.Warn($"Could not add appa to PATH: {ex.Message}"); }
}

/// <summary>
/// Copies the currently-running appa binary into the bin dir (used by `appa setup`).
/// </summary>
static void InstallSelf(bool isWin)
{
    string self = Environment.ProcessPath ?? "";
    if (string.IsNullOrEmpty(self) || !File.Exists(self))
    { Log.Warn("Could not locate the running appa binary to install."); return; }

    string target = AppaPaths.AppaBin;
    if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        return;

    try
    {
        Log.Step("Installing appa binary...");
        File.Copy(self, target, true);
        if (!isWin) Exec("chmod", $"+x \"{target}\"", null, silent: true);
        Log.Info($"appa installed to {target}");
    }
    catch (Exception ex) { Log.Warn($"Could not install appa binary: {ex.Message}"); }
}

/// <summary>
/// Downloads the latest appa binary and swaps it in after this process exits.
/// The replacement is deferred to a detached process because the installed binary
/// may be the one currently running.
/// </summary>
static void UpdateAppaBinary(bool isWin, bool isMac)
{
    string target = AppaPaths.AppaBin;
    string newBin = Path.Combine(Path.GetTempPath(), isWin ? "appa_new.exe" : "appa_new");

    try { DownloadWithProgress(Urls.AppaBinary(), newBin, "appa"); }
    catch (Exception ex) { Log.Warn($"Could not download new appa binary: {ex.Message}"); return; }

    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

    try
    {
        if (isWin)
        {
            var psi = new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 2 /nobreak >nul & move /Y \"{newBin}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true };
            Process.Start(psi);
        }
        else
        {
            string script = $"sleep 2; mv -f '{newBin}' '{target}'; chmod +x '{target}'; ";
            if (isMac) script += $"xattr -d com.apple.quarantine '{target}' 2>/dev/null; ";
            script += "true";
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            Process.Start(psi);
        }
        Log.Info("Downloaded the latest appa; it will replace the installed binary momentarily.");
    }
    catch (Exception ex) { Log.Warn($"Could not schedule appa self-update: {ex.Message}"); }
}

/// <summary>
/// Downloads a URL to a local file, printing a progress bar or byte counter while downloading.
/// </summary>
static void DownloadWithProgress(string url, string dest, string name)
{
    using var client = new System.Net.Http.HttpClient();
    client.Timeout = TimeSpan.FromMinutes(10);
    using var response = client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).Result;
    response.EnsureSuccessStatusCode();
    long? total = response.Content.Headers.ContentLength;
    using var stream = response.Content.ReadAsStream();
    using var outFile = File.Create(dest);
    var buffer = new byte[81920];
    const string spin = @"|/-\";
    long downloaded = 0;
    int read, ticks = 0;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
    {
        outFile.Write(buffer, 0, read);
        downloaded += read;
        // A known, positive Content-Length draws a percentage bar; an absent or
        // zero-length total (e.g. GitHub archive endpoints) shows a live byte counter.
        if (total is > 0)
        {
            int pct = (int)(downloaded * 100 / total.Value);
            int filled = pct * 40 / 100;
            string bar = new string('=', filled) + new string(' ', 40 - filled);
            Out.Redraw($"{name}  |{bar}| {pct}% ({downloaded/1048576.0:F1}/{total.Value/1048576.0:F1} MB)");
        }
        else
            Out.Redraw($"{name}  {spin[ticks++ % 4]} {downloaded/1048576.0:F1} MB");
    }
    Console.WriteLine();
}

/// <summary>
/// Extracts the GatOS template zip into destDir, flattening GitHub's single wrapper
/// folder and keeping only its top-level directories (src/, targets/).
/// </summary>
static void ExtractTemplate(string zipPath, string destDir)
{
    string staging = Path.Combine(Path.GetTempPath(), $"appa-tmpl-stage-{Environment.ProcessId}");
    if (Directory.Exists(staging)) Directory.Delete(staging, true);
    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging);

    var entries = Directory.GetFileSystemEntries(staging);
    string root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;

    if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
    Directory.CreateDirectory(destDir);
    foreach (var dir in Directory.GetDirectories(root))
    {
        string dst = Path.Combine(destDir, Path.GetFileName(dir));
        Directory.CreateDirectory(dst);
        CopyDirectory(dir, dst);
    }
    Directory.Delete(staging, true);
}

#endregion

// Type declarations must follow all top-level statements.

// Files written by `appa init`.
static class Templates
{
    /// <summary>
    /// Returns the .gconf file content for a new GatOS project.
    /// </summary>
    public static string GatOSGconf(string name) => $"""
<!--
  TargetBackend:        GatOS | Hosted
  BuildMode:            Debug | Release
  OutputType:           Framebuffer | Serial
  KeyboardSupport:      Default (PS/2) | External (+ USB) | Hotplug (+ hotplug)
  CapabilityDiscovery:  On (infer mem/input/threads from the program, default)
                        | Off (assume all three - escape valve for a native blind spot)
-->
<appa>
    <ProjectName>{name}</ProjectName>
    <TargetBackend>GatOS</TargetBackend>
    <BuildMode>Debug</BuildMode>
    <OutputType>Framebuffer</OutputType>
    <KeyboardSupport>Default</KeyboardSupport>
    <CapabilityDiscovery>On</CapabilityDiscovery>
</appa>

""";

    /// <summary>
    /// Returns the src/main.g starter file content for a new GatOS project.
    /// </summary>
    public static string GatOSMain(string name) => $$"""
import LibGata;
import Collections;

kernel {
    entry func Main() {
        Misc.PrintBanner();
        Console.PrintLine("Hello from {{name}}!");
    }
}

user {
    foreground process App {
        thread Main {
            entry func Run() {
                Console.PrintLine("Hello from userspace!");
            }
        }
    }
}

""";
}

// The GatOS gcc flag set. appa owns these - a .gconf carries none of them. This must
// match the GatOS build.py exactly: kernel code uses the FPU freely (lazy save/restore
// handles it), and SSE is disabled ONLY in the fixed set of files whose code runs from
// interrupt context (where touching XMM would corrupt the interrupted thread's state).
static class GatosFlags
{
    public static readonly string[] Common =
        ["-m64", "-ffreestanding", "-nostdlib", "-fno-pic", "-mcmodel=kernel",
         "-mno-red-zone", "-ffunction-sections", "-fdata-sections"];

    // Applied ONLY to the interrupt-path files below - never to ordinary kernel code.
    public static readonly string[] FpuRestrictions =
        ["-mno-sse", "-mno-sse2", "-mno-mmx", "-mno-80387"];

    public static readonly HashSet<string> InterruptPath = new(StringComparer.Ordinal)
    {
        "arch/x86_64/cpu/interrupts.c",
        "kernel/sys/scheduler.c",
        "kernel/sys/timers.c",
        "kernel/drivers/keyboard.c",
        "kernel/drivers/xhci.c",
        "tests/test_timers.c",
        "kernel/memory/vmm.c",
        "kernel/memory/pmm.c",
        "klibc/avl.c",
    };

    /// <summary>
    /// Returns the optimization flags for the given build mode.
    /// </summary>
    public static string[] For(Mode mode) => mode == Mode.Release
        ? ["-O3", "-fpredictive-commoning", "-fstrict-aliasing",
           "-fno-delete-null-pointer-checks", "-fomit-frame-pointer", "-fno-stack-protector"]
        : [];
}

// Plain, flush-left narration for setup/update/init.
static class Log
{
    /// <summary>
    /// Prints an informational message.
    /// </summary>
    public static void Info(string m) => Console.WriteLine(m);

    /// <summary>
    /// Prints a success message with a green checkmark.
    /// </summary>
    public static void Ok(string m) => Console.WriteLine($"{C.GREEN}✓{C.NC} {m}");

    /// <summary>
    /// Prints a step message in cyan.
    /// </summary>
    public static void Step(string m) => Console.WriteLine($"{C.CYAN}{m}{C.NC}");

    /// <summary>
    /// Prints a warning message.
    /// </summary>
    public static void Warn(string m) => Console.WriteLine($"{C.YELLOW}warning:{C.NC} {m}");

    /// <summary>
    /// Prints an error message and optional hint to stderr.
    /// </summary>
    public static void Error(string m, string? hint = null)
    {
        Console.Error.WriteLine($"{C.RED}error:{C.NC} {m}");
        if (hint != null) Console.Error.WriteLine($"  hint: {hint}");
    }
}

// Collects every `_env_*` floor bind referenced anywhere in the lowered IR.
sealed class EnvProbe : IrRewriter
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
    /// Collects _env_dbg and _env_panic from debug and panic statements.
    /// </summary>
    protected override IrStmt RewriteStmt(IrStmt s)
    {
        if (s is IrDebug) Refs.Add("_env_dbg");
        if (s is IrPanic) Refs.Add("_env_panic");
        return base.RewriteStmt(s);
    }

    /// <summary>
    /// Runs the probe over the whole module and populates Refs.
    /// </summary>
    public void Run(IrModule m)
    {
        foreach (var c in m.Classes)
        {
            foreach (var mm in c.Methods) if (mm.Body != null) RewriteStmt(mm.Body);
            foreach (var o in c.Operators) if (o.Body != null) RewriteStmt(o.Body);
        }
        foreach (var f in m.FreeFunctions) if (f.Body != null) RewriteStmt(f.Body);
    }
}
