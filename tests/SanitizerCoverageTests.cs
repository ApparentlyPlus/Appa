namespace Appa.Tests;

/// <summary>
/// Says out loud whether the execution suite's memory-safety oracle is switched on.
/// </summary>
public class SanitizerCoverageTests
{
    /// <summary>
    /// If any compiler on this machine can link with ASan/UBSan, the harness must have chosen one
    /// of them. Distributions commonly ship gcc with libasan in a separate package while clang next
    /// to it is complete, and 'cc' is probed first - so picking the first compiler that answers
    /// --version silently disarmed every execution test on exactly those machines.
    /// </summary>
    [Fact]
    public void HarnessPicksSanitizer()
    {
        var chosen = HostedRun.FindCompiler();
        if (chosen == null) { Assert.Skip("no host C compiler"); return; }

        var capable = new List<string>();
        foreach (var exe in (string[])["cc", "gcc", "clang"])
            if (HostedRun.CanRun(exe) && HostedRun.SupportsSanitizers(exe)) capable.Add(exe);

        if (capable.Count == 0)
        {
            Assert.Skip($"'{chosen}' has no sanitizer runtime and no other compiler on this machine " +
                        "does either - the execution suite ran without its memory-safety oracle");
            return;
        }

        Assert.True(HostedRun.SupportsSanitizers(chosen),
            $"the harness chose '{chosen}', which cannot link with ASan/UBSan, while " +
            $"{string.Join(", ", capable)} can - every execution test just ran unsanitized");
    }

    /// <summary>
    /// A deliberate use-after-free in hand-written C must be caught. This is the sabotage check for
    /// the check: it fails if the sanitizer is linked but not actually reporting, which no amount
    /// of green Gata programs would reveal.
    /// </summary>
    [Fact]
    public void SanitizerReportsUseAfterFree()
    {
        var cc = HostedRun.FindCompiler();
        if (cc == null || !HostedRun.SupportsSanitizers(cc)) { Assert.Skip("no sanitizing compiler"); return; }

        using var work = TempDir.Create("appa-asan-selftest-");
        File.WriteAllText(work.Combine("uaf.c"), """
            #include <stdlib.h>
            int main(void) { int* p = malloc(sizeof(int)); free(p); return *p; }
            """);

        var (ccCode, ccOut) = HostedRun.Run(cc,
            "-fsanitize=address,undefined -fno-omit-frame-pointer -g -o uaf uaf.c", work.Path);
        Assert.True(ccCode == 0, $"the sanitizer self-test did not build:\n{ccOut}");

        var (_, runOut) = HostedRun.Run(work.Combine("uaf"), "", work.Path,
            new Dictionary<string, string> { ["ASAN_OPTIONS"] = "detect_leaks=0" });
        Assert.Contains("AddressSanitizer", runOut);
    }
}
