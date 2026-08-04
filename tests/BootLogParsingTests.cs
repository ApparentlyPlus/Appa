namespace Appa.Tests;

/// <summary>
/// The boot log reader, tested without a boot.
/// </summary>
public class BootLogParsingTests
{
    /// <summary>
    /// The real shape of the failure: GRUB's "  Booting `GatOS'" loses its closing quote and its
    /// newline - they never reach the serial chardev, which was confirmed to happen before the
    /// kernel runs - and the kernel's first line continues that same line.
    /// </summary>
    [Fact]
    public void GluedFirstMarkerRead()
    {
        string glued = string.Join("\n",
            "Booting from DVD/CD...",
            "Welcome to GRUB!",
            "  Booting `GatOSR:box 1",
            "drop 1",
            "R:done");

        Assert.Equal(["R:box 1", "drop 1", "R:done"], BootTests.ProgramLines(glued));
    }

    /// <summary>
    /// And the clean capture reads identically, so the two are the same transcript rather than two
    /// spellings of one - which is the entire premise of comparing the images.
    /// </summary>
    [Fact]
    public void CleanAndGluedMatch()
    {
        string clean = string.Join("\n", "  Booting `GatOS'", "", "R:box 1", "drop 1", "R:done");
        string glued = string.Join("\n", "  Booting `GatOSR:box 1", "drop 1", "R:done");

        Assert.Equal(BootTests.ProgramLines(clean), BootTests.ProgramLines(glued));
    }

    /// <summary>
    /// Reading a marker from mid-line must not turn ordinary console text into one. 'ERROR:' ends
    /// in the letters a marker starts with, and a kernel that printed one would otherwise inject a
    /// phantom line into the comparison.
    /// </summary>
    [Theory]
    [InlineData("ERROR: something went wrong")]
    [InlineData("[DEBUG] SERIAL: ready")]
    [InlineData("Reached kernel idle loop")]
    [InlineData("dropped 3 packets")]
    [InlineData("Welcome to GRUB!")]
    public void ConsoleTextNotMarker(string line)
    {
        Assert.Empty(BootTests.ProgramLines(line));
    }

    /// <summary>
    /// The failure the comparison exists to catch still fails: a destructor announcement that moves
    /// or goes missing changes the sequence, glue or no glue.
    /// </summary>
    [Fact]
    public void ReorderedDropDiffers()
    {
        var reference = BootTests.ProgramLines("R:box 1\ndrop 1\ndrop 2\nR:done");

        Assert.NotEqual(reference, BootTests.ProgramLines("R:box 1\ndrop 1\nR:done"));
        Assert.NotEqual(reference, BootTests.ProgramLines("R:box 1\ndrop 2\ndrop 1\nR:done"));
        Assert.NotEqual(reference, BootTests.ProgramLines("  Booting `GatOSR:box 1\ndrop 1\nR:done"));
    }
}
