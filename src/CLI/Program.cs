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
        case "setup": await Installer.RunSetup(isUpdate: false); break;
        case "update": await Installer.RunSetup(isUpdate: true); break;
        case "init": RunInit(args[1..]); break;
        case "build": RunBuild(args[1..]); break;
        case "check": RunCheck(args[1..]); break;
        case "--help":
        case "-h": PrintHelp(); break;
        case "--version":
        case "-v": Console.WriteLine($"appa {AppaVersion.Current}"); break;
        default:
            Log.Error($"Unknown command '{args[0]}'",
                Suggest.Closest(args[0], ["init", "setup", "update", "build", "check", "--help", "--version"]) is { } near
                    ? $"did you mean '{near}'?" : null);
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
/// Scaffolds a new GatOS project: a .gconf, an env.g copied from the installed environment, and a
/// starter src/main.g, then prints a short file tree.
/// </summary>
static void RunInit(string[] args)
{
    string name = args.ElementAtOrDefault(0) ?? "myproject";
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
        Cli.Fail($"'{name}' is not a valid project name");

    string projDir = Path.GetFullPath(name);
    if (Directory.Exists(projDir)) Cli.Fail($"directory '{name}' already exists");

    string envSrc = Path.Combine(AppaPaths.EnvsDir, "env.GatOS.g");
    if (!File.Exists(envSrc))
        Cli.Fail("GatOS environment not found. Run 'appa setup' first.");

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
    Console.WriteLine($"{C.CYAN}Common next steps:{C.NC}");
    Console.WriteLine($"  cd {name}");
    Console.WriteLine($"  appa build");
    Console.WriteLine();
}

#endregion

#region appa build

/// <summary>
/// Parses build arguments, transpiles and lowers the project, then dispatches to the GatOS image
/// builder or the hosted pure-transpile path.
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
                if (args[i].StartsWith("--timeout=")) timeout = Cli.ParseTimeout(args[i]["--timeout=".Length..]);
                else if (args[i].StartsWith("--")) Cli.Fail($"unknown option '{args[i]}'");
                else manifestArg = args[i];
                break;
        }

    bool looseTranspile = pureTranspile && envOverride != null && entryOverride != null;
    var (manifest, envPath, entryPath, projectRoot, stdlibDir) = Cli.ResolveInputs(
        manifestArg, envOverride, entryOverride, stdlibOverride, looseTranspile,
        "--pure-transpile --env <file> --entry <file>", "--pure-transpile --env --entry");

    if (manifest != null)
        Console.WriteLine($"{C.BOLD}Building{C.NC} {manifest.ProjectName} {C.DIM}({manifest.Target}, {manifest.Mode.ToString().ToLowerInvariant()}){C.NC}");
    else
        Console.WriteLine($"{C.BOLD}Building{C.NC} {C.DIM}(--pure-transpile){C.NC}");
    Console.WriteLine();

    var (module, sourcemap, caps, diag) = RunFrontEnd(envPath, entryPath, projectRoot, stdlibDir, manifest, warnAsError);

    var output = Layout.Compose(new Emitter(module, diag).Build(), module.Symbols);

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
        Cli.WriteOutputs(output, outDir);
        if (emitSourcemap) Cli.WriteSourcemap(sourcemap, outDir);
        Console.WriteLine();
        Console.WriteLine($"{C.BOLD}Finished{C.NC} {C.DIM}→{C.NC} {outDir}{Path.DirectorySeparatorChar}");
        foreach (var f in output) Out.Child($"{C.DIM}{Path.Combine("transpilation", f.Name)}{C.NC}");
        return;
    }

    if (emitSourcemap) Cli.WriteSourcemap(sourcemap, projectRoot);
    var defines = Toolchain.CapabilityDefines(caps, manifest!);
    Toolchain.BuildGatOSImage(output, manifest!, projectRoot, defines, Toolchain.CapabilitiesNote(caps, manifest!), doRun, headless, timeout);
}

#endregion

#region appa check

/// <summary>
/// Parses check arguments and runs the compiler front end only, reporting diagnostics without ever
/// reaching emission.
/// </summary>
static void RunCheck(string[] args)
{
    string? manifestArg = null, envOverride = null, entryOverride = null, stdlibOverride = null;
    bool warnAsError = false;

    for (int i = 0; i < args.Length; i++)
        switch (args[i])
        {
            case "--env" when i + 1 < args.Length: envOverride = args[++i]; break;
            case "--entry" when i + 1 < args.Length: entryOverride = args[++i]; break;
            case "--stdlib" when i + 1 < args.Length: stdlibOverride = args[++i]; break;
            case "--werror": warnAsError = true; break;
            default:
                if (args[i].StartsWith("--")) Cli.Fail($"unknown option '{args[i]}'");
                else manifestArg = args[i];
                break;
        }

    bool loose = envOverride != null && entryOverride != null;
    var (manifest, envPath, entryPath, projectRoot, stdlibDir) = Cli.ResolveInputs(
        manifestArg, envOverride, entryOverride, stdlibOverride, loose,
        "--env <file> --entry <file>", "--env --entry");

    if (manifest != null)
        Console.WriteLine($"{C.BOLD}Checking{C.NC} {manifest.ProjectName} {C.DIM}({manifest.Target}, {manifest.Mode.ToString().ToLowerInvariant()}){C.NC}");
    else
        Console.WriteLine($"{C.BOLD}Checking{C.NC} {C.DIM}(--env/--entry){C.NC}");
    Console.WriteLine();

    RunFrontEnd(envPath, entryPath, projectRoot, stdlibDir, manifest, warnAsError);
}

/// <summary>
/// Runs every front-end stage shared by `appa build` and `appa check`: parse, lower, validate the
/// environment/floor/structure, then report diagnostics.
/// </summary>
static (IrModule Module, IReadOnlyDictionary<string, string> Sourcemap, CapabilityScan Caps, DiagnosticBag Diag)
    RunFrontEnd(string envPath, string entryPath, string projectRoot, string stdlibDir,
                Manifest? manifest, bool warnAsError)
{
    var inputFiles = new List<string> { Path.GetFullPath(envPath), Path.GetFullPath(entryPath) };
    var (programs, attempted, imports, diag) = Pipeline.Transpile(inputFiles, projectRoot, stdlibDir);
    // A file that failed to load or parse leaves the name universe incomplete, so the semantic
    // passes below report names as missing that the build was supposed to have. Their findings
    // are kept only when loading actually succeeded.
    bool loaded = !diag.HasErrors;
    int afterLoad = diag.All.Count;

    var visible = Pipeline.VisibleModules(imports);
    var (module, sourcemap, caps) = Pipeline.BuildModule(programs, visible, manifest?.Mode ?? Mode.Debug, diag);

    if (!loaded)
    {
        diag.TruncateTo(afterLoad);
        Pipeline.ReportGataFiles(attempted, diag, warnAsError, stdlibDir);
        return (module, sourcemap, caps, diag);
    }

    Target target = manifest?.Target ?? (module.HasKernelRealm ? Target.GatOS : Target.Hosted);

    Pipeline.ValidateEnvironment(programs, diag);
    Pipeline.ValidateFloor(module, diag);
    Pipeline.ValidateIntrinsics(module, diag);
    Pipeline.ValidateStructure(programs, target, diag);
    if (manifest?.Target == Target.Hosted && module.HasKernelRealm)
        diag.Error(Codes.KernelBlockInHosted, "<environment>", TextSpan.None,
            "the active environment declares a kernel preamble, which is not allowed for a Hosted build");
    if (!diag.HasErrors) Pipeline.WarnReferenceCycles(module);
    Pipeline.ReportGataFiles(attempted, diag, warnAsError, stdlibDir);

    return (module, sourcemap, caps, diag);
}

#endregion


#region Help

/// <summary>
/// Prints the top-level usage text: commands, build options, and examples.
/// </summary>
static void PrintHelp() => Console.WriteLine($$"""
{{C.GREEN}}Welcome to Appa {{C.NC}} {{AppaVersion.Current}} - the Gata language compiler for GatOS

{{C.CYAN}}Usage:{{C.NC}}
  appa setup                      Install the GatOS toolchain, template, and libgata
  appa update                     Re-download and overwrite the installed GatOS bundle
  appa init [project]             Create a GatOS project
  appa build [project|.gconf]     Build the project described by its .gconf
  appa check [project|.gconf]     Lex, parse, and type-check only - reports errors, emits nothing
  appa --version / -v             Print the appa version

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

{{C.YELLOW}}Check options:{{C.NC}}
  --stdlib <dir> / --werror / --env <env.g> / --entry <file.g>   Same meaning as for build
  (no --pure-transpile needed for --env/--entry: check never emits, loose or not)

{{C.BLUE}}Examples:{{C.NC}}
  appa setup
  appa init myos && cd myos && appa build --run
  appa build --pure-transpile --env env.g --entry src/main.g
  appa check myos
  appa check --env env.g --entry src/main.g
""");

#endregion

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
import Misc;
import Console;

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
        if (hint != null) Console.Error.WriteLine($"{C.BLUE}={C.NC} {C.CYAN}help{C.NC}: {hint}");
    }
}
