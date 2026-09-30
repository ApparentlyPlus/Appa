namespace Appa;

internal sealed class SourceText
{
    public string Path { get; }

    public string Text { get; }

    /// <summary>
    /// Offsets of where each line starts in the Text. Line N starts at _ls[N-1].
    /// </summary>
    private readonly int[] _ls;

    /// <summary>
    /// Constructs a SourceText from a path and text, computing the line start offsets.
    /// </summary>
    public SourceText(string path, string text)
    {
        Path = path;
        Text = text;

        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        _ls = starts.ToArray();
    }

    /// <summary>
    /// Takes in an offset between 0 and Text.Length, and returns the corresponding (line, column)
    /// pair. It treats \n as the line separator, and lines are 1-indexed. Columns are also
    /// 1-indexed.
    /// </summary>
    public (int Line, int Col) LineCol(int offset)
    {
        // protect against out of bounds offsets
        offset = Math.Clamp(offset, 0, Text.Length);

        // last line start that is <= offset
        int i = Array.BinarySearch(_ls, offset);
        if (i < 0) i = ~i - 1;

        return (i + 1, offset - _ls[i] + 1);
    }

    /// <summary>
    /// Returns the text of a given line number (1-indexed). If the line number is out of range,
    /// returns an empty span.
    /// </summary>
    public ReadOnlySpan<char> LineSpan(int line)
    {
        int i = line - 1;
        if (i < 0 || i >= _ls.Length) return default;

        // some inline case handling to avoid extra allocations
        int start = _ls[i];
        int end = i + 1 < _ls.Length ? _ls[i + 1] : Text.Length;
        return Text.AsSpan(start, end - start).TrimEnd("\r\n");
    }
}

/// <summary>
/// Every source file read during a build, keyed by absolute path, so the renderer can resolve a
/// diagnostic's Span back to its text.
/// </summary>
internal sealed class SourceSet
{
    private readonly Dictionary<string, SourceText> _files = new(StringComparer.OrdinalIgnoreCase);

    public SourceText Add(string path, string text) => _files[path] = new SourceText(path, text);

    public SourceText? Get(string? path) => path == null ? null : _files.GetValueOrDefault(path);
}
