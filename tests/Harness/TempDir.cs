namespace Appa.Tests;

/// <summary>
/// A scratch directory that deletes itself at the end of the enclosing 'using'.
///
/// Several suites shell out to gcc, QEMU or the whole appa driver and so need real files on
/// disk. Routing every one of them through this makes cleanup structural rather than a
/// try/finally each test has to remember: a test that throws mid-assert still removes its
/// directory, and a test that forgets the 'using' fails to compile rather than leaking.
///
/// Deletion is best-effort. A held file handle on Windows, or a subprocess that outlived its
/// timeout and still owns a file, must not turn a passing test red on the way out.
/// </summary>
internal sealed class TempDir : IDisposable
{
    /// <summary>Absolute path of the directory, which exists for the lifetime of this object.</summary>
    public string Path { get; }

    private TempDir(string path) => Path = path;

    /// <summary>Creates a uniquely named directory under the system temp root.</summary>
    public static TempDir Create(string prefix) =>
        new(Directory.CreateTempSubdirectory(prefix).FullName);

    /// <summary>Combines a relative path against this directory.</summary>
    public string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
