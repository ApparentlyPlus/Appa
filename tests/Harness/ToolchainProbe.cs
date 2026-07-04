namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// Cheap presence checks for external toolchains that asan/boot tests shell out to.
/// Used to skip gracefully rather than fail when a machine lacks the toolchain.
/// </summary>
internal static class ToolchainProbe
{
    /// <summary>
    /// True if a host gcc is reachable on PATH. Asan uses the host compiler directly,
    /// not the GatOS cross toolchain.
    /// </summary>
    public static bool HasGcc() => CanStart("gcc", "--version");

    /// <summary>
    /// True if the GatOS cross toolchain and QEMU are installed (via 'appa setup').
    /// </summary>
    public static bool HasGatOSToolchain() =>
        Directory.Exists(AppaPaths.ToolchainDir) && File.Exists(AppaPaths.Gcc()) &&
        CanStart(AppaPaths.QemuExe, "--version");

    /// <summary>
    /// Starts a process and returns true if it launched and exited, regardless of
    /// exit code - false only when the executable itself could not be found/run.
    /// </summary>
    private static bool CanStart(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }
}
