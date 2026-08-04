namespace Appa.Tests;

/// <summary>
/// libgata's memory engine against a byte-wise reference, at every alignment.
/// </summary>
public class MemoryEngineTests
{
    /// <summary>
    /// Cross-checks each operation against a naive byte loop over every combination of source
    /// offset, destination offset and length in range, and reports a count of mismatches per
    /// operation so a failure says which one broke rather than just that something did.
    /// </summary>
    private const string Program = """
        import Console;
        import String;
        import Mem;

        void func RefCopy(char* d, char* s, usize n) {
            unsafe { let i = (0 as usize); while (i < n) { d[i] = s[i]; i = i + (1 as usize); } }
        }

        int func RefCompare(char* a, char* b, usize n) {
            unsafe {
                let i = (0 as usize);
                while (i < n) {
                    if (a[i] != b[i]) { return (a[i] as int) - (b[i] as int); }
                    i = i + (1 as usize);
                }
            }
            return 0;
        }

        int func Sign(int x) { if (x > 0) { return 1; } if (x < 0) { return -1; } return 0; }

        realm userspace {
            entry func Main() {
                unsafe {
                    let cap  = (512 as usize);
                    let src  = alloc(cap) as char*;
                    let got  = alloc(cap) as char*;
                    let want = alloc(cap) as char*;

                    let seed = (12345 as usize);
                    let k = (0 as usize);
                    while (k < cap) {
                        seed = (seed * (1103515245 as usize) + (12345 as usize));
                        src[k] = ((seed >> 16) & (255 as usize)) as char;
                        k = k + (1 as usize);
                    }

                    let copyBad = 0; let cmpBad = 0; let fillBad = 0; let moveBad = 0;

                    let so = (0 as usize);
                    while (so < (9 as usize)) {
                        let dof = (0 as usize);
                        while (dof < (9 as usize)) {
                            let n = (0 as usize);
                            while (n < (70 as usize)) {
                                let z = (0 as usize);
                                while (z < cap) { got[z] = '\0'; want[z] = '\0'; z = z + (1 as usize); }
                                Mem.Copy(got + dof, src + so, n);
                                RefCopy(want + dof, src + so, n);
                                if (Mem.Compare(got, want, cap) != 0) { copyBad = copyBad + 1; }

                                if (Sign(Mem.Compare(src + so, src + so, n)) != 0) { cmpBad = cmpBad + 1; }
                                if (n > (0 as usize)) {
                                    RefCopy(want, src + so, n);
                                    RefCopy(got, src + so, n);
                                    let p = (0 as usize);
                                    while (p < n) {
                                        got[p] = ((got[p] as int) ^ 0x5A) as char;
                                        if (Sign(Mem.Compare(got, want, n)) != Sign(RefCompare(got, want, n))) {
                                            cmpBad = cmpBad + 1;
                                        }
                                        got[p] = want[p];
                                        p = p + (1 as usize);
                                    }
                                }

                                let z2 = (0 as usize);
                                while (z2 < cap) { got[z2] = '\0'; want[z2] = '\0'; z2 = z2 + (1 as usize); }
                                Mem.Fill(got + dof, 0xA7 as byte, n);
                                let q = (0 as usize);
                                while (q < n) { want[dof + q] = 0xA7 as char; q = q + (1 as usize); }
                                if (Mem.Compare(got, want, cap) != 0) { fillBad = fillBad + 1; }

                                n = n + (1 as usize);
                            }
                            dof = dof + (1 as usize);
                        }
                        so = so + (1 as usize);
                    }

                    // Overlap, forward and backward. The expectation comes from a scratch buffer,
                    // since a naive forward copy is itself wrong for a forward overlap.
                    let shift = (0 as usize);
                    while (shift < (20 as usize)) {
                        let len = (40 as usize);
                        RefCopy(got, src, (128 as usize));
                        RefCopy(want, src, (128 as usize));
                        Mem.Move(got + shift, got, len);
                        let tmp = alloc((64 as usize)) as char*;
                        RefCopy(tmp, want, len);
                        RefCopy(want + shift, tmp, len);
                        free(tmp as void*);
                        if (Mem.Compare(got, want, (128 as usize)) != 0) { moveBad = moveBad + 1; }
                        shift = shift + (1 as usize);
                    }

                    Console.PrintLine($"copyBad={copyBad} cmpBad={cmpBad} fillBad={fillBad} moveBad={moveBad}");
                }
            }
        }
        """;

    /// <summary>
    /// Debug first: the operations have to be right before the optimiser is asked about them.
    /// </summary>
    [Fact]
    public void MatchesByteWiseReference()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no checkout/compiler"); return; }

        var r = HostedRun.BuildAndRun(Program, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Contains("copyBad=0 cmpBad=0 fillBad=0 moveBad=0", r.Output);
    }

    /// <summary>
    /// And under the flag set that made the aliasing violation matter. A Release build is where a
    /// compiler is entitled to act on "these two loops cannot touch the same memory", so a
    /// regression here would show up at -O3 while Debug stayed green - the shape that reaches a
    /// shipped image and nothing else.
    /// </summary>
    [Fact]
    public void MatchesUnderStrictAliasing()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompilerAcceptingReleaseFlags();
        if (gata == null || cc == null) { Assert.Skip("no checkout, or no compiler taking the release flags"); return; }

        var r = HostedRun.BuildAndRun(
            new Dictionary<string, string> { ["main.g"] = Program }, gata, cc, release: true);

        Assert.True(r.ExitCode == 0, $"the release build exited {r.ExitCode}:\n{r.Output}");
        Assert.Contains("copyBad=0 cmpBad=0 fillBad=0 moveBad=0", r.Output);
    }
}
