namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// End-to-end boot regression, ported from tests/boot/run.sh: build a full GatOS
/// ISO from a comprehensive program and boot it headless in QEMU, asserting the
/// kernel reaches its idle loop and the program's own markers print. Needs the
/// GatOS toolchain + template that 'appa setup' installs; skips gracefully when
/// that isn't present.
/// </summary>
[Collection("Boot")]
public class BootTests(BootFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(35);

    /// <summary>
    /// The trace markers Fixtures/Boot/main.g emits, one per section, in program order. A
    /// missing marker names the exact construct that failed to run on the target.
    ///
    /// Kernel-realm markers only: a user-realm 'debug' is routed to COM3 by GatOS, a separate
    /// serial channel from the kernel's COM2, and this harness captures only the latter. The
    /// user thread reports through Console instead, and is checked in ExpectedOutput.
    /// </summary>
    private static readonly string[] ExpectedMarkers =
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
        "M:user-thread",
        "pi*2=6 load=3",
    ];

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

        string work = Directory.CreateTempSubdirectory("appa-boot-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(work, "src"));
            // Every .g in the fixture, not just main.g: the program spans two files so the
            // boot regression covers the multi-file front end on the real target too.
            foreach (var g in Directory.GetFiles(Path.Combine(fixturesDir, "Boot"), "*.g"))
                File.Copy(g, Path.Combine(work, "src", Path.GetFileName(g)));
            File.Copy(Path.Combine(fixture.EnvsDir!, "env.GatOS.g"), Path.Combine(work, "env.g"));
            File.WriteAllText(Path.Combine(work, "boot.gconf"), """
                <appa>
                    <ProjectName>boot</ProjectName>
                    <TargetBackend>GatOS</TargetBackend>
                    <BuildMode>Debug</BuildMode>
                    <OutputType>Serial</OutputType>
                </appa>
                """);

            // No --stdlib: this test exercises the real, installed GatOS toolchain end
            // to end, so it discovers libgata the same way a real 'appa build' does.
            var psi = new ProcessStartInfo("dotnet",
                $"\"{appaDll}\" build --run --headless --timeout={(int)Timeout.TotalSeconds}s")
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

            // The kernel reached its idle loop, so the image booted and ran to completion.
            Assert.Contains("Reached kernel idle loop", log);

            // Every section of the program announced itself. This is what separates "the ISO
            // booted" from "every construct actually executed": a section that faults, is
            // skipped, or is optimised away leaves its marker missing, and checking only the
            // final answers would not notice.
            var missing = ExpectedMarkers.Where(m => !log.Contains($"[DEBUG] {m}")).ToList();
            Assert.True(missing.Count == 0,
                $"the image booted but these sections never ran: {string.Join(", ", missing)}\n\n--- serial log ---\n{log}");

            // Markers prove the code ran; these prove it computed the right thing. Each line
            // is checked verbatim, so a miscompile shows up as a wrong number rather than as
            // a silent pass.
            foreach (var expected in ExpectedOutput)
                Assert.True(log.Contains(expected),
                    $"expected output line not found: '{expected}'\n\n--- serial log ---\n{log}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
