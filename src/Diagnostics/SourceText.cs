namespace Appa;

/// <summary>
/// SourceText is a class representing one source file. It stores the text, the path, 
/// and a precomputed array of line start offsets for fast line/column lookups.
/// </summary>
sealed class SourceText
{
    /// <summary>
    /// The absolute path of the source file
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The full text of the source file
    /// </summary>
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

        ReadOnlySpan<char> span = text.AsSpan();
        int count = 1;
        int offset = 0;
        int index;

        // Count newlines using vectorized search
        while ((index = span[offset..].IndexOf('\n')) >= 0)
        {
            count++;
            offset += index + 1;
        }

        // Allocate the exact size needed for the line start offsets (avoid allocs)
        int[] lineStarts = new int[count];
        lineStarts[0] = 0;
        int lineIndex = 1;
        offset = 0;

        // Populate offsets search
        while ((index = span[offset..].IndexOf('\n')) >= 0)
        {
            offset += index + 1;
            lineStarts[lineIndex++] = offset;
        }

        _ls = lineStarts;
    }

    /// <summary>
    /// Takes in an offset between 0 and Text.Length, and returns the corresponding (line, column) pair.
    /// It treats \n as the line separator, and lines are 1-indexed. Columns are also 1-indexed.
    /// </summary>
    public (int Line, int Col) LineCol(int offset)
    {
        // protect against out of bounds offsets
        offset = Math.Clamp(offset, 0, Text.Length);

        // binsearch for the largest line start that is <= offset
        int index = _ls.AsSpan().BinarySearch(offset);
        int lo = index >= 0 ? index : ~index - 1;
        return (lo + 1, offset - _ls[lo] + 1);
    }

    /// <summary>
    /// Returns the text of a given line number (1-indexed). If the line number is out of range, returns an empty span.
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

    /// <summary>
    /// Returns the text of a given line number (1-indexed). If the line number is out of range, returns an empty string.
    /// </summary>
    public string LineText(int line)
    {
        // Slices first, performing at most one string allocation
        ReadOnlySpan<char> span = LineSpan(line);
        return span.IsEmpty ? "" : new string(span);
    }
}

/// <summary>
/// Every source file read during a build, keyed by absolute path, so the renderer
/// can resolve a diagnostic's Span back to its text.
/// </summary>
sealed class SourceSet
{
    private readonly Dictionary<string, SourceText> _ff = new(StringComparer.OrdinalIgnoreCase);
    public SourceText Add(string path, string text) => _ff[path] = new SourceText(path, text);
    public SourceText? Get(string? path) => path != null ? _ff.GetValueOrDefault(path) : null;
}
