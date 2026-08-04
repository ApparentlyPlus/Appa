namespace Appa.Tests;

using System.Text.RegularExpressions;
using Appa;

/// <summary>
/// The book's Gata samples, run through the real parser. A language reference drifts silently -
/// nothing else in the tree reads it - and the two things that had already gone stale were a block
/// syntax that stopped being valid and a modifier the grammar never accepted.
/// </summary>
public partial class BookSamplesTests
{
    [GeneratedRegex(@"^```(\w*)\s*$")]
    private static partial Regex Fence();

    /// <summary>
    /// Locates the book beside the compiler checkout, or null when it is not part of this tree.
    /// </summary>
    private static string? FindBook()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var path = Path.Combine(d.FullName, "Gata", "The Gata Programming Language.md");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Every fenced 'go' block, with the source line it starts on.
    /// </summary>
    private static List<(int Line, string Source)> Samples(string book)
    {
        var lines = File.ReadAllText(book).Split('\n');
        var found = new List<(int, string)>();
        for (int i = 0; i < lines.Length;)
        {
            var m = Fence().Match(lines[i]);
            if (!m.Success) { i++; continue; }
            int start = i + 1, j = i + 1;
            while (j < lines.Length && !Fence().IsMatch(lines[j])) j++;
            if (m.Groups[1].Value == "go") found.Add((start + 1, string.Join("\n", lines[start..j])));
            i = j + 1;
        }
        return found;
    }

    /// <summary>
    /// The contexts a sample may be written for. A reference is mostly fragments: a few statements,
    /// a class body, a realm's contents. A sample is sound when it parses in any one of them, which
    /// is what keeps this pinned to "still valid Gata" rather than to how the prose is organised.
    /// </summary>
    private static IEnumerable<string> Contexts(string sample) =>
    [
        sample,
        $"realm kernel {{\nentry func Main() {{\n{sample}\n}}\n}}",
        $"realm kernel {{\n{sample}\nentry func Main() {{ }}\n}}",
        $"class _Wrap {{\n{sample}\n}}",
        $"realm kernel {{\nforeground process _P {{\n{sample}\n}}\nentry func Main() {{ }}\n}}",
        $"union _U {{\n{sample}\n}}",
        $"{sample}\nrealm kernel {{ entry func Main() {{ }} }}",
    ];

    /// <summary>
    /// Every sample parses somewhere. Type errors are not checked: a fragment names things the book
    /// declared paragraphs earlier, and demanding they resolve would pin the test to the prose.
    /// </summary>
    [Fact]
    public void SamplesAreValid()
    {
        var book = FindBook();
        if (book == null) { Assert.Skip("no sibling Gata checkout found; skipping book samples"); return; }

        var samples = Samples(book);
        Assert.True(samples.Count > 50, $"only {samples.Count} samples found; the extractor stopped matching");

        var failures = new List<string>();
        foreach (var (line, sample) in samples)
        {
            if (Parses(sample, out _)) continue;
            var parts = Split(sample);
            if (parts.Count > 1 && parts.All(p => Parses(p, out _))) continue;

            Parses(sample, out string? why);
            failures.Add($"book line {line}: {why}\n{sample}");
        }

        if (failures.Count == 0) return;
        var shown = string.Join("\n\n", failures.Take(10));
        var more = failures.Count > 10 ? $"\n\n... and {failures.Count - 10} more" : "";
        Assert.Fail($"{failures.Count} of {samples.Count} book samples no longer parse:\n\n{shown}{more}");
    }

    /// <summary>
    /// Splits a sample into the snippets it is really made of: blank-line separated, but only at
    /// brace depth zero. A class body has blank lines in it too, and cutting there leaves two
    /// halves neither of which is anything.
    /// </summary>
    private static List<string> Split(string sample)
    {
        var parts = new List<string>();
        var current = new List<string>();
        int depth = 0;

        foreach (var line in sample.Split('\n'))
        {
            if (line.Trim().Length == 0 && depth == 0)
            {
                if (current.Count > 0) { parts.Add(string.Join("\n", current)); current.Clear(); }
                continue;
            }
            current.Add(line);
            foreach (char c in StripTrailingComment(line))
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
        }
        if (current.Count > 0) parts.Add(string.Join("\n", current));
        return parts;
    }

    /// <summary>
    /// A line without its trailing '//' comment, so braces mentioned in prose do not move the depth.
    /// </summary>
    private static string StripTrailingComment(string line)
    {
        int at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    /// <summary>
    /// True when a snippet parses in any of the contexts a book sample may be written for. The
    /// reported reason is the last context's, since the bare form almost always fails and naming
    /// that would describe the wrapping rather than the snippet.
    /// </summary>
    private static bool Parses(string snippet, out string? why)
    {
        why = null;
        foreach (var candidate in Contexts(snippet))
        {
            try { SingleFileCompile.Parse(candidate); return true; }
            catch (ParseException ex) { why = $"{ex.Code} {ex.Message}"; }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
        }
        return false;
    }

    /// <summary>
    /// Syntax the language has moved past. A sample can parse and still be wrong: a realm written
    /// the old way is a free function called 'kernel' taking a block, which parses as something.
    /// </summary>
    [Theory]
    [InlineData(@"(?<!realm )(?<![\w.])kernel\s*\{", "a realm is written 'realm kernel { }'")]
    [InlineData(@"(?<![\w.])user\s*\{", "the userspace realm is written 'realm userspace { }'")]
    [InlineData(@"(?<![\w.])(public|private|static)\s+(class|module|enum|union)\b",
                "a top-level type takes no visibility modifier")]
    public void NoRetiredSyntax(string pattern, string why)
    {
        var book = FindBook();
        if (book == null) { Assert.Skip("no sibling Gata checkout found"); return; }

        var rx = new Regex(pattern);
        var bad = Samples(book).Where(s => rx.IsMatch(s.Source)).Select(s => s.Line).ToList();
        Assert.True(bad.Count == 0, $"{why} - book lines {string.Join(", ", bad)}");
    }
}
