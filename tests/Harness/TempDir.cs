namespace Appa.Tests;

/// <summary>
/// A scratch directory that deletes itself at the end of the enclosing 'using', so cleanup is
/// structural rather than a try/finally each test must remember. Best-effort: a held handle must
/// not turn a passing test red on the way out.
/// </summary>
internal sealed class TempDir : IDisposable
{
    /// <summary>
    /// Absolute path of the directory, which exists for the lifetime of this object.
    /// </summary>
    public string Path { get; }

    private TempDir(string path) => Path = path;

    /// <summary>
    /// Creates a uniquely named directory under the system temp root.
    /// </summary>
    public static TempDir Create(string prefix) =>
        new(Directory.CreateTempSubdirectory(prefix).FullName);

    /// <summary>
    /// Combines a relative path against this directory.
    /// </summary>
    public string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
