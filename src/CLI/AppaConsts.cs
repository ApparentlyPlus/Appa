namespace Appa;

using System.Runtime.InteropServices;

#region Download URLs

static class Urls
{
    const string AppaRel = "https://github.com/ApparentlyPlus/Appa/releases/latest/download";
    const string Tc = "https://github.com/ApparentlyPlus/GatOS/releases/download/build-toolchain";
    public const string GataOwner = "ApparentlyPlus";
    public const string GataRepo = "Gata";
    public const string GataRef = "main";
    public const string Template = "https://github.com/ApparentlyPlus/GatOS/archive/refs/heads/appa-template.zip";

    /// <summary>
    /// Returns the platform toolchain bundle URL for the current OS.
    /// </summary>
    public static string Toolchain() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Tc + "/x86_64-win.zip" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Tc + "/x86_64-macOS.zip" :
        Tc + "/x86_64-linux.zip";

    /// <summary>
    /// Returns the self-update URL for the appa binary on the current platform, always resolving to
    /// the latest GitHub release via the "releases/latest/download" alias. Mac distinguishes Apple
    /// Silicon (amac) from Intel (imac).
    /// </summary>
    public static string AppaBinary() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? AppaRel + "/appa-win.exe" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? AppaRel + "/appa-amac"
                : AppaRel + "/appa-imac")
            : AppaRel + "/appa-linux";
}

#endregion

#region Version

static class AppaVersion
{
    public const string Current = "2.0.0";
}

#endregion

#region Filesystem Paths

static class AppaPaths
{
    private static string GetUserHomeUnix(string username)
    {
        try
        {
            if (File.Exists("/etc/passwd"))
            {
                foreach (var line in File.ReadLines("/etc/passwd"))
                {
                    var parts = line.Split(':');
                    if (parts.Length >= 6 && parts[0] == username)
                    {
                        return parts[5];
                    }
                }
            }
        }
        catch
        {
            // Ignore and fall back
        }
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? $"/Users/{username}" : $"/home/{username}";
    }

    private static string GetLocalSharePath()
    {
        string? sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrEmpty(sudoUser) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string homeDir = GetUserHomeUnix(sudoUser);
            if (Directory.Exists(homeDir))
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Path.Combine(homeDir, "Library", "Application Support")
                    : Path.Combine(homeDir, ".local", "share");
            }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public static readonly string Root = Path.Combine(
        GetLocalSharePath(), "appa");

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
        ? Path.Combine(PlatformToolchain, "xorriso", "xorriso.exe")
        : Path.Combine(PlatformToolchain, "xorriso", "xorriso");
}

#endregion

#region Console Output

static class C
{
    public const string NC = "\x1b[0m";
    public const string BOLD = "\x1b[1m";
    public const string DIM = "\x1b[2m";
    public const string EMBER = "\x1b[1;38;5;209m";
    public const string GOLD = "\x1b[1;38;5;221m";
    public const string SAND = "\x1b[38;5;180m";
    public const string CYAN = "\x1b[1;38;5;80m";
    public const string YELLOW = "\x1b[1;38;5;214m";
    public const string RED = "\x1b[1;38;5;203m";
}

static class Out
{
    const string Indent = Fmt.Indent;

    /// <summary>
    /// Prints a finished step with its elapsed time pinned to the right edge, so every step in a run
    /// lines up however long its label runs.
    /// </summary>
    public static void Step(string message, TimeSpan elapsed) =>
        Fmt.Justify(message, $"{C.DIM}{Spin.Fmt(elapsed)}{C.NC}");

    /// <summary>
    /// Prints a plain indented fact with no timing.
    /// </summary>
    public static void Note(string message) => Console.WriteLine($"{Indent}{message}");

    /// <summary>
    /// Prints a paragraph, wrapped to the terminal at the standard indent.
    /// </summary>
    public static void Para(string message) => Fmt.Para(message);

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
