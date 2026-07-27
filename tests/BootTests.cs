namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// End-to-end boot regression: build a full GatOS ISO from a comprehensive program and boot it
/// headless in QEMU, asserting the kernel reaches its idle loop and that every section of the
/// program announced itself. Needs the GatOS toolchain + template that 'appa setup' installs;
/// skips gracefully when that isn't present.
/// </summary>
[Collection("Boot")]
public class BootTests(BootFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(35);

    #region Expectations

    /// <summary>
    /// Kernel-realm trace markers, in program order. GatOS routes a kernel 'debug' to COM1
    /// (drivers/serial.h), which QEMU multiplexes onto stdio.
    /// </summary>
    private static readonly string[] KernelMarkers =
    [
        "M:start",              // entry reached
        "M:arith",              // overload selection, int64 arithmetic, explicit narrowing
        "M:arc",                // reference-counted allocation churn in a loop
        "M:generics",           // nested generic containers and for..in
        "M:defer",              // block-bodied defer spliced at an early return
        "M:throws",             // catch handler supplying a managed value, and propagation
        "M:pressure",           // ARC under load: deep recursion and string temporaries
        "M:generic-throws",     // a generic throws function's Result typedef
        "M:cross-file-generic", // a generic from another file over a class declared here
        "M:enum-union",         // cross-file enum and union, switch and match
        "M:aggregates",         // default([4]int) and nested unary operators
        "M:c-keywords",         // locals named struct/register/signed, and a ref parameter
        "M:private-mangling",   // two files' same-named private functions
        "M:unsafe",             // pointer round trip
        "M:done",               // ran to the end of the entry function
    ];

    /// <summary>
    /// User-realm trace markers. A user 'debug' is a SYS_DEBUG_WRITE syscall that bypasses the
    /// TTY and goes straight to COM3 (sys/syscall.c), which appa points at artifacts/user-debug.log.
    /// Checking that file is what proves the userspace _env_dbg bind works at all, rather than
    /// inferring it from the thread's ordinary console output.
    /// </summary>
    private static readonly string[] UserMarkers =
    [
        "M:user-thread", // the spawned user thread's entry ran
        "M:user-arc",    // a class from the other file, allocated in userspace
        "M:user-done",   // ran to the end of the thread entry
    ];

    /// <summary>
    /// Exact output lines the program prints. Every value is derived rather than constant, so
    /// each one pins a computation: 'neg=-5' is a nested unary minus, 'scaled=10' is the two
    /// private Scale functions resolving to their own file's, 'crate=4' is the cross-file
    /// generic, 'keywords=3' is a C-keyword local passed by ref across a file boundary.
    /// </summary>
    private static readonly string[] ExpectedOutput =
    [
        "shown=9900",
        "counter=1999000 acc=9",
        "defer=380 caught=755 unwrap=7/100",
        "grade=5 read=7 zeros=0 neg=-5 flip=9",
        "keywords=3 scaled=10 deref=42 crate=4",
        "recursed=20100 strchurn=3835",
        "REGRESSION_OK",
        "pi*2=6 load=3",
    ];

    #endregion

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

        using var work = TempDir.Create("appa-boot-");
        Directory.CreateDirectory(Path.Combine(work.Path, "src"));
        foreach (var g in Directory.GetFiles(Path.Combine(fixturesDir, "Boot"), "*.g"))
            File.Copy(g, Path.Combine(work.Path, "src", Path.GetFileName(g)));
        File.Copy(Path.Combine(fixture.EnvsDir!, "env.GatOS.g"), Path.Combine(work.Path, "env.g"));
        File.WriteAllText(Path.Combine(work.Path, "boot.gconf"), """
            <appa>
                <ProjectName>boot</ProjectName>
                <TargetBackend>GatOS</TargetBackend>
                <BuildMode>Debug</BuildMode>
                <OutputType>Serial</OutputType>
            </appa>
            """);

        // No --stdlib: this exercises the real, installed GatOS toolchain end to end, so it
        // discovers libgata the same way a real 'appa build' does.
        var psi = new ProcessStartInfo("dotnet",
            $"\"{appaDll}\" build --run --headless --timeout={(int)Timeout.TotalSeconds}s")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work.Path,
        };
        // Read both streams concurrently before waiting: draining one to completion first
        // deadlocks if the process fills the other's OS pipe buffer.
        var ct = TestContext.Current.CancellationToken;
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = new CancellationTokenSource(Timeout + TimeSpan.FromSeconds(15));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { } }

        string log = await outTask + await errTask;
        string userLog = ReadIfPresent(Path.Combine(work.Path, "artifacts", "user-debug.log"));

        Assert.Contains("Reached kernel idle loop", log);

        // What separates "the ISO booted" from "every construct actually executed": a section
        // that faults, is skipped, or is optimised away leaves its marker missing, and checking
        // only the final answers would not notice.
        AssertMarkers(KernelMarkers, "[DEBUG] ", log, "COM1/stdio", log, userLog);
        AssertMarkers(UserMarkers, "[USER DEBUG] ", userLog, "COM3/user-debug.log", log, userLog);

        // Markers prove the code ran; these prove it computed the right thing.
        foreach (var expected in ExpectedOutput)
            Assert.True(log.Contains(expected),
                $"expected output line not found: '{expected}'{Logs(log, userLog)}");
    }

    /// <summary>Asserts every marker, with its realm's prefix, reached the channel it belongs on.</summary>
    private static void AssertMarkers(
        string[] markers, string prefix, string channel, string channelName, string log, string userLog)
    {
        var missing = markers.Where(m => !channel.Contains(prefix + m)).ToList();
        Assert.True(missing.Count == 0,
            $"the image booted but these sections never reached {channelName}: " +
            $"{string.Join(", ", missing)}{Logs(log, userLog)}");
    }

    private static string Logs(string log, string userLog) =>
        $"\n\n--- COM1/stdio ---\n{log}\n--- COM3/user-debug.log ---\n{userLog}";

    /// <summary>Reads a serial capture, tolerating QEMU never having created it.</summary>
    private static string ReadIfPresent(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : "<not written>"; }
        catch (IOException) { return "<unreadable>"; }
    }
}
