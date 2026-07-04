namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// End-to-end boot regression, ported from tests/boot/run.sh: build a full GatOS
/// ISO from a comprehensive program and boot it headless in QEMU, asserting the
/// kernel reaches its idle loop and the program's own markers print. Needs the
/// GatOS toolchain + template that 'appa setup' installs; skips gracefully when
/// that isn't present.
/// </summary>
public class BootTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(35);

    /// <summary>
    /// Scaffolds a throwaway GatOS project around boot/main.g, runs 'appa build
    /// --run --headless' against it, and asserts the serial log carries the idle
    /// loop marker, the kernel-side regression marker, and the userspace marker.
    /// </summary>
    [Fact]
    public async Task GatOSImageBootsAndProgramMarkersAppear()
    {
        if (!ToolchainProbe.HasGatOSToolchain())
        {
            Assert.Skip("GatOS toolchain/QEMU not installed (run 'appa setup'); skipping boot regression");
            return;
        }

        string fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        string appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        string libgataDir = Path.Combine(fixturesDir, "Libgata");

        string work = Directory.CreateTempSubdirectory("appa-boot-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(work, "src"));
            File.Copy(Path.Combine(fixturesDir, "Boot", "main.g"), Path.Combine(work, "src", "main.g"));
            File.Copy(Path.Combine(fixturesDir, "Envs", "env.GatOS.g"), Path.Combine(work, "env.g"));
            File.WriteAllText(Path.Combine(work, "boot.gconf"), """
                <appa>
                    <ProjectName>boot</ProjectName>
                    <TargetBackend>GatOS</TargetBackend>
                    <BuildMode>Debug</BuildMode>
                    <OutputType>Serial</OutputType>
                </appa>
                """);

            var psi = new ProcessStartInfo("dotnet",
                $"\"{appaDll}\" build --stdlib \"{libgataDir}\" --run --headless --timeout={(int)Timeout.TotalSeconds}s")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = work,
            };
            // Read both streams concurrently before waiting: reading one to completion
            // first deadlocks if the process fills the other's OS pipe buffer, since it
            // then blocks on that write while we block on this read.
            var ct = TestContext.Current.CancellationToken;
            using var proc = Process.Start(psi)!;
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            using var cts = new CancellationTokenSource(Timeout + TimeSpan.FromSeconds(15));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { } }

            string log = await outTask + await errTask;
            Assert.Contains("Reached kernel idle loop", log);
            Assert.Contains("REGRESSION_OK", log);
            Assert.Contains("pi*2=6", log);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
