namespace Appa;

using System.Text;

/// <summary>
/// Accumulates generated C text with indentation managed by the writer.
/// Depth changes only through Block and Braces scopes, and every line of
/// multi-line text is indented at the current depth so output cannot be
/// misindented by hand.
/// </summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _sb = new();
    private int _depth;
    private const string Unit = "    ";

    /// <summary>
    /// Appends a line of text at the current indentation depth.
    /// Multi-line text is split and each line is indented individually.
    /// An empty string appends a blank line with no leading whitespace.
    /// </summary>
    public void Line(string text = "")
    {
        if (text.Length == 0) { _sb.Append('\n'); return; }
        ReadOnlySpan<char> span = text.AsSpan();
        int index;
        while ((index = span.IndexOf('\n')) >= 0)
        {
            Indented(span[..index].TrimEnd('\r'));
            span = span[(index + 1)..];
        }
        Indented(span.TrimEnd('\r'));
    }

    /// <summary>
    /// Appends each string in the sequence as a separate indented line.
    /// </summary>
    public void Lines(params ReadOnlySpan<string> lines) { foreach (var l in lines) Line(l); }

    /// <summary>
    /// Appends a completely blank line with no indentation.
    /// </summary>
    public void Blank()
    {
        _sb.Append('\n');
    }

    /// <summary>
    /// Appends a line with the current indentation prefix, or a bare newline for empty input.
    /// </summary>
    private void Indented(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) { _sb.Append('\n'); return; }
        if (_depth > 0) _sb.Append(' ', _depth * 4);
        _sb.Append(s).Append('\n');
    }

    /// <summary>
    /// Appends the header line, increases indentation, and returns a scope that
    /// decreases indentation and writes the closer string on disposal.
    /// </summary>
    public Scope Block(string header, string closer = "}") { Line(header); _depth++; return new Scope(this, closer); }

    /// <summary>
    /// Opens a bare brace block, increasing indentation until the returned scope is disposed.
    /// </summary>
    public Scope Braces(string closer = "}")
    {
        return Block("{", closer);
    }

    /// <summary>
    /// Returns the accumulated C text.
    /// </summary>
    public override string ToString()
    {
        return _sb.ToString();
    }

    /// <summary>
    /// Disposable scope that restores indentation and writes the closing token on disposal.
    /// </summary>
    public readonly struct Scope(CodeWriter w, string closer) : IDisposable
    {
        /// <summary>
        /// Decreases indentation and writes the closing token.
        /// </summary>
        public void Dispose() { w._depth--; w.Line(closer); }
    }
}
