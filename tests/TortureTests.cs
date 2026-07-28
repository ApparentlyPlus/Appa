namespace Appa.Tests;

using System.Text;

/// <summary>
/// Whole-compiler invariants over a corpus of plausible but garbage programs: nothing crashes,
/// every diagnostic is well-formed, and anything clean emits valid C. One [Fact] sweeps the corpus
/// and reports every violation at once, since a regression is usually a class.
/// </summary>
public class TortureTests
{
    #region Sweep plumbing

    /// <summary>
    /// Collects failure messages across a corpus sweep and turns them into a single assertion,
    /// capped so one broken invariant can't produce a megabyte of output.
    /// </summary>
    private sealed class Failures
    {
        private readonly List<string> _items = [];
        private const int MaxShown = 25;

        public void Add(string msg) => _items.Add(msg);

        public void Assert(string what)
        {
            if (_items.Count == 0) return;
            var shown = string.Join("\n\n", _items.Take(MaxShown));
            var more = _items.Count > MaxShown ? $"\n\n... and {_items.Count - MaxShown} more" : "";
            Xunit.Assert.Fail($"{_items.Count} {what}:\n\n{shown}{more}");
        }
    }

    /// <summary>
    /// Runs a source through the whole front end, turning an escaping exception into a message
    /// rather than tearing down the sweep. Mirrors Program.RunFrontEnd, not
    /// SingleFileCompile.Check, which stops before the Validate* passes.
    /// </summary>
    private static (DiagnosticBag? Diag, IrModule? Module, string? Crash) TryCheck(string src)
    {
        const string path = "<torture>";
        try
        {
            var sources = new SourceSet();
            sources.Add(path, src);
            var diag = new DiagnosticBag(sources);

            Program? prog = null;
            try { prog = SingleFileCompile.Parse(src); }
            catch (ParseException ex) { diag.Error(ex.Code, path, ex.Span, ex.Message, ex.Hints); }
            if (prog == null) return (diag, null, null);

            var programs = new List<(string path, Program prog)> { (path, prog) };
            var visible = new Dictionary<string, HashSet<string>> { [path] = [path] };
            var (module, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);

            // ValidateEnvironment and ValidateFloor are skipped: both require a real
            // @environment file with floor binds, which a single-file corpus case has
            // no way to supply, so they would fire on every case and mask everything else.
            Pipeline.ValidateIntrinsics(module, diag);
            Pipeline.ValidateStructure(programs, null, diag);
            return (diag, module, null);
        }
        catch (Exception ex)
        {
            return (null, null, Describe(ex));
        }
    }

    /// <summary>
    /// Emits a checked module, returning the crash description instead of throwing.
    /// </summary>
    private static (IReadOnlyList<OutputFile>? Files, string? Crash) TryEmit(IrModule module, DiagnosticBag diag)
    {
        try { return (Layout.Compose(new Emitter(module, diag).Build(), module.Symbols), null); }
        catch (Exception ex) { return (null, Describe(ex)); }
    }

    /// <summary>
    /// Formats an exception as "Type: message @ top frame" for a failure message.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var frame = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "<no stack>";
        return $"{ex.GetType().Name}: {ex.Message.Replace('\n', ' ')} @ {frame}";
    }

    /// <summary>
    /// Trims a source down to something that fits in a failure message.
    /// </summary>
    private static string Excerpt(string src) =>
        src.Length <= 400 ? src : src[..400] + " ...";

    #endregion

    /// <summary>
    /// The core invariant: no program, however malformed, makes the compiler throw. A crash here is
    /// always a compiler bug, never a property of the input.
    /// </summary>
    [Fact]
    public void NoCorpusCaseCrashesTheCompiler()
    {
        var fails = new Failures();
        foreach (var c in TortureCorpus.All)
        {
            var (diag, module, crash) = TryCheck(c.Source);
            if (crash != null) { fails.Add($"[{c.Name}] front end threw -- {crash}\n{Excerpt(c.Source)}"); continue; }
            if (diag!.HasErrors || module == null) continue;

            var (_, emitCrash) = TryEmit(module, diag);
            if (emitCrash != null) fails.Add($"[{c.Name}] emitter threw -- {emitCrash}\n{Excerpt(c.Source)}");
        }
        fails.Assert("corpus cases crashed the compiler");
    }

    /// <summary>
    /// Every diagnostic must be usable: a code the compiler declares, a non-empty single-line
    /// message, and a span that actually points into the source. A span of the wrong length or past
    /// the end of the file renders a caret under nothing.
    /// </summary>
    [Fact]
    public void AllDiagnosticsAreWellFormed()
    {
        var known = new HashSet<string>(typeof(Codes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!));

        var fails = new Failures();
        foreach (var c in TortureCorpus.All)
        {
            var (diag, _, crash) = TryCheck(c.Source);
            if (crash != null) continue; // owned by NoCorpusCaseCrashesTheCompiler

            foreach (var d in diag!.All)
            {
                string where = $"[{c.Name}] {d.Code}";
                if (!known.Contains(d.Code)) fails.Add($"{where}: undeclared diagnostic code");
                if (string.IsNullOrWhiteSpace(d.Message)) fails.Add($"{where}: empty message");
                else if (d.Message.Contains('\n')) fails.Add($"{where}: multi-line message -- '{d.Message}'");

                // TextSpan.None is the deliberate "this diagnostic is about the build,
                // not a place in a file" marker, and an empty file has nothing to point at.
                if (d.Loc.Span == TextSpan.None || c.Source.Length == 0) continue;
                if (d.Loc.Span.Start < 0 || d.Loc.Span.Start > c.Source.Length)
                    fails.Add($"{where}: span start {d.Loc.Span.Start} outside [0,{c.Source.Length}] -- '{d.Message}'");
                else if (d.Loc.Span.Start + d.Loc.Span.Length > c.Source.Length)
                    fails.Add($"{where}: span end {d.Loc.Span.Start + d.Loc.Span.Length} past EOF " +
                              $"{c.Source.Length} -- '{d.Message}'");
            }
        }
        fails.Assert("malformed diagnostics");
    }

    /// <summary>
    /// Cases the corpus marks <see cref="Expect.Rejected"/> must produce an error, and the named
    /// one where a code is given. A case that silently passes is a missing diagnostic - the
    /// compiler is about to emit C for nonsense.
    /// </summary>
    [Fact]
    public void CorpusExpectationsHold()
    {
        var fails = new Failures();
        foreach (var c in TortureCorpus.All)
        {
            if (c.Expect == Expect.Any) continue;

            var (diag, _, crash) = TryCheck(c.Source);
            if (crash != null) continue; // owned by NoCorpusCaseCrashesTheCompiler

            var errors = diag!.All.Where(d => d.Severity == Severity.Error).ToList();
            var got = errors.Count == 0 ? "no errors" : string.Join("; ", errors.Select(e => $"{e.Code} {e.Message}"));

            if (c.Expect == Expect.Rejected)
            {
                if (errors.Count == 0)
                    fails.Add($"[{c.Name}] expected an error, got none\n{Excerpt(c.Source)}");
                else if (c.Code != null && !errors.Any(e => e.Code == c.Code))
                    fails.Add($"[{c.Name}] expected {c.Code}, got: {got}\n{Excerpt(c.Source)}");
            }
            else if (errors.Count > 0)
            {
                fails.Add($"[{c.Name}] expected clean, got: {got}\n{Excerpt(c.Source)}");
            }
        }
        fails.Assert("unmet corpus expectations");
    }

    /// <summary>
    /// Anything that checks clean must emit C that is at least structurally sound: balanced
    /// delimiters, no placeholder names left over from a failed lookup, and no empty aggregate
    /// bodies (a constraint violation gcc rejects).
    /// </summary>
    [Fact]
    public void EmittedCIsStructurallyValid()
    {
        var fails = new Failures();
        foreach (var c in TortureCorpus.All)
        {
            var (diag, module, crash) = TryCheck(c.Source);
            if (crash != null || diag!.HasErrors || module == null) continue;
            var (files, emitCrash) = TryEmit(module, diag);
            if (emitCrash != null) continue; // owned by NoCorpusCaseCrashesTheCompiler

            foreach (var f in files!)
            {
                var code = StripLiteralsAndComments(f.Content);
                if (!Balanced(code, '{', '}')) fails.Add($"[{c.Name}] unbalanced braces in {f.Name}");
                if (!Balanced(code, '(', ')')) fails.Add($"[{c.Name}] unbalanced parens in {f.Name}");
                if (LeakedPlaceholder(code) is { } bad)
                    fails.Add($"[{c.Name}] leaked placeholder '{bad}' in {f.Name}");
                if (EmptyAggregate(code) is { } kw)
                    fails.Add($"[{c.Name}] emitted an empty '{kw}' body in {f.Name}\n{Excerpt(c.Source)}");
            }
        }
        fails.Assert("structurally invalid emissions");
    }

    /// <summary>
    /// The same structural check with a realm forced on. Units come from the environment's
    /// @preamble targets and a single-file case declares none, so Layout.Compose emits only
    /// shared.h; a kernel preamble puts the real emitted code under the same assertions.
    /// </summary>
    [Fact]
    public void EmittedCIsValidWithARealm()
    {
        const string realm = "@preamble(kernel) native { }\n";
        var fails = new Failures();
        foreach (var c in TortureCorpus.All)
        {
            var src = realm + c.Source;
            var (diag, module, crash) = TryCheck(src);
            if (crash != null) { fails.Add($"[{c.Name}+realm] front end threw -- {crash}\n{Excerpt(src)}"); continue; }
            if (diag!.HasErrors || module == null) continue;

            var (files, emitCrash) = TryEmit(module, diag);
            if (emitCrash != null) { fails.Add($"[{c.Name}+realm] emitter threw -- {emitCrash}\n{Excerpt(src)}"); continue; }

            foreach (var f in files!)
            {
                var code = StripLiteralsAndComments(f.Content);
                if (!Balanced(code, '{', '}')) fails.Add($"[{c.Name}+realm] unbalanced braces in {f.Name}");
                if (!Balanced(code, '(', ')')) fails.Add($"[{c.Name}+realm] unbalanced parens in {f.Name}");
                if (LeakedPlaceholder(code) is { } bad)
                    fails.Add($"[{c.Name}+realm] leaked placeholder '{bad}' in {f.Name}\n{Excerpt(src)}");
                if (EmptyAggregate(code) is { } kw)
                    fails.Add($"[{c.Name}+realm] empty '{kw}' body in {f.Name}\n{Excerpt(src)}");
            }
        }
        fails.Assert("structurally invalid emissions with a realm declared");
    }

    /// <summary>
    /// Deterministic token-soup fuzzer: random atoms from the real token set, producing unbalanced
    /// nesting, keywords in operand position and declarations truncated mid-header. Nothing is
    /// asserted beyond "did not throw".
    /// </summary>
    [Fact]
    public void RandomTokenSoupNeverCrashes()
    {
        string[] atoms =
        [
            "kernel", "user", "class", "module", "enum", "union", "func", "entry", "throws",
            "let", "if", "else", "while", "for", "in", "switch", "case", "default", "match",
            "try", "catch", "assign", "throw", "defer", "unsafe", "return", "break", "continue",
            "new", "null", "true", "sizeof", "as", "ref", "static", "public", "private",
            "operator", "process", "thread", "foreground", "background", "panic", "debug", "import",
            "int", "bool", "char", "void", "String", "Main", "x", "T",
            "{", "}", "(", ")", "[", "]", ";", ",", ".", ":", "=", "==", "->", "+", "-", "*", "/",
            "%", "&", "|", "^", "!", "~", "<", ">", "?", "++", "--", "&&", "||", "<<", ">>",
            "1", "0x10", "\"s\"", "'c'", "$\"{x}\"", "@keep", "@extern", "@environment",
        ];

        var rng = new Random(20260727); // fixed seed: a failure is always reproducible
        var fails = new Failures();
        var sb = new StringBuilder();
        for (int i = 0; i < 4000; i++)
        {
            sb.Clear();
            int n = 3 + rng.Next(40);
            for (int j = 0; j < n; j++) sb.Append(atoms[rng.Next(atoms.Length)]).Append(' ');
            var src = sb.ToString();

            var (diag, module, crash) = TryCheck(src);
            if (crash != null) { fails.Add($"[soup#{i}] {crash}\n{src}"); continue; }
            if (diag!.HasErrors || module == null) continue;

            var (_, emitCrash) = TryEmit(module, diag);
            if (emitCrash != null) fails.Add($"[soup#{i}] emitter -- {emitCrash}\n{src}");
        }
        fails.Assert("token-soup crashes");
    }

    /// <summary>
    /// A large program exercising most of the grammar, used by the mutation fuzzers below. Kept
    /// valid so that every mutation of it is exactly one step away from a well-formed program --
    /// which is where parser edge cases live.
    /// </summary>
    private const string Kitchen = """
    enum Color { Red, Green = 5 }
    union Shape { Circle(int r), Square(int w, int h), Empty }
    class Box[T] {
        T v;
        func _init() { }
        public T func Get() { return self.v; }
        public operator int func [](int k) { return k; }
    }
    module Util { public static int func Twice(int n) { return n * 2; } }
    throws int func Risky(int n) { if (n < 0) { throw; } return n; }
    kernel {
        foreground process P { thread T { entry func Run() { } } }
        entry func Main() {
            let int a = Util.Twice(3);
            let int b = Risky(a) catch { assign 0; };
            try { Risky(-1); } catch { debug "caught"; }
            let Box[int] box = new Box[int]();
            let Shape s = Shape.Circle(2);
            match (s) {
                case Circle(r) { debug "circle"; }
                case Square(w, h) { debug "square"; }
                default { }
            }
            switch (a) { case 1 { } default { } }
            for x in [1, 2, 3] { debug "x"; }
            unsafe { let int n = 1; let int* p = &n; let int d = *p; }
            defer debug "bye";
        }
    }
    """;

    /// <summary>
    /// Truncation fuzzer: every prefix of a large valid program must fail cleanly. Cutting a
    /// program mid-token is the cheapest way to reach parser states no complete input produces.
    /// </summary>
    [Fact]
    public void EveryPrefixFailsCleanly()
    {
        var fails = new Failures();
        for (int cut = 0; cut <= Kitchen.Length; cut++)
        {
            var src = Kitchen[..cut];
            var (diag, module, crash) = TryCheck(src);
            if (crash != null) { fails.Add($"[prefix {cut}] {crash}\n{Excerpt(src)}"); continue; }
            if (diag!.HasErrors || module == null) continue;

            var (_, emitCrash) = TryEmit(module, diag);
            if (emitCrash != null) fails.Add($"[prefix {cut}] emitter -- {emitCrash}");
        }
        fails.Assert("prefix truncations that did not fail cleanly");
    }

    /// <summary>
    /// Deletion fuzzer: blanking any single token out of a valid program must never crash. The
    /// cheapest generator of off-by-one-token parser states, reaching lookahead helpers that only
    /// misbehave when the shape they probe is one token short.
    /// </summary>
    [Fact]
    public void SingleTokenDeletionsNeverCrash()
    {
        var fails = new Failures();
        var toks = SingleFileCompile.Tokenize(Kitchen);
        for (int i = 0; i < toks.Count; i++)
        {
            if (toks[i].Kind == TK.EOF) continue;
            var span = toks[i].Span;
            if (span.Start + span.Length > Kitchen.Length) continue;
            var src = Kitchen[..span.Start] + new string(' ', span.Length) + Kitchen[(span.Start + span.Length)..];

            var (diag, module, crash) = TryCheck(src);
            if (crash != null) { fails.Add($"[delete #{i} '{toks[i].Value}'] {crash}"); continue; }
            if (diag!.HasErrors || module == null) continue;

            var (_, emitCrash) = TryEmit(module, diag);
            if (emitCrash != null) fails.Add($"[delete #{i} '{toks[i].Value}'] emitter -- {emitCrash}");
        }
        fails.Assert("token deletions that crashed");
    }

    #region Emitted-C helpers

    /// <summary>
    /// Blanks out string literals, char literals, and comments so delimiter counting isn't thrown
    /// off by a brace inside a message or a native block's comment.
    /// </summary>
    private static string StripLiteralsAndComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '"' or '\'')
            {
                char q = ch;
                i++;
                while (i < s.Length && s[i] != q)
                {
                    if (s[i] == '\\') i++;
                    i++;
                }
                sb.Append(' ');
                continue;
            }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i++;
                sb.Append(' ');
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The first unresolved-symbol placeholder the emitter leaked, or null.
    /// gata_MISSING_retain/_release are the one pair emitted on purpose, since a corpus case binds
    /// no ARC role. Any other means a lookup failed with no diagnostic.
    /// </summary>
    private static string? LeakedPlaceholder(string code)
    {
        foreach (var marker in (string[])["MISSING", "_UNRESOLVED"])
        {
            int i = 0;
            while ((i = code.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
            {
                int start = i;
                while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_')) start--;
                int end = i + marker.Length;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_')) end++;
                var name = code[start..end];
                if (name is not ("gata_MISSING_retain" or "gata_MISSING_release")) return name;
                i = end;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns true if open/close never go negative and end at zero.
    /// </summary>
    private static bool Balanced(string s, char open, char close)
    {
        int depth = 0;
        foreach (var ch in s)
        {
            if (ch == open) depth++;
            else if (ch == close && --depth < 0) return false;
        }
        return depth == 0;
    }

    /// <summary>
    /// Returns the keyword of the first "enum/struct/union [tag] { }" with nothing but whitespace
    /// between the braces, or null. An empty aggregate body is a constraint violation in C, and is
    /// what an empty Gata enum or union lowers to if nothing upstream rejects it.
    /// </summary>
    private static string? EmptyAggregate(string code)
    {
        foreach (var kw in (string[])["enum", "struct", "union"])
        {
            int i = 0;
            while ((i = code.IndexOf(kw, i, StringComparison.Ordinal)) >= 0)
            {
                // Only a whole word counts; "union" inside "reunion" does not.
                bool wordStart = i == 0 || (!char.IsLetterOrDigit(code[i - 1]) && code[i - 1] != '_');
                int j = i + kw.Length;
                if (wordStart)
                {
                    while (j < code.Length && (char.IsWhiteSpace(code[j]) || char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++;
                    if (j < code.Length && code[j] == '{')
                    {
                        int k = j + 1;
                        while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
                        if (k < code.Length && code[k] == '}') return kw;
                    }
                }
                i += kw.Length;
            }
        }
        return null;
    }

    #endregion
}
