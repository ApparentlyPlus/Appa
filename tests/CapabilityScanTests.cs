namespace Appa.Tests;

using Appa;

/// <summary>
/// What the GatOS image is built to carry. A capability the scan misses is not a build error - the
/// subsystem is simply left out, and the call that needed it answers whatever the stub answers. That
/// makes an under-declaration a silent wrong result on the real target and nowhere else, which is
/// the hardest kind to notice and the reason this is checked here rather than only by booting.
/// </summary>
public class CapabilityScanTests
{
    /// <summary>
    /// Binds the floor roles the scan keys on, so a call in the program under test is recognised as
    /// reaching the clock or the keyboard rather than being an ordinary function.
    /// </summary>
    private const string Floor = """
        @intrinsic(env_time)
        @extern int64 func _env_time_ns();

        @intrinsic(env_read)
        @extern int func _env_read();

        """;

    private static CapabilityScan Scan(string src)
    {
        var (diag, module) = SingleFileCompile.Check(Floor + src);
        Assert.False(diag.HasErrors, "fixture should check cleanly, but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
        Assert.NotNull(module);
        return new CapabilityScan(module).Run();
    }

    /// <summary>
    /// The control: a clock read on the path from the entry point is found.
    /// </summary>
    [Fact]
    public void EntryClockDeclaresTime() =>
        Assert.True(Scan("realm kernel { entry func Main() { let int64 t = _env_time_ns(); } }").Time);

    /// <summary>
    /// The defect. A destructor runs wherever an owner leaves scope, and the emitter synthesises that
    /// call - so no IR expression names it and the walk from the entry points never arrived. The same
    /// clock read moved into a '_deinit' left GATA_CAP_TIME out of the image, and the call then
    /// answered 0 instead of the uptime, booting cleanly the whole time.
    /// </summary>
    [Fact]
    public void DestructorClockDeclaresTime() =>
        Assert.True(Scan("""
            class Logger {
                public int id;
                public func _init(int i) { self.id = i; }
                func _deinit() { let int64 t = _env_time_ns(); }
            }
            realm kernel { entry func Main() { let Logger l = new Logger(1); } }
            """).Time);

    /// <summary>
    /// The same hole for input, and through a call the destructor makes rather than one it contains -
    /// so this pins that the destructor is a real walk root, not a single-level special case.
    /// </summary>
    [Fact]
    public void DestructorReachesCallees() =>
        Assert.True(Scan("""
            int func ReadKey() { return _env_read(); }
            class Prompt {
                public int id;
                public func _init(int i) { self.id = i; }
                func _deinit() { let int k = ReadKey(); }
            }
            realm kernel { entry func Main() { let Prompt p = new Prompt(1); } }
            """).Input);

    /// <summary>
    /// The other half: a program that touches neither must not have them declared, or the scan would
    /// be passing these tests by turning everything on.
    /// </summary>
    [Fact]
    public void NeitherUsedNeitherDeclared()
    {
        var caps = Scan("""
            class Plain {
                public int id;
                public func _init(int i) { self.id = i; }
                func _deinit() { self.id = 0; }
            }
            realm kernel { entry func Main() { let Plain p = new Plain(1); } }
            """);
        Assert.False(caps.Time);
        Assert.False(caps.Input);
        // Constructing one still needs the heap.
        Assert.True(caps.Mem);
    }

    #region Reference-counting mode

    /// <summary>
    /// Emits the source and returns shared.h.
    /// </summary>
    private static string SharedHeaderFor(string src)
    {
        var files = SingleFileCompile.Emit(Floor + src);
        Assert.NotEmpty(files);
        return files.Single(f => f.Name == "shared.h").Content;
    }

    /// <summary>
    /// Reference counts go atomic exactly when the program declares a process.
    /// </summary>
    [Fact]
    public void AtomicOnlyWithProcess()
    {
        Assert.Contains("#define GATA_RC_ATOMIC 0",
            SharedHeaderFor("class C { public int v; func _init() { self.v = 1; } } " +
                            "realm kernel { entry func Main() { let C c = new C(); } }"));

        Assert.Contains("#define GATA_RC_ATOMIC 1",
            SharedHeaderFor("class C { public int v; func _init() { self.v = 1; } } " +
                            "realm kernel { " +
                            "  background process P { thread T { entry func Run() { let C c = new C(); } } } " +
                            "  entry func Main() { let C c = new C(); } }"));
    }

    /// <summary>
    /// The definition is guarded so a '-D' on the command line wins.
    /// </summary>
    [Fact]
    public void CliForcesAtomic()
    {
        string header = SharedHeaderFor(
            "class C { public int v; func _init() { self.v = 1; } } " +
            "realm kernel { entry func Main() { let C c = new C(); } }");

        int guard = header.IndexOf("#ifndef GATA_RC_ATOMIC", StringComparison.Ordinal);
        int define = header.IndexOf("#define GATA_RC_ATOMIC", StringComparison.Ordinal);
        Assert.True(guard >= 0 && define > guard,
            $"the definition must sit inside '#ifndef', or -D cannot win:\n{header[..Math.Min(600, header.Length)]}");
    }

    /// <summary>
    /// And the toolchain does pass it whenever it resolves threads on, so the two halves meet.
    /// </summary>
    [Fact]
    public void ThreadsPassAtomicDefine()
    {
        var withThreads = Scan(
            "realm kernel { background process P { thread T { entry func Run() { } } } entry func Main() { } }");
        var manifest = new Manifest(".", "t", Target.GatOS, Mode.Debug, Output.Serial,
                                    Keyboard.Default, CapabilityDiscovery.On);

        var defines = Toolchain.CapabilityDefines(withThreads, manifest);
        Assert.Contains("-DGATA_CAP_THREADS", defines);
        Assert.Contains("-DGATA_RC_ATOMIC=1", defines);
    }

    #endregion
}
