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
        "uweights=22 umade=7 ulive=0 uchurn=400 uchurnlive=0",
        "ueq=125 ueqlive=0",
        "gsum=107 leaves=3",
        "depth=2 root=1",
        "mainshare=42 2",
        "REGRESSION_OK",
        "udepth=4 q=431",
        "pi*2=6 load=3",
    ];

    #endregion

    [Fact]
    public async Task ImageBoots()
    {
        if (!ToolchainProbe.HasGatOSToolchain())
        {
            Assert.Skip("GatOS toolchain/QEMU not installed (run 'appa setup'); skipping boot regression");
            return;
        }

        var (log, userLog) = await BuildAndBoot("Boot", "Debug");

        Assert.Contains("Reached kernel idle loop", log);

        // A thread cannot print a marker without the kernel's _env_dbg having been bound to the userspace one
        foreach (var channel in (string[])[log, userLog])
            Assert.True(!channel.Contains("-BAD"),
                "a section reported a failed check (look for the '-BAD' marker)" + Logs(log, userLog));

        // What separates "the ISO booted" from "every construct actually executed"
        AssertMarkers(KernelMarkers, "[DEBUG] ", log, "COM1/stdio", log, userLog);
        AssertMarkers(UserMarkers, "[USER DEBUG] ", userLog, "COM3/user-debug.log", log, userLog);

        // Markers prove the code ran; these prove it computed the right thing.
        foreach (var expected in ExpectedOutput)
            Assert.True(log.Contains(expected),
                $"expected output line not found: '{expected}'{Logs(log, userLog)}");
    }

    /// <summary>
    /// The lines a Release image has to print. Nothing here is a 'debug' marker: Release rejects
    /// both 'debug' and 'panic', which is why the main fixture cannot be reused and why the kernel
    /// had never been compiled at the optimisation level a shipped image uses.
    /// </summary>
    private static readonly string[] ReleaseOutput =
    [
        "R:box 1", "R:pair 5", "R:one 4", "R:vec 7 false true",
        "R:churn 499 4 46", "R:throws 1 -9 20",
        "R:nums -2147483647 4294967295 -715827882 -1",
        "R:defer", "R:str abccc 5", "R:done",
    ];

    /// <summary>
    /// Builds the same source twice - Debug and Release - into two ISOs, boots both, and requires
    /// them to print the same thing.
    /// </summary>
    [Fact]
    public async Task ReleaseImageMatchesDebug()
    {
        if (!ToolchainProbe.HasGatOSToolchain())
        {
            Assert.Skip("GatOS toolchain/QEMU not installed (run 'appa setup'); skipping release boot regression");
            return;
        }

        var (debugLog, _) = await BuildAndBoot("BootRelease", "Debug");
        Assert.Contains("Reached kernel idle loop", debugLog);

        var (releaseLog, _) = await BuildAndBoot("BootRelease", "Release");
        Assert.Contains("Reached kernel idle loop", releaseLog);

        foreach (var expected in ReleaseOutput)
            Assert.True(releaseLog.Contains(expected),
                $"the release image booted but never printed '{expected}'\n\n--- release ---\n{releaseLog}");

        var d = ProgramLines(debugLog);
        var r = ProgramLines(releaseLog);
        Assert.True(d.Count > 0, "the debug image printed none of the fixture's lines");
        if (d.SequenceEqual(r)) return;

        var diff = new List<string>();
        for (int i = 0; i < Math.Max(d.Count, r.Count); i++)
        {
            string a = i < d.Count ? d[i] : "<none>";
            string b = i < r.Count ? r[i] : "<none>";
            if (a != b) diff.Add($"  line {i + 1}: debug '{a}' vs release '{b}'");
        }
        Assert.Fail($"the release image computed something the debug image did not:\n" +
                    string.Join("\n", diff.Take(20)));
    }

    /// <summary>
    /// The fixture's own output lines, in order. Everything the kernel and GRUB print around them
    /// is dropped, so the comparison is of the program rather than of the boot.
    /// </summary>
    internal static List<string> ProgramLines(string log)
    {
        var lines = new List<string>();
        foreach (var raw in log.Split('\n'))
        {
            string l = raw.Trim();
            int at = MarkerStart(l);
            if (at >= 0) lines.Add(l[at..]);
        }
        return lines;
    }

    /// <summary>
    /// Where this line's fixture marker begins, or -1. What follows the prefix is checked as well
    /// as the prefix itself: every marker is 'R:' plus a lowercase name or 'drop ' plus a count, so
    /// unrelated console text that merely contains the letters - 'ERROR: ...' ends in 'R:'.
    /// </summary>
    private static int MarkerStart(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == 'R' && i + 2 < line.Length && line[i + 1] == ':' && char.IsAsciiLetterLower(line[i + 2]))
                return i;
            if (line[i] == 'd' && i + 5 < line.Length &&
                line.AsSpan(i, 5).SequenceEqual("drop ") && char.IsAsciiDigit(line[i + 5]))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Writes the named fixture into a temporary project, builds a GatOS ISO in the given mode and
    /// boots it headless, returning COM1/stdio and the userspace capture.
    /// </summary>
    private async Task<(string Log, string UserLog)> BuildAndBoot(string fixtureName, string buildMode)
    {
        string fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        string appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");

        using var work = TempDir.Create("appa-boot-");
        Directory.CreateDirectory(Path.Combine(work.Path, "src"));
        foreach (var g in Directory.GetFiles(Path.Combine(fixturesDir, fixtureName), "*.g"))
            File.Copy(g, Path.Combine(work.Path, "src", Path.GetFileName(g)));
        File.Copy(Path.Combine(fixture.EnvsDir!, "env.GatOS.g"), Path.Combine(work.Path, "env.g"));
        File.WriteAllText(Path.Combine(work.Path, "boot.gconf"), $"""
            <appa>
                <ProjectName>boot</ProjectName>
                <TargetBackend>GatOS</TargetBackend>
                <BuildMode>{buildMode}</BuildMode>
                <OutputType>Serial</OutputType>
            </appa>
            """);

        // No --stdlib
        var psi = new ProcessStartInfo("dotnet",
            $"\"{appaDll}\" build --run --headless --timeout={(int)BootTimeout.TotalSeconds}s")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work.Path,
        };
        var ct = TestContext.Current.CancellationToken;
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = new CancellationTokenSource(ProcessTimeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { } }

        return (await outTask + await errTask,
                ReadIfPresent(Path.Combine(work.Path, "artifacts", "user-debug.log")));
    }

    /// <summary>
    /// Asserts every marker reached the channel it belongs on, and that the channel is the one
    /// this realm writes to.
    /// </summary>
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
