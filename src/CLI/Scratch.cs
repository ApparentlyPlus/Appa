namespace Appa;

internal sealed class Scratch : IDisposable
{
    /// <summary>
    /// Absolute path of the directory, which exists for the lifetime of this object.
    /// </summary>
    public string Path { get; }

    private Scratch(string path) => Path = path;

    /// <summary>
    /// Creates a uniquely named, private directory under the system temp root. The prefix is for
    /// the human who finds one left behind after a kill -9, so name it after the work.
    /// </summary>
    public static Scratch Create(string prefix) =>
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
