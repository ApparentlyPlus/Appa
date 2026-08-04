namespace Appa.Tests;

/// <summary>
/// libgata's hash containers must only resize when they actually gain an entry.
/// </summary>
public class ContainerGrowthTests
{
    private const string Program = """
        import Console;
        import String;
        import Map;
        import Set;

        realm userspace {
            entry func Main() {
                // Fill each container until it sits exactly at its load threshold, then do
                // nothing but overwrite. Capacity must not move.
                let Map[int, int] m = new Map[int, int]();
                let int i = 0;
                m.Put(i, i); i = i + 1;
                while (m.Length() * 10 < m.Capacity() * 7) { m.Put(i, i); i = i + 1; }
                let int mcap = m.Capacity();
                let int mlen = m.Length();
                for (let int r = 0; r < 50; r++) { for (let int k = 0; k < i; k++) { m.Put(k, k + r); } }
                Console.PrintLine($"map {mcap} {m.Capacity()} {mlen} {m.Length()} {m.Get(0)}");

                let StringMap[int] sm = new StringMap[int]();
                let int j = 0;
                sm.Put($"k{j}", j); j = j + 1;
                while (sm.Length() * 10 < sm.Capacity() * 7) { sm.Put($"k{j}", j); j = j + 1; }
                let int smcap = sm.Capacity();
                let int smlen = sm.Length();
                for (let int r = 0; r < 50; r++) { for (let int k = 0; k < j; k++) { sm.Put($"k{k}", k + r); } }
                Console.PrintLine($"smap {smcap} {sm.Capacity()} {smlen} {sm.Length()} {sm.Get($"k0")}");

                let Set[int] s = new Set[int]();
                let int p = 0;
                s.Add(p); p = p + 1;
                while (s.Length() * 10 < s.Capacity() * 7) { s.Add(p); p = p + 1; }
                let int scap = s.Capacity();
                let int slen = s.Length();
                for (let int r = 0; r < 50; r++) { for (let int k = 0; k < p; k++) { s.Add(k); } }
                Console.PrintLine($"set {scap} {s.Capacity()} {slen} {s.Length()}");

                let StringSet ss = new StringSet();
                let int q = 0;
                ss.Add($"s{q}"); q = q + 1;
                while (ss.Length() * 10 < ss.Capacity() * 7) { ss.Add($"s{q}"); q = q + 1; }
                let int sscap = ss.Capacity();
                let int sslen = ss.Length();
                for (let int r = 0; r < 50; r++) { for (let int k = 0; k < q; k++) { ss.Add($"s{k}"); } }
                Console.PrintLine($"sset {sscap} {ss.Capacity()} {sslen} {ss.Length()}");

                // And growth still happens when entries really are added, so a container that
                // simply never grew would not pass.
                let Map[int, int] g = new Map[int, int]();
                for (let int k = 0; k < 500; k++) { g.Put(k, k); }
                Console.PrintLine($"grew {g.Length()} {g.Capacity() > 500}");
            }
        }
        """;

    [Fact]
    public void OverwriteNeverResizes()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no checkout/compiler"); return; }

        var r = HostedRun.BuildAndRun(Program, gata, cc);
        HostedRun.AssertClean(r);

        foreach (var line in r.Output.Split('\n').Select(l => l.Trim()))
        {
            var f = line.Split(' ');
            switch (f[0])
            {
                case "map" or "smap":
                    Assert.Equal(f[1], f[2]);
                    Assert.Equal(f[3], f[4]);
                    Assert.Equal("49", f[5]);
                    break;
                case "set" or "sset":
                    Assert.Equal(f[1], f[2]);
                    Assert.Equal(f[3], f[4]);
                    break;
                case "grew":
                    Assert.Equal("500", f[1]);
                    Assert.Equal("true", f[2]);
                    break;
            }
        }

        foreach (var tag in (string[])["map", "smap", "set", "sset", "grew"])
            Assert.Contains(r.Output.Split('\n').Select(l => l.Trim()), l => l.StartsWith(tag + " ", StringComparison.Ordinal));
    }
}
