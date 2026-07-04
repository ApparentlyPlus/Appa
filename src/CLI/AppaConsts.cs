namespace Appa;

using System.Runtime.InteropServices;

#region Download URLs

// Download URLs for `appa setup` / `appa update`.
static class Urls
{
    const string Rel = "https://github.com/ApparentlyPlus/Gata/releases/download/artifacts";
    const string Tc = "https://github.com/ApparentlyPlus/GatOS/releases/download/build-toolchain";

    public const string Libgata = Rel + "/Gata-Internals.zip";

    // GitHub branch archive - wraps everything in a single top-level folder, which
    // the extractor flattens away (see ExtractTemplate).
    public const string Template = "https://github.com/ApparentlyPlus/GatOS/archive/refs/heads/appa-template.zip";

    /// <summary>
    /// Returns the platform toolchain bundle URL for the current OS.
    /// </summary>
    public static string Toolchain() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Tc + "/x86_64-win.zip" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Tc + "/x86_64-macOS.zip" :
        Tc + "/x86_64-linux.zip";

    /// <summary>
    /// Returns the self-update URL for the appa binary on the current platform.
    /// Mac distinguishes Apple Silicon from Intel.
    /// </summary>
    public static string AppaBinary() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Rel + "/appa-win.exe" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? Rel + "/appa-arm-mac"
                : Rel + "/appa-intel-mac")
            : Rel + "/appa-linux";
}

#endregion

#region Filesystem Paths

// All appa-managed state lives under <temp>/appa/.
static class AppaPaths
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "appa");

    public static string ToolchainDir => Path.Combine(Root, "toolchain");
    public static string LibgataDir => Path.Combine(Root, "libgata");
    public static string EnvsDir => Path.Combine(Root, "envs");
    public static string TemplateDir => Path.Combine(Root, "template");
    public static string BinDir => Path.Combine(Root, "bin");

    public static string AppaBin => Path.Combine(BinDir,
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "appa.exe" : "appa");

    public static string PlatformToolchain => Path.Combine(ToolchainDir,
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "x86_64-win" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "x86_64-macos" :
        "x86_64-linux");

    public static string GccBin => Path.Combine(PlatformToolchain, "gcc", "bin");
    public static string GrubDir => Path.Combine(PlatformToolchain, "grub");
    public static string QemuExe => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(PlatformToolchain, "qemu", "qemu-system-x86_64.exe")
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Path.Combine(PlatformToolchain, "qemu", "bin", "qemu-system-x86_64")
            : Path.Combine(PlatformToolchain, "qemu", "QEMU-x86_64.AppImage");

    /// <summary>
    /// Returns the full path to a cross-gcc tool binary for the current platform.
    /// </summary>
    public static string Gcc(string tool = "x86_64-elf-gcc") =>
        Path.Combine(GccBin, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? tool + ".exe" : tool);

    /// <summary>
    /// Returns the full path to a grub tool binary for the current platform.
    /// </summary>
    public static string GrubTool(string tool) =>
        Path.Combine(GrubDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? tool + ".exe" : tool);

    public static string XorrisoExe => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "" : Path.Combine(PlatformToolchain, "xorriso", "xorriso");
}

#endregion

#region Console Output

// ANSI color codes for console output.
static class C
{
    public const string NC = "\x1b[0m";
    public const string GREEN = "\x1b[1;32m";
    public const string RED = "\x1b[1;31m";
    public const string YELLOW = "\x1b[1;33m";
    public const string BLUE = "\x1b[1;34m";
    public const string CYAN = "\x1b[1;36m";
    public const string BOLD = "\x1b[1m";
    public const string DIM = "\x1b[2m";
}

// A quiet table for build-pipeline output: an indented fact per line, with its
// elapsed time (if any) starting at a fixed column. Compiler diagnostics are the
// deliberate exception: they stay flush left, gcc/rustc-style.
static class Out
{
    const string Indent = "  ";
    const int MsgWidth = 34;

    /// <summary>
    /// Prints a finished step with elapsed time at a fixed column.
    /// </summary>
    public static void Step(string message, TimeSpan elapsed) =>
        Console.WriteLine($"{Indent}{message.PadRight(MsgWidth)}{C.DIM}{Spin.Fmt(elapsed)}{C.NC}");

    /// <summary>
    /// Prints a plain indented fact with no timing.
    /// </summary>
    public static void Note(string message) => Console.WriteLine($"{Indent}{message}");

    /// <summary>
    /// Redraws a single line in place by returning to column 0 and clearing to EOL.
    /// </summary>
    public static void Redraw(string s) => Console.Write($"\r{s}\x1b[K");

    /// <summary>
    /// Clears the current in-place redraw line.
    /// </summary>
    public static void ClearRedraw() => Console.Write("\r\x1b[K");

    /// <summary>
    /// Prints a line nested one level deeper than Note/Step.
    /// </summary>
    public static void Child(string s) => Console.WriteLine($"{Indent}{Indent}{s}");
}

#endregion
