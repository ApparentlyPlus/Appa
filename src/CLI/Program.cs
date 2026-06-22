using Appa;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

#region Entry point

if (args.Length == 0) { PrintHelp(); Environment.Exit(1); }
try
{
    switch (args[0])
    {
        case "setup":  RunSetup(isUpdate: false); break;
        case "update": RunSetup(isUpdate: true);  break;
        case "init":   RunInit(args[1..]);         break;
        case "build":  RunBuild(args[1..]);        break;
        case "--help":
        case "-h":     PrintHelp();               break;
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
        ("env.g",         "platform environment (@environment)"),
        ("src/main.g",    "entry point"),
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
static void RunBuild(string[] args)
{
    string? manifestArg = null, envOverride = null, entryOverride = null, stdlibOverride = null;
    bool warnAsError = false, doRun = false, headless = false, pureTranspile = false;
    int? timeout = null;

    for (int i = 0; i < args.Length; i++)
        switch (args[i])
        {
            case "--env"     when i+1 < args.Length: envOverride    = args[++i]; break;
            case "--entry"   when i+1 < args.Length: entryOverride  = args[++i]; break;
            case "--stdlib"  when i+1 < args.Length: stdlibOverride = args[++i]; break;
            case "--werror":         warnAsError   = true; break;
            case "--run":            doRun         = true; break;
            case "--headless":       headless      = true; break;
            case "--pure-transpile": pureTranspile = true; break;
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
                manifestArg == null           ? ManifestReader.Discover(Directory.GetCurrentDirectory())
              : Directory.Exists(manifestArg) ? ManifestReader.Discover(manifestArg)
              :                                 manifestArg;
            if (manifestPath != null) manifest = ManifestReader.Load(manifestPath);
        }
        catch (ManifestError e) { Fail(e.Message); }
        if (manifest == null)
            Fail("no <project>.gconf found - run 'appa init', or use --pure-transpile --env <file> --entry <file>");
    }
    else if (manifestArg != null)
        Log.Warn($"project argument '{manifestArg}' is ignored with --pure-transpile --env --entry (loose-file mode discovers nothing from a project)");

    string? envPath   = envOverride   ?? (manifest != null ? DiscoverEnv(manifest.Dir)   : null);
    string? entryPath = entryOverride ?? (manifest != null ? DiscoverEntry(manifest.Dir) : null);
    if (envPath   == null) Fail("no environment found - mark one project file @environment, or pass --env");
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
    var (module, _, caps) = BuildModule(programs, visible, manifest?.Mode ?? Mode.Debug, diag);

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
        Console.WriteLine();
        Console.WriteLine($"{C.BOLD}Finished{C.NC} {C.DIM}→{C.NC} {outDir}{Path.DirectorySeparatorChar}");
        foreach (var f in output) Out.Child($"{C.DIM}{Path.Combine("transpilation", f.Name)}{C.NC}");
        return;
    }

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
/// Returns the entry point path (src/main.g convention), or null if it doesn't exist.
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
                             DiagnosticBag diag, Span span)
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
  --run / --headless / --timeout=<Xs>   Launch QEMU after a GatOS image build

  A project build auto-discovers its environment (the @environment file in the
  project dir) and entry (src/main.g) - no --env/--entry needed.

{{C.BLUE}}Examples:{{C.NC}}
  appa setup
  appa init myos && cd myos && appa build --run
  appa build --pure-transpile --env env.g --entry src/main.g
""");

#endregion

#region Templates and flags

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

#endregion

#region Log

// Plain, flush-left narration for setup/update/init.
static class Log
{
    /// <summary>Prints an informational message.</summary>
    public static void Info(string m)  => Console.WriteLine(m);
    /// <summary>Prints a success message with a green checkmark.</summary>
    public static void Ok(string m)    => Console.WriteLine($"{C.GREEN}✓{C.NC} {m}");
    /// <summary>Prints a step message in cyan.</summary>
    public static void Step(string m)  => Console.WriteLine($"{C.CYAN}{m}{C.NC}");
    /// <summary>Prints a warning message.</summary>
    public static void Warn(string m)  => Console.WriteLine($"{C.YELLOW}warning:{C.NC} {m}");
    /// <summary>Prints an error message and optional hint to stderr.</summary>
    public static void Error(string m, string? hint = null)
    {
        Console.Error.WriteLine($"{C.RED}error:{C.NC} {m}");
        if (hint != null) Console.Error.WriteLine($"  hint: {hint}");
    }
}

#endregion

#region Build pipeline

// The semantic pipeline: AST programs to a fully lowered IrModule, ready to print.
// Each stage is a total transform - monomorphize generics, collect declarations,
// resolve+typecheck into typed IR, then the IR-to-IR lowering passes that peel ARC and
// desugaring out of the emitter. Front-end orchestration stays in RunBuild.
static (IrModule Module, IReadOnlyDictionary<string, string> Sourcemap, CapabilityScan Caps) BuildModule(
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
static (List<(string path, Appa.Program prog)> programs, List<string> attempted,
        Dictionary<string, List<string>> imports, DiagnosticBag diag)
    Transpile(List<string> inputFiles, string projectRoot, string libgataDir)
{
    var sources   = new SourceSet();
    var diag      = new DiagnosticBag(sources);
    var ordered   = new List<(string path, Appa.Program prog)>();
    var attempted = new List<string>();
    var imports   = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var visited   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void Resolve(string path, string? from, Span fromSpan)
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

    foreach (var f in inputFiles) Resolve(Path.GetFullPath(f), null, Span.None);
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
        diag.Error(Codes.File, "", Span.None, "no @environment file in the build");
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
            diag.Error(Codes.MissingFloorBind, "<environment>", Span.None,
                $"the active environment's @preamble provides no definition of '{name}'; add one (the environment file, not your Gata source, is incomplete)");
}

/// <summary>
/// Validates that the build contains exactly one kernel block with exactly one entry func.
/// </summary>
static void ValidateStructure(List<(string path, Appa.Program prog)> programs, DiagnosticBag diag)
{
    if (diag.HasErrors) return;

    var kernelBlocks = new List<(string file, Span span)>();
    var entryFuncs   = new List<(string file, Span span)>();

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
        diag.Error(Codes.MissingEntryPoint, "", Span.None, "no 'kernel { }' entry point found in any .g file");
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
    var low   = new Dictionary<string, int>();
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
        var errors    = fileDiags.Where(d => d.Severity == Severity.Error).ToList();
        var warnings  = fileDiags.Where(d => d.Severity == Severity.Warning).ToList();

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
        UseShellExecute        = false,
        RedirectStandardOutput = capture,
        RedirectStandardError  = capture,
        WorkingDirectory       = workDir ?? ""
    };

    using var proc = Process.Start(psi)!;
    var outTask = capture ? proc.StandardOutput.ReadToEndAsync() : null;
    var errTask = capture ? proc.StandardError.ReadToEndAsync() : null;

    if (spinner != null) Spin.WhileRunning(proc, spinner);
    proc.WaitForExit();
    return (proc.ExitCode, outTask?.Result ?? "", errTask?.Result ?? "");
}

#endregion

#region EnvProbe

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

#endregion

// Stubs - implemented in subsequent commits.
static List<string> CapabilityDefines(CapabilityScan caps, Manifest m)
    => throw new NotImplementedException();

static string CapabilitiesNote(CapabilityScan caps, Manifest m)
    => throw new NotImplementedException();

static void BuildGatOSImage(IReadOnlyList<OutputFile> output, Manifest manifest,
    string projectRoot, List<string> defines, string capsNote,
    bool doRun, bool headless, int? timeout)
    => throw new NotImplementedException();

static void RunSetup(bool isUpdate) => throw new NotImplementedException();
