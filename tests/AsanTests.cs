namespace Appa.Tests;

using System.Diagnostics;
using Appa;

/// <summary>
/// Memory-safety regression, ported from tests/asan/run.sh: transpile a
/// userspace-heavy program (ARC churn, nested generics, string building) for the
/// hosted target, compile it under ASan+UBSan+LSan against a real libc, and assert
/// the checksum. Skips gracefully when no host gcc is available.
/// </summary>
public class AsanTests
{
    private const long ExpectedChecksum = 50294;

    /// <summary>
    /// Transpiles asan/main.g, compiles the generated C with host gcc under
    /// AddressSanitizer/UndefinedBehaviorSanitizer/LeakSanitizer, and asserts the
    /// driver's checksum with no sanitizer violations.
    /// </summary>
    [Fact]
    public void HostedProgramPassesUnderSanitizers()
    {
        if (!ToolchainProbe.HasGcc())
        {
            Assert.Skip("host gcc not found on PATH; skipping ASan regression");
            return;
        }

        string fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        string libgataDir = Path.Combine(fixturesDir, "Libgata");
        string envPath = Path.Combine(fixturesDir, "Envs", "env.hosted.g");
        string driverPath = Path.Combine(fixturesDir, "Asan", "driver.c");
        string entryPath = Path.Combine(fixturesDir, "Asan", "main.g");

        string work = Directory.CreateTempSubdirectory("appa-asan-").FullName;
        try
        {
            var (programs, _, imports, diag) = Pipeline.Transpile([envPath, entryPath], work, libgataDir);
            var visible = Pipeline.VisibleModules(imports);
            var (module, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);
            Assert.False(diag.HasErrors, "asan/main.g failed to transpile");

            var output = Layout.Compose(new Emitter(module, diag).Build());
            Directory.CreateDirectory(Path.Combine(work, "transpilation"));
            foreach (var f in output)
                File.WriteAllText(Path.Combine(work, "transpilation", f.Name), f.Content);

            string programC = Path.Combine(work, "transpilation", "program.c");
            string binary = Path.Combine(work, "asan_test");
            var compile = RunProcess("gcc",
                $"-fsanitize=address,undefined -g -O1 -I\"{Path.Combine(work, "transpilation")}\" " +
                $"\"{programC}\" \"{driverPath}\" -lm -o \"{binary}\"", work);
            Assert.True(compile.ExitCode == 0, $"gcc failed:\n{compile.StdErr}");

            var run = RunProcess(binary, "", work, env: new() { ["ASAN_OPTIONS"] = "detect_leaks=1" });
            Assert.True(run.ExitCode == 0, $"asan_test exited {run.ExitCode}:\n{run.StdOut}\n{run.StdErr}");
            Assert.Contains("ASAN_REGRESSION_OK", run.StdOut);
            Assert.Contains($"checksum={ExpectedChecksum}", run.StdOut);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Runs an external process to completion and captures its exit code and output.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string exe, string args, string workDir, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }
}
