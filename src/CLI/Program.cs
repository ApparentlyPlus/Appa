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
    var (module, caps) = BuildModule(programs, visible, manifest?.Mode ?? Mode.Debug, diag);

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

// Forward declarations: these are added in subsequent commits.
static (IrModule Module, CapabilityScan Caps) BuildModule(
    List<(string path, Appa.Program prog)> programs,
    Dictionary<string, HashSet<string>> visible, Mode mode, DiagnosticBag diag)
    => throw new NotImplementedException();

static (List<(string path, Appa.Program prog)> programs, List<string> attempted,
        Dictionary<string, List<string>> imports, DiagnosticBag diag)
    Transpile(List<string> inputFiles, string projectRoot, string libgataDir)
    => throw new NotImplementedException();

static Dictionary<string, HashSet<string>> VisibleModules(Dictionary<string, List<string>> imports)
    => throw new NotImplementedException();

static void ValidateEnvironment(List<(string path, Appa.Program prog)> programs, DiagnosticBag diag)
    => throw new NotImplementedException();

static void ValidateFloor(IrModule module, DiagnosticBag diag)
    => throw new NotImplementedException();

static void ValidateStructure(List<(string path, Appa.Program prog)> programs, DiagnosticBag diag)
    => throw new NotImplementedException();

static void WarnReferenceCycles(IrModule module) => throw new NotImplementedException();

static void ReportGataFiles(List<string> attempted, DiagnosticBag diag, bool warnAsError)
    => throw new NotImplementedException();

static List<string> CapabilityDefines(CapabilityScan caps, Manifest m)
    => throw new NotImplementedException();

static string CapabilitiesNote(CapabilityScan caps, Manifest m)
    => throw new NotImplementedException();

static void BuildGatOSImage(IReadOnlyList<OutputFile> output, Manifest manifest,
    string projectRoot, List<string> defines, string capsNote,
    bool doRun, bool headless, int? timeout)
    => throw new NotImplementedException();

static void RunSetup(bool isUpdate) => throw new NotImplementedException();
