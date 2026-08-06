using Appa;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

#region Entry point

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0) { PrintHelp(); return; }
try
{
    switch (args[0])
    {
        case "install":
        case "update":
        {
            var (pathPref, force) = InstallOptions(args[1..]);
            await Installer.RunSetup(isUpdate: args[0] == "update", pathPref, force);
            break;
        }
        case "new": RunNew(args[1..]); break;
        case "clean": RunClean(args[1..]); break;
        case "build": RunBuild(args[1..], doRun: false); break;
        case "run": RunBuild(args[1..], doRun: true); break;
        case "check": RunCheck(args[1..]); break;
        case "help":
        case "--help":
        case "-h": PrintHelp(); break;
        case "version":
        case "--version":
        case "-v": Console.WriteLine($"appa {AppaVersion.Current}"); break;
        default:
            Log.Error($"unknown command '{args[0]}'",
                args[0] == "setup" ? "'appa setup' is now 'appa install'"
                : Suggest.Closest(args[0], Commands()) is { } near ? $"did you mean 'appa {near}'?"
                : "run 'appa --help' for the list of commands");
            Environment.Exit(1);
            break;
    }
}
catch (Exception ex) when (args[0] is "install" or "update" && Installer.IsExpectedSetupFailure(ex))
{
    Log.Error($"{args[0]} could not complete: {ex.Message}", Installer.SetupFailureHint(ex));
    Environment.Exit(1);
}
catch (Exception ex)
{
    Log.Error($"internal compiler error: {ex.Message}");
    Console.Error.WriteLine(ex);
    Environment.Exit(1);
}

#endregion

#region appa install / appa update

/// <summary>
/// Reads the options off an install/update invocation.
/// </summary>
static (bool? PathPref, bool Force) InstallOptions(string[] args)
{
    bool? pref = null;
    bool force = false;
    foreach (var a in args)
        switch (a)
        {
            case "--with-path": pref = true; break;
            case "--no-path": pref = false; break;
            case "--force": force = true; break;
            default:
                Cli.Fail($"unknown option '{a}'", "install takes --with-path, --no-path, or --force");
                break;
        }
    return (pref, force);
}

#endregion

#region appa new

/// <summary>
/// Scaffolds a new GatOS project: a .gconf, an env.g copied from the installed environment, and a
/// starter src/main.g, then prints a short file tree.
/// </summary>
static void RunNew(string[] args)
{
    string name = args.ElementAtOrDefault(0) ?? "myproject";
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
        Cli.Fail($"'{name}' is not a valid project name");

    string projDir = Path.GetFullPath(name);
    if (Directory.Exists(projDir)) Cli.Fail($"directory '{name}' already exists");

    string envSrc = Path.Combine(AppaPaths.EnvsDir, "env.GatOS.g");
    if (!File.Exists(envSrc))
        Cli.Fail("GatOS environment not found. Run 'appa install' first.");

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

    Console.WriteLine();
    Console.WriteLine($"{C.LEAF}✓{C.NC} Created {C.BOLD}{name}{C.NC} {C.DIM}(GatOS){C.NC}");
    Console.WriteLine();
    Console.WriteLine($"{Fmt.Indent}{C.DIM}{name}/{C.NC}");
    Fmt.Table([..entries.Select((e, i) =>
        ($"{C.DIM}{(i == entries.Length - 1 ? "└─" : "├─")}{C.NC} {e.Path}", $"{C.DIM}{e.Desc}{C.NC}"))],
        Fmt.Indent);

    Fmt.Section("Next steps");
    Out.Note($"cd {name}");
    Out.Note("appa run");
    Console.WriteLine();
}

#endregion

#region appa clean

/// <summary>
/// Removes the directories a build writes into the project root - transpilation/, artifacts/, and
/// build/ - leaving sources and the .gconf untouched.
/// </summary>
static void RunClean(string[] args)
{
    string? dirArg = null;
    foreach (var a in args)
        if (a.StartsWith("--")) Cli.Fail($"unknown option '{a}'");
        else dirArg = a;

    string projectRoot = Path.GetFullPath(dirArg ?? ".");
    if (!Directory.Exists(projectRoot)) Cli.Fail($"'{dirArg}' does not exist");

    string? manifestPath = null;
    try { manifestPath = ManifestReader.Discover(projectRoot); }
    catch (ManifestError e) { Cli.Fail(e.Message); }
    if (manifestPath == null)
        Cli.Fail($"no <project>.gconf found in {projectRoot}",
                 "clean only removes build output from a project directory");

    Console.WriteLine();
    Console.WriteLine($"{C.LEAF}Cleaning{C.NC} {Path.GetFileNameWithoutExtension(manifestPath)} {C.DIM}({projectRoot}){C.NC}");
    Console.WriteLine();

    int removed = 0;
    foreach (var name in Cli.GeneratedDirs)
    {
        string path = Path.Combine(projectRoot, name);
        if (!Directory.Exists(path)) continue;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { Directory.Delete(path, true); }
        catch (Exception e) { Cli.Fail($"could not remove {name}{Path.DirectorySeparatorChar}: {e.Message}"); }
        Out.Step($"removed {name}{Path.DirectorySeparatorChar}", sw.Elapsed);
        removed++;
    }

    if (removed == 0) Out.Note($"{C.DIM}nothing to remove - the project is already clean{C.NC}");
    else
    {
        Console.WriteLine();
        Console.WriteLine($"{C.LEAF}✓{C.NC} {C.BOLD}Clean{C.NC}");
    }
    Console.WriteLine();
}

#endregion

#region appa build / appa run

/// <summary>
/// Parses build arguments, transpiles and lowers the project, then dispatches to the GatOS image
/// builder or the hosted pure-transpile path. <paramref name="doRun"/> is set by `appa run`, which
/// is `appa build` followed by launching the image in QEMU.
/// </summary>
static void RunBuild(string[] args, bool doRun)
{
    string? manifestArg = null, envOverride = null, entryOverride = null, stdlibOverride = null;
    bool warnAsError = false, headless = false, pureTranspile = false, emitSourcemap = false;
    int? timeout = null;

    for (int i = 0; i < args.Length; i++)
        switch (args[i])
        {
            case "--env" when i+1 < args.Length: envOverride = args[++i]; break;
            case "--entry" when i+1 < args.Length: entryOverride = args[++i]; break;
            case "--stdlib" when i+1 < args.Length: stdlibOverride = args[++i]; break;
            case "--werror": warnAsError = true; break;
            case "headless":
            case "--headless": headless = RunOnly(args[i]); break;
            case "--pure-transpile": pureTranspile = true; break;
            case "--emit-sourcemap": emitSourcemap = true; break;
            default:
                if (args[i].StartsWith("timeout=")) { RunOnly(args[i]); timeout = Cli.ParseTimeout(args[i]["timeout=".Length..]); }
                else if (args[i].StartsWith("--timeout=")) { RunOnly(args[i]); timeout = Cli.ParseTimeout(args[i]["--timeout=".Length..]); }
                else if (args[i].StartsWith("--")) Cli.Fail($"unknown option '{args[i]}'");
                else manifestArg = args[i];
                break;
        }

    bool RunOnly(string opt)
    {
        if (!doRun) Cli.Fail($"'{opt}' only applies to 'appa run'", "use 'appa run' to build the ISO and launch it");
        return true;
    }

    bool looseTranspile = pureTranspile && envOverride != null && entryOverride != null;
    var (manifest, envPath, entryPath, projectRoot, stdlibDir) = Cli.ResolveInputs(
        manifestArg, envOverride, entryOverride, stdlibOverride, looseTranspile,
        "--pure-transpile --env <file> --entry <file>", "--pure-transpile --env --entry");

    if (manifest != null)
        Console.WriteLine($"{C.LEAF}Building{C.NC} {manifest.ProjectName} {C.DIM}({manifest.Target}, {manifest.Mode.ToString().ToLowerInvariant()}){C.NC}");
    else
        Console.WriteLine($"{C.LEAF}Building{C.NC} {C.DIM}(--pure-transpile){C.NC}");
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
    if (!emitIso && doRun)
        Log.Warn("'appa run' only launches a GatOS image; there is nothing to boot here (this build just writes C)");
    if (!emitIso)
    {
        string outDir = Path.Combine(projectRoot, Cli.TranspileDir);
        Cli.WriteOutputs(output, outDir);
        if (emitSourcemap) Cli.WriteSourcemap(sourcemap, outDir);
        Console.WriteLine();
        Console.WriteLine($"{C.LEAF}✓{C.NC} {C.BOLD}Finished{C.NC} {C.DIM}→{C.NC} {outDir}{Path.DirectorySeparatorChar}");
        foreach (var f in output) Out.Child($"{C.DIM}{Path.Combine(Cli.TranspileDir, f.Name)}{C.NC}");
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
        Console.WriteLine($"{C.LEAF}Checking{C.NC} {manifest.ProjectName} {C.DIM}({manifest.Target}, {manifest.Mode.ToString().ToLowerInvariant()}){C.NC}");
    else
        Console.WriteLine($"{C.LEAF}Checking{C.NC} {C.DIM}(--env/--entry){C.NC}");
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
    Pipeline.ValidateStructure(programs, target, diag, parsed: true);
    if (manifest?.Target == Target.Hosted && module.HasKernelRealm)
        diag.Error(Codes.KernelBlockInHosted, "<environment>", TextSpan.None,
            "the active environment declares a kernel preamble, which is not allowed for a Hosted build");
    if (!diag.HasErrors) Pipeline.WarnReferenceCycles(module, diag, programs, stdlibDir);
    Pipeline.ReportGataFiles(attempted, diag, warnAsError, stdlibDir);

    return (module, sourcemap, caps, diag);
}

#endregion


#region Help

/// <summary>
/// Every command name, in the order the help lists them. Also what a mistyped command is matched
/// against, so the two can never drift apart.
/// </summary>
static string[] Commands() => ["install", "update", "new", "check", "build", "run", "clean"];

/// <summary>
/// Prints the top-level usage: commands, options, and examples. The text is data, laid out by Fmt
/// against the real terminal width - no line in here is wrapped or padded by hand.
/// </summary>
static void PrintHelp()
{
    Console.WriteLine();
    Console.WriteLine($"{C.LEAF}appa{C.NC} {AppaVersion.Current} {C.DIM}- the Gata language compiler for GatOS{C.NC}");

    Fmt.Section("Commands");
    Fmt.Table([
        ("appa install",              "Install the GatOS toolchain, template, and libgata"),
        ("appa update",               "Re-download the GatOS bundle and self-update appa"),
        ("appa new <name>",           "Create a GatOS project"),
        ("appa check [project]",      "Lex, parse, and type-check only - reports errors, emits nothing"),
        ("appa build [project]",      "Build the project described by its .gconf into an ISO"),
        ("appa run [project]",        "Build the ISO, then launch it in QEMU"),
        ("appa clean [project]",      $"Remove {string.Join("/, ", Cli.GeneratedDirs)}/"),
        ("appa --version / -v",       "Print the appa version"),
    ]);
    Console.WriteLine();
    Fmt.Para($"{C.DIM}A project argument is a directory or a path to its .gconf; the default is the current directory.{C.NC}");

    Fmt.Section("Install options");
    Fmt.Table([
        ("--with-path", "Add appa to PATH without asking - re-runs elevated if it has to"),
        ("--no-path",   "Install without touching PATH, and without asking"),
        ("--force",     "Overwrite an existing install without confirming"),
    ]);

    Fmt.Section("Run options", "(on top of every build option below)");
    Fmt.Table([
        ("headless",     "No QEMU window - serial only"),
        ("timeout=<Xs>", "Kill the guest after a duration (30s, 5m, 1h)"),
    ]);

    Fmt.Section("Build options", "(also accepted by run and check)");
    Fmt.Table([
        ("--stdlib <dir>",   "Override the libgata directory"),
        ("--werror",         "Treat warnings as errors"),
        ("--env <env.g>",    "Environment file, overriding discovery"),
        ("--entry <file.g>", "Entry source, overriding discovery"),
        ("--emit-sourcemap", "Write sourcemap.json (dense name -> readable name)"),
        ("--pure-transpile", "Emit C and stop, with no .gconf at all - needs --env and --entry (build only; check never emits, so it takes --env/--entry on their own)"),
    ]);
    Console.WriteLine();
    Fmt.Para($"{C.DIM}A project build discovers its own environment (the @environment file in the project directory) and entry (src/main.g), so --env and --entry are only for loose files.{C.NC}");

    Fmt.Section("Examples");
    Fmt.Table([
        ("appa install", ""),
        ("appa new myos && cd myos && appa run", ""),
        ("appa run headless timeout=30s", ""),
        ("appa build --pure-transpile --env env.g --entry src/main.g", ""),
        ("appa check myos --werror", ""),
        ("appa clean", ""),
    ]);
    Console.WriteLine();
}

#endregion

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

realm kernel {
    entry func Main() {
        Misc.PrintBanner();
        Console.PrintLine("Hello from {{name}}!");
    }
}

realm userspace {
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

static class Log
{
    /// <summary>
    /// Prints an informational message.
    /// </summary>
    public static void Info(string m) => Console.WriteLine(m);

    /// <summary>
    /// Prints a success message with a green checkmark.
    /// </summary>
    public static void Ok(string m) => Console.WriteLine($"{C.LEAF}✓{C.NC} {m}");

    /// <summary>
    /// Prints a step message in cyan.
    /// </summary>
    public static void Step(string m) => Console.WriteLine($"{C.FOREST}{m}{C.NC}");

    /// <summary>
    /// Prints a warning message, wrapped under its own label.
    /// </summary>
    public static void Warn(string m) => Console.Out.Write(Tagged($"{C.YELLOW}warning:{C.NC}", m));

    /// <summary>
    /// Prints an error message and optional hint to stderr. Both wrap to the terminal and hang under
    /// their label, so a long hint reads as one block instead of running off the right edge.
    /// </summary>
    public static void Error(string m, string? hint = null)
    {
        Console.Error.Write(Tagged($"{C.RED}error:{C.NC}", m));
        if (hint != null) Console.Error.Write(Tagged($"{C.SAGE}={C.NC} {C.CYAN}help{C.NC}:", hint));
    }

    /// <summary>
    /// Lays out "label: message" with the message wrapped and continuation lines indented under it.
    /// </summary>
    private static string Tagged(string label, string message)
    {
        string indent = new string(' ', Fmt.Visible(label) + 1);
        var lines = Fmt.Wrap(message, Math.Max(20, Fmt.Width - indent.Length));
        var sb = new System.Text.StringBuilder($"{label} {lines[0]}{Environment.NewLine}");
        for (int i = 1; i < lines.Count; i++) sb.AppendLine(indent + lines[i]);
        return sb.ToString();
    }
}
