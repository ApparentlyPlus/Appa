namespace Appa.Tests;

using Appa;

/// <summary>
/// The parts of 'appa install' / 'appa update' that can run without a network or a real install. The
/// installer had no tests at all, and it is the one piece of the product every user runs before any
/// of the rest of it exists.
/// </summary>
public class InstallerTests
{
    private static void Write(string dir, string name, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, name))!);
        File.WriteAllText(Path.Combine(dir, name), text);
    }

    /// <summary>
    /// libgata and envs are mirrors of the Gata repo, but the sync only ever added files. A module
    /// deleted or renamed upstream stayed installed forever - and a stale copy still imports and
    /// still binds whatever @intrinsic roles it declares, so a rename left two claimants for one
    /// role. Files the download did write must survive untouched.
    /// </summary>
    [Fact]
    public void StaleModulesPruned()
    {
        using var root = Scratch.Create("appa-prune-");
        string dir = root.Combine("libgata");
        Write(dir, "String.g", "current");
        Write(dir, "Int.g", "current");
        Write(dir, "Renamed.g", "stale");

        Installer.PruneStale(dir, [Path.Combine(dir, "String.g"), Path.Combine(dir, "Int.g")]);

        Assert.True(File.Exists(Path.Combine(dir, "String.g")));
        Assert.True(File.Exists(Path.Combine(dir, "Int.g")));
        Assert.False(File.Exists(Path.Combine(dir, "Renamed.g")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(dir, "String.g")));
    }

    /// <summary>
    /// Only .g files are the mirror's business. Anything else in the install directory is left alone
    /// rather than swept up by a sync of the standard library.
    /// </summary>
    [Fact]
    public void NonGataFilesKept()
    {
        using var root = Scratch.Create("appa-prune-");
        string dir = root.Combine("libgata");
        Write(dir, "String.g");
        Write(dir, "README.md");
        Write(dir, "notes.txt");

        Installer.PruneStale(dir, [Path.Combine(dir, "String.g")]);

        Assert.True(File.Exists(Path.Combine(dir, "README.md")));
        Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
    }

    /// <summary>
    /// A nested layout prunes at depth, and a directory that does not exist is not an error - the
    /// first install creates it after this would run.
    /// </summary>
    [Fact]
    public void PruneHandlesNesting()
    {
        using var root = Scratch.Create("appa-prune-");
        string dir = root.Combine("libgata");
        Write(dir, Path.Combine("sub", "Kept.g"));
        Write(dir, Path.Combine("sub", "Gone.g"));

        Installer.PruneStale(dir, [Path.Combine(dir, "sub", "Kept.g")]);
        Assert.True(File.Exists(Path.Combine(dir, "sub", "Kept.g")));
        Assert.False(File.Exists(Path.Combine(dir, "sub", "Gone.g")));

        Installer.PruneStale(root.Combine("does-not-exist"), []);
    }

    /// <summary>
    /// Nothing written means nothing kept, which is what a mirror of an emptied directory means. The
    /// downloader refuses to report an empty prefix in the first place, so this only runs when that
    /// really is the upstream state.
    /// </summary>
    [Fact]
    public void EmptySetRemovesAll()
    {
        using var root = Scratch.Create("appa-prune-");
        string dir = root.Combine("libgata");
        Write(dir, "A.g");
        Write(dir, "B.g");

        Installer.PruneStale(dir, []);

        Assert.Empty(Directory.GetFiles(dir, "*.g"));
    }

    /// <summary>
    /// A failed download used to reach the last-resort net and print "internal compiler error" with a
    /// stack trace. Being offline, rate-limited, or unable to write the install directory are ordinary
    /// outcomes of an installer, and each has a different thing to try next.
    /// </summary>
    [Theory]
    [InlineData(typeof(System.Net.Http.HttpRequestException), "network")]
    [InlineData(typeof(TaskCanceledException), "network")]
    [InlineData(typeof(UnauthorizedAccessException), "permissions")]
    [InlineData(typeof(System.IO.InvalidDataException), "corrupt")]
    public void SetupFailuresAdvised(Type exceptionType, string expectedHint)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.True(Installer.IsExpectedSetupFailure(ex), $"{exceptionType.Name} should not reach the internal-error net");
        Assert.Contains(expectedHint, Installer.SetupFailureHint(ex));
        Assert.Contains("appa install", Installer.SetupFailureHint(ex));
    }

    /// <summary>
    /// A rate-limited fetch is the one case with a specific remedy rather than "try again".
    /// </summary>
    [Fact]
    public void RateLimitPointsAtToken()
    {
        var ex = new InvalidOperationException("GitHub API rate limit exhausted; reset is 42 minutes away");
        Assert.True(Installer.IsExpectedSetupFailure(ex));
        Assert.Contains("GITHUB_TOKEN", Installer.SetupFailureHint(ex));
    }

    /// <summary>
    /// A genuine compiler bug during setup must still reach the internal-error net rather than being
    /// dressed up as an install problem.
    /// </summary>
    [Fact]
    public void UnexpectedFailureNotInstall() =>
        Assert.False(Installer.IsExpectedSetupFailure(new NullReferenceException()));

    /// <summary>
    /// GitHub's archive zips wrap everything in a single "repo-branch/" folder that the install must
    /// flatten away, and only the top-level directories inside it are the template. Untested until
    /// now, and it deletes the existing template before extracting, so getting the shape wrong loses
    /// the installed one.
    /// </summary>
    [Fact]
    public void ExtractFlattensWrapper()
    {
        using var root = Scratch.Create("appa-tmpl-");
        string staging = root.Combine("make");
        Directory.CreateDirectory(Path.Combine(staging, "GatOS-appa-template", "src", "kernel"));
        Directory.CreateDirectory(Path.Combine(staging, "GatOS-appa-template", "targets"));
        File.WriteAllText(Path.Combine(staging, "GatOS-appa-template", "src", "kernel", "main.c"), "kernel");
        File.WriteAllText(Path.Combine(staging, "GatOS-appa-template", "targets", "linker.ld"), "link");
        File.WriteAllText(Path.Combine(staging, "GatOS-appa-template", "README.md"), "loose");

        string zip = root.Combine("t.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(staging, zip);

        string dest = root.Combine("template");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "stale.txt"), "from a previous install");

        Installer.ExtractTemplate(zip, dest);

        Assert.Equal("kernel", File.ReadAllText(Path.Combine(dest, "src", "kernel", "main.c")));
        Assert.Equal("link", File.ReadAllText(Path.Combine(dest, "targets", "linker.ld")));
        Assert.False(Directory.Exists(Path.Combine(dest, "GatOS-appa-template")));
        Assert.False(File.Exists(Path.Combine(dest, "README.md")));
        Assert.False(File.Exists(Path.Combine(dest, "stale.txt")));
    }

    /// <summary>
    /// A zip with no single wrapper folder - which is what a hand-built archive looks like - is taken
    /// as already being the root rather than having a level stripped off it.
    /// </summary>
    [Fact]
    public void ExtractAcceptsUnwrapped()
    {
        using var root = Scratch.Create("appa-tmpl-");
        string staging = root.Combine("make");
        Directory.CreateDirectory(Path.Combine(staging, "src"));
        Directory.CreateDirectory(Path.Combine(staging, "targets"));
        File.WriteAllText(Path.Combine(staging, "src", "a.c"), "a");
        File.WriteAllText(Path.Combine(staging, "targets", "b.ld"), "b");

        string zip = root.Combine("t.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(staging, zip);

        string dest = root.Combine("template");
        Installer.ExtractTemplate(zip, dest);

        Assert.Equal("a", File.ReadAllText(Path.Combine(dest, "src", "a.c")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "targets", "b.ld")));
    }

    /// <summary>
    /// appa's staging directories used to be fixed names in the shared temp directory
    /// (/tmp/appa_tc.zip). With fs.protected_regular - on by default - a file there owned by another
    /// user cannot be re-created, root included, so installing as the user and then re-running under
    /// sudo failed on the leftover the *user* owned, reported as a permissions problem in the
    /// install directory. Every run must get its own directory, or that comes back.
    /// </summary>
    [Fact]
    public void ScratchIsPrivatePerRun()
    {
        using var a = Scratch.Create("appa-test-");
        using var b = Scratch.Create("appa-test-");

        Assert.NotEqual(a.Path, b.Path);
        foreach (var s in new[] { a, b })
        {
            Assert.True(Directory.Exists(s.Path));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(s.Path));
            // Fresh, so nothing left by an earlier run (or another user) is in the way.
            Assert.Empty(Directory.GetFileSystemEntries(s.Path));
        }
    }

    /// <summary>
    /// The point of the type: the directory is gone after the using, with everything staged in it.
    /// Every temp path appa creates goes through this, so a leak here is a leak everywhere.
    /// </summary>
    [Fact]
    public void ScratchDeletesItselfWithItsContents()
    {
        string path;
        using (var s = Scratch.Create("appa-test-"))
        {
            path = s.Path;
            Directory.CreateDirectory(s.Combine("nested", "deeper"));
            File.WriteAllText(s.Combine("nested", "deeper", "big.o"), "object file");
        }
        Assert.False(Directory.Exists(path));
    }
}
