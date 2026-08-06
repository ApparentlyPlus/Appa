namespace Appa;

using System.Text;

static class Fmt
{
    // The narrowest and widest text column appa will lay out into.
    public const int MinWidth = 48, MaxWidth = 96;

    // The standard indent for anything nested under a heading.
    public const string Indent = "  ";

    // Columns between a table's left column and its descriptions.
    private const int Gutter = 2;

    /// <summary>
    /// The usable text width: the terminal's, clamped to something readable, and a fixed default
    /// when there is no terminal to ask (a pipe, a test harness, a CI log).
    /// </summary>
    public static int Width
    {
        get
        {
            int w;
            try { w = Console.IsOutputRedirected ? 80 : Console.WindowWidth - 1; }
            catch (IOException) { w = 80; }
            return Math.Clamp(w, MinWidth, MaxWidth);
        }
    }

    /// <summary>
    /// The width a string actually occupies on screen: SGR colour escapes are counted as nothing,
    /// because that is what the terminal does with them.
    /// </summary>
    public static int Visible(string s)
    {
        int len = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\x1b')
            {
                while (i < s.Length && s[i] != 'm') i++;
                continue;
            }
            len++;
        }
        return len;
    }

    /// <summary>
    /// Pads to a visible width, so a column stays aligned whether or not its cells are coloured.
    /// </summary>
    public static string Pad(string s, int width)
    {
        int pad = width - Visible(s);
        return pad > 0 ? s + new string(' ', pad) : s;
    }

    /// <summary>
    /// Greedy word wrap at a visible width. Explicit newlines in the input are honoured as
    /// paragraph breaks; everything else is free to reflow.
    /// </summary>
    public static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var sb = new StringBuilder();
            int len = 0;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int w = Visible(word);
                if (len > 0 && len + 1 + w > width) { lines.Add(sb.ToString()); sb.Clear(); len = 0; }
                if (len > 0) { sb.Append(' '); len++; }
                sb.Append(word);
                len += w;
            }
            lines.Add(sb.ToString());
        }
        return lines;
    }

    /// <summary>
    /// Writes a paragraph wrapped to the terminal, every line carrying the given indent.
    /// </summary>
    public static void Para(string text, string indent = Indent)
    {
        foreach (var line in Wrap(text, Width - indent.Length))
            Console.WriteLine(line.Length == 0 ? "" : indent + line);
    }

    /// <summary>
    /// Writes a two-column table: the left column sized to its widest cell, the descriptions
    /// wrapped into whatever is left and hanging-indented under themselves.
    /// </summary>
    public static void Table(IReadOnlyList<(string Left, string Right)> rows, string indent = Indent)
    {
        if (rows.Count == 0) return;
        int left = rows.Where(r => r.Right.Length > 0).Select(r => Visible(r.Left)).DefaultIfEmpty(0).Max();
        int right = Math.Max(MinWidth / 2, Width - indent.Length - left - Gutter);
        string hang = indent + new string(' ', left + Gutter);

        foreach (var (l, r) in rows)
        {
            if (r.Length == 0) { Para(l, indent); continue; }
            var wrapped = Wrap(r, right);
            Console.WriteLine($"{indent}{Pad(l, left)}{new string(' ', Gutter)}{wrapped[0]}");
            for (int i = 1; i < wrapped.Count; i++) Console.WriteLine(hang + wrapped[i]);
        }
    }

    /// <summary>
    /// Writes a line with something pinned to the right edge - a label and its elapsed time.
    /// </summary>
    public static void Justify(string left, string right, string indent = Indent)
    {
        int avail = Width - indent.Length;
        int rw = Visible(right);

        var lines = Wrap(left, avail - rw - 2);
        for (int i = 0; i < lines.Count - 1; i++) Console.WriteLine(indent + lines[i]);

        string last = lines[^1];
        int lastW = Visible(last);
        if (lastW + 2 + rw <= avail)
            Console.WriteLine(indent + last + new string(' ', avail - lastW - rw) + right);
        else
        {
            Console.WriteLine(indent + last);
            Console.WriteLine(indent + new string(' ', Math.Max(0, avail - rw)) + right);
        }
    }

    /// <summary>
    /// A section heading: coloured, flush left, with a blank line above it so blocks separate
    /// themselves without callers counting newlines.
    /// </summary>
    public static void Section(string title, string? note = null)
    {
        Console.WriteLine();
        Console.WriteLine($"{C.FOREST}{title}{C.NC}{(note is null ? "" : $" {C.DIM}{note}{C.NC}")}");
    }
}
