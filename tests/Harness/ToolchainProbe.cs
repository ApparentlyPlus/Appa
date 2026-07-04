namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// Cheap presence check for the GatOS cross toolchain that BootTests shells out to.
/// Used to skip gracefully rather than fail when a machine lacks the toolchain.
/// </summary>
internal static class ToolchainProbe
{
    /// <summary>
    /// True if the GatOS cross toolchain, libgata, and QEMU are installed (via 'appa setup').
    /// </summary>
    public static bool HasGatOSToolchain() =>
        Directory.Exists(AppaPaths.ToolchainDir) && File.Exists(AppaPaths.Gcc()) &&
        Directory.Exists(AppaPaths.LibgataDir) && Directory.GetFiles(AppaPaths.LibgataDir, "*.g").Length > 0 &&
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
