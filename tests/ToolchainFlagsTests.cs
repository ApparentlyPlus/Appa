namespace Appa.Tests;

/// <summary>
/// Asserts the per-translation-unit compiler flags for a GatOS build.
/// </summary>
public class ToolchainFlagsTests
{
    private static List<string> Flags(string rel, bool isMac = false) =>
        Toolchain.CFlagsFor(rel, "/src", GatosFlags.For(Mode.Debug), [], isMac);

    /// <summary>
    /// No translation unit is compiled under semantics other than the ones the language promises.
    /// Any flag that changes what an arithmetic expression means belongs on this list, and it
    /// applies to hand-written C as much as to the generated Gata.
    /// </summary>
    [Theory]
    [InlineData("-ffast-math")]
    [InlineData("-ffinite-math-only")]
    [InlineData("-funsafe-math-optimizations")]
    [InlineData("-fno-signed-zeros")]
    [InlineData("-freciprocal-math")]
    [InlineData("-Ofast")]
    public void IeeeSemanticsKept(string forbidden)
    {
        foreach (var rel in (string[])["kernel/kmain.c", "ulibc/string.c",
                                       Toolchain.GeneratedUserTu, .. GatosFlags.InterruptPath])
            Assert.DoesNotContain(forbidden, Flags(rel));
    }

    [Fact]
    public void UserspaceRecognised()
    {
        Assert.True(Toolchain.IsUserspace(Toolchain.GeneratedUserTu));
        Assert.DoesNotContain("-flto", Flags(Toolchain.GeneratedUserTu));
    }

    [Fact]
    public void KernelLtoExceptMac()
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

    /// <summary>
    /// A build's artifacts are filed under the project's name, so the name has to be usable as one
    /// path component - and a .gconf is a text file anyone can edit.
    /// </summary>
    [Theory]
    [InlineData("myos", "myos")]
    [InlineData("MyOS_2", "MyOS_2")]
    [InlineData("my os", "my_os")]
    [InlineData("my   os", "my_os")]
    [InlineData("a/b", "a_b")]
    [InlineData("../../etc/passwd", "etc_passwd")]
    [InlineData("C:\\os", "C_os")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("kernel.v2", "kernel.v2")]
    [InlineData("..", "image")]
    [InlineData("///", "image")]
    [InlineData("", "image")]
    public void ArtifactStemIsAFilename(string projectName, string expected)
    {
        string stem = Toolchain.ArtifactStem(projectName);
        Assert.Equal(expected, stem);
        Assert.Equal(stem, Path.GetFileName(stem));
        Assert.Equal(-1, stem.IndexOfAny(Path.GetInvalidFileNameChars()));
    }
}
