namespace Appa.Tests;

/// <summary>
/// One program, written as several files and as one, required to compute the same answers.
/// </summary>
public class FileSplitEquivalenceTests
{
    [Fact]
    public void SplitMatchesSingle()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no Gata checkout or C compiler found"); return; }

        var split = ReleaseModeExecutionTests.Files();
        var joined = Collapse(split);

        var a = HostedRun.BuildAndRun(split, gata, cc);
        HostedRun.AssertClean(a);
        var b = HostedRun.BuildAndRun(joined, gata, cc);
        HostedRun.AssertClean(b);

        var la = Lines(a.Output);
        var lb = Lines(b.Output);
        Assert.True(la.Count > 0, "the split build printed nothing; the program stopped working");
        if (la.SequenceEqual(lb)) return;

        var diff = new List<string>();
        for (int i = 0; i < Math.Max(la.Count, lb.Count); i++)
        {
            string x = i < la.Count ? la[i] : "<none>";
            string y = i < lb.Count ? lb[i] : "<none>";
            if (x != y) diff.Add($"  line {i + 1}: split '{x}' vs single '{y}'");
        }
        Assert.Fail($"the same program computed different things split and joined:\n{string.Join("\n", diff.Take(20))}");
    }

    /// <summary>
    /// Concatenates the files into one main.g in dependency order, dropping the project-internal
    /// imports that no longer have a file to name. Library imports are kept and deduplicated,
    /// since importing one twice is legal but says nothing here.
    /// </summary>
    private static Dictionary<string, string> Collapse(IReadOnlyDictionary<string, string> files)
    {
        var imports = new List<string>();
        var body = new System.Text.StringBuilder();

        // main.g last: it holds the realm, and the others are what it imports.
        foreach (var name in files.Keys.Where(k => k != "main.g").Concat(["main.g"]))
        {
            foreach (var line in files[name].Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("import \"", StringComparison.Ordinal)) continue;
                if (t.StartsWith("import ", StringComparison.Ordinal))
                {
                    if (!imports.Contains(t)) imports.Add(t);
                    continue;
                }
                body.AppendLine(line);
            }
        }
        return new Dictionary<string, string>
        {
            ["main.g"] = string.Join("\n", imports) + "\n\n" + body,
        };
    }

    /// <summary>
    /// The program's own output lines, with sanitizer chatter dropped.
    /// </summary>
    private static List<string> Lines(string output) =>
        [.. output.Split('\n').Select(l => l.Trim())
                  .Where(l => l.Length > 0 && !l.StartsWith('=') && !l.Contains("Sanitizer", StringComparison.Ordinal))];
}
