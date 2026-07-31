namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// End-to-end boot regression: builds a full GatOS ISO and boots it headless in QEMU, asserting the
/// kernel reaches its idle loop and every section announced itself. Needs what 'appa setup'
/// installs, and skips gracefully without it.
/// </summary>
[Collection("Boot")]
public class BootTests(BootFixture fixture)
{
    /// <summary>
    /// How long the booted image gets to reach its idle loop. Passed to 'appa build --run', so it
    /// bounds the QEMU run alone - the cross-compile ahead of it is not on this clock.
    /// </summary>
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(35);

    /// <summary>
    /// Backstop for the whole 'appa build --run' process, which cross-compiles the kernel, builds an
    /// ISO with grub-mkrescue and xorriso, and only then boots it.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. This is a "something hung" guard, not a performance assertion: QEMU is
    /// already bounded by BootTimeout, so the only thing a tight budget here can do is fail the
    /// build on a slow or loaded machine. It was BootTimeout + 15s, which covered the boot but not
    /// the build in front of it, and killed the run mid-compile on a cold rebuild - reported as the
    /// image never reaching its idle loop, which is the one thing that had not gone wrong.
    /// </remarks>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(10);

    #region Expectations

    /// <summary>
    /// Kernel-realm trace markers, in program order. GatOS routes a kernel 'debug' to COM1
    /// (drivers/serial.h), which QEMU multiplexes onto stdio.
    /// </summary>
    private static readonly string[] KernelMarkers =
    [
        "M:start",              // entry reached
        "M:shadowing",          // @shadows: three levels of one name, each meaning its own
        "M:arith",              // overload selection, int64 arithmetic, explicit narrowing
        "M:arc",                // reference-counted allocation churn in a loop
        "M:generics",           // nested generic containers and for..in
        "M:defer",              // block-bodied defer spliced at an early return
        "M:throws",             // catch handler supplying a managed value, and propagation
        "M:pressure",           // ARC under load: deep recursion and string temporaries
        "M:generic-throws",     // a generic throws function's Result typedef
        "M:cross-file-generic", // a generic from another file over a class declared here
        "M:enum-union",         // cross-file enum and union, switch and match
        "M:managed-union",      // reference-counted union payloads, nested and in a class field
        "M:union-equality",     // generated structural equality, including through a payload's '=='
        "M:generic-union",      // a union template stamped per instantiation, including a recursive one
        "M:aggregates",         // default([4]int) and nested unary operators
        "M:c-keywords",         // locals named struct/register/signed, and a ref parameter
        "M:private-mangling",   // two files' same-named private functions
        "M:unsafe",             // pointer round trip
        "M:kernel-thread",      // a kernel process reusing the userspace realm's process and thread names
        "M:done",               // ran to the end of the entry function
        // Concurrency (conc.g). These come from threads, so they land after the idle loop.
        "M:sync-basics-ok",     // uncontended AtomicInt and SpinLock semantics, including TryLock
        "M:kconc-setup",        // the shared object was published to the kernel-side slot
        "M:kconc-joined",       // all four kernel workers finished
        "M:kconc-ok",           // atomics, lock-protected non-atomic state and CAS tickets all exact
        "M:kconc-rc-ok",        // the shared object's refcount came back to the pin
        "M:state-joined",       // both threads finished contributing to their process's variables
        "M:state-ok",           // process variables: shared across threads, reachable from a
                                // process function, and a managed one still alive to be read
    ];

    /// <summary>
    /// User-realm trace markers. A user 'debug' is a syscall bypassing the TTY for COM3, which appa
    /// points at artifacts/user-debug.log - checking that file is what proves the userspace
    /// _env_dbg bind works, rather than inferring it from console output.
    /// </summary>
    private static readonly string[] UserMarkers =
    [
        "M:user-thread", // the spawned user thread's entry ran
        "M:qualified",   // a scope qualifier reached each of the four Depths from the innermost
        "M:user-arc",    // a class from the other file, allocated in userspace
        "M:user-done",   // ran to the end of the thread entry
        "M:uconc-setup", // the shared object was published to the userspace slot
        "M:uconc-joined",// all three userspace workers finished
        "M:uconc-ok",    // the same invariants, in a separate address space
        "M:uconc-rc-ok", // the shared object's refcount came back to the pin
    ];

    /// <summary>
    /// Exact output lines. Every value is derived rather than constant, so each pins a computation:
    /// 'neg=-5' a nested unary minus, 'scaled=10' two private Scale functions, 'crate=4' the
    /// cross-file generic, 'keywords=3' a C-keyword local passed by ref.
    /// </summary>
    private static readonly string[] ExpectedOutput =
    [
        "shown=9900",
        "counter=1999000 acc=9",
        "defer=380 caught=755 unwrap=7/100",
        "grade=5 read=7 zeros=0 neg=-5 flip=9",
        "keywords=3 scaled=10 deref=42 crate=4 relay=6",
        "recursed=20100 strchurn=3835",
        // Managed unions. 'ulive'/'uchurnlive' are load-bearing: the live population once every
        // owner is out of scope, so anything but 0 means a release did not run. 'umade' against
        // the weights pins the other direction - a double release changes a weight.
        "uweights=22 umade=7 ulive=0 uchurn=400 uchurnlive=0",
        // Structural equality. 125 is bits 1+4+8+16+32+64 - the two comparisons that must be
        // false are exactly the bits left clear, so an equality answering true for everything
        // reads 255. 'ueqlive=0' pins that the inline-built operands were released.
        "ueq=125 ueqlive=0",
        // Generic unions. 107 = 6 (Found) - 1 (Missing) + 2 (a counted payload's id) + 100
        // (equality within one instantiation); the cross-instantiation comparison adds nothing.
        // 'leaves=3' walks a recursive Tree[int] through a generic function inferring from it.
        "gsum=107 leaves=3",
        // Three declarations of one name, each meaning its own. 'root=1' is the load-bearing half:
        // a function written outside the realm still sees the outer Depth, so a scope's displacement
        // does not leak past the scope that declared it.
        "depth=2 root=1",
        "REGRESSION_OK",
        // The innermost of the four Depths is what a bare call means; the qualifiers then name the
        // other three explicitly, from inside the scope that displaced them. 'q=431' is the proof
        // that a qualifier is a compile-time choice of symbol, not a runtime lookup.
        "udepth=4 q=431",
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
            $"\"{appaDll}\" build --run --headless --timeout={(int)BootTimeout.TotalSeconds}s")
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
        using var cts = new CancellationTokenSource(ProcessTimeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { } }

        string log = await outTask + await errTask;
        string userLog = ReadIfPresent(Path.Combine(work.Path, "artifacts", "user-debug.log"));

        Assert.Contains("Reached kernel idle loop", log);

        // A thread cannot print, so conc.g's reporters check their own invariants and announce
        // a verdict. This runs before the marker check because both failures look the same
        // there - a reporter that never ran and one that ran and disagreed each leave the
        // '-ok' marker missing. Checked first, the verdict names which check failed.
        foreach (var channel in (string[])[log, userLog])
            Assert.True(!channel.Contains("-BAD"),
                "a section reported a failed check (look for the '-BAD' marker)" + Logs(log, userLog));

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

    /// <summary>
    /// Asserts every marker reached the channel it belongs on, and that the channel is the one
    /// this realm writes to.
    /// </summary>
    /// <remarks>
    /// The prefix is checked once for the channel rather than against each marker. It used to be
    /// required immediately before every marker, which stopped holding once two userspace threads
    /// could emit at the same time: the environment's userspace _env_dbg wrote the prefix, the
    /// message and the newline as three separate syscalls, so a thread switch between any two
    /// interleaved the lines and produced "[USER DEBUG] [USER DEBUG] M:a\nM:b". Both markers
    /// arrived, on the right channel, but neither sat next to a prefix.
    ///
    /// env.GatOS.g now assembles the line and writes it once, which removes the cause - but the
    /// boot fixture downloads its environment rather than reading the one in this checkout, so
    /// that fix only takes effect for this test once envs/ is republished. Checking the prefix
    /// per channel still proves routing (a kernel marker appearing here would fail), without
    /// pinning the test to how many writes the environment happens to use.
    /// </remarks>
    private static void AssertMarkers(
        string[] markers, string prefix, string channel, string channelName, string log, string userLog)
    {
        Assert.True(markers.Length == 0 || channel.Contains(prefix),
            $"nothing on {channelName} carried the '{prefix.Trim()}' prefix{Logs(log, userLog)}");

        var missing = markers.Where(m => !channel.Contains(m)).ToList();
        Assert.True(missing.Count == 0,
            $"the image booted but these sections never reached {channelName}: " +
            $"{string.Join(", ", missing)}{Logs(log, userLog)}");
    }

    private static string Logs(string log, string userLog) =>
        $"\n\n--- COM1/stdio ---\n{log}\n--- COM3/user-debug.log ---\n{userLog}";

    /// <summary>
    /// Reads a serial capture, tolerating QEMU never having created it.
    /// </summary>
    private static string ReadIfPresent(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : "<not written>"; }
        catch (IOException) { return "<unreadable>"; }
    }
}
