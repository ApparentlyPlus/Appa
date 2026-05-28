namespace Appa;

/// <summary>
/// A TextSpan is a byte range [Start, Start+Length) that represents a location in the source (.g) text.
/// It will be used to report errors and warnings, and to highlight the relevant text in the source.
/// </summary>
readonly record struct TextSpan(int Start, int Length)
{
    public static readonly TextSpan None = new(-1, 0);
    public bool IsNone => Start < 0;
    public int End => Start + Length;

    /// <summary>
    /// The smallest span that contains both this and the other span.
    /// </summary>
    public TextSpan Merge(TextSpan other)
    {
        if (IsNone) return other;
        if (other.IsNone) return this;
        int ss = Math.Min(Start, other.Start);
        return new(ss, Math.Max(End, other.End) - ss);
    }
}
