namespace Appa.Tests;

/// <summary>
/// Asserts the per-translation-unit compiler flags for a GatOS build.
///
/// These are the flags whose effect is invisible in a build that succeeds. -ffast-math was applied
/// to all of userspace, the generated translation unit included: the ISO booted, every test
/// passed, and libgata's Math silently stopped honouring IEEE - Math.Mod(x, 0.0) returned 1.0
/// rather than NaN, signed zero collapsed, and the denormal paths in sqrt and scalbn ran on values
/// already flushed away. Nothing here needs a toolchain, so the choices are pinned wherever the
/// suite runs.
/// </summary>
public class ToolchainFlagsTests
{
    private static List<string> Flags(string rel, bool isMac = false) =>
        Toolchain.CFlagsFor(rel, "/src", GatosFlags.For(Mode.Debug), [], isMac);

    /// <summary>
    /// No translation unit is compiled under semantics other than the ones the language promises.
    /// Any flag that changes what an arithmetic expression means belongs on this list, and it
    /// applies to hand-written C as much as to the generated Gata: they share one libgata, so a
    /// Math routine reached from ulibc must answer the same way as one reached from uproc.
    /// </summary>
    [Theory]
    [InlineData("-ffast-math")]
    [InlineData("-ffinite-math-only")]
    [InlineData("-funsafe-math-optimizations")]
    [InlineData("-fno-signed-zeros")]
    [InlineData("-freciprocal-math")]
    [InlineData("-Ofast")]
    public void NoTranslationUnitGivesUpIeeeSemantics(string forbidden)
    {
        foreach (var rel in (string[])["kernel/kmain.c", "ulibc/string.c",
                                       Toolchain.GeneratedUserTu, .. GatosFlags.InterruptPath])
            Assert.DoesNotContain(forbidden, Flags(rel));
    }

    [Fact]
    public void GeneratedUserspaceIsRecognisedAsUserspace()
    {
        // It must still take the userspace path: that is what keeps it out of LTO.
        Assert.True(Toolchain.IsUserspace(Toolchain.GeneratedUserTu));
        Assert.DoesNotContain("-flto", Flags(Toolchain.GeneratedUserTu));
    }

    [Fact]
    public void KernelCodeIsLinkTimeOptimisedExceptOnMac()
    {
        Assert.Contains("-flto", Flags("kernel/kmain.c"));
        Assert.DoesNotContain("-flto", Flags("kernel/kmain.c", isMac: true));
    }

    /// <summary>
    /// The interrupt path runs before the FPU is in a usable state, so it must be compiled with no
    /// floating-point registers at all - and the generated Gata must never land in that set.
    /// </summary>
    [Fact]
    public void InterruptPathHasNoFpu()
    {
        Assert.NotEmpty(GatosFlags.InterruptPath);
        foreach (var rel in GatosFlags.InterruptPath)
        {
            Assert.False(Toolchain.IsUserspace(rel));
            foreach (var r in GatosFlags.FpuRestrictions) Assert.Contains(r, Flags(rel));
        }
        Assert.DoesNotContain(Toolchain.GeneratedUserTu, GatosFlags.InterruptPath);
    }
}
