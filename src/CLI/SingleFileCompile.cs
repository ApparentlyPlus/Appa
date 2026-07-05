namespace Appa;

// A disk-free path for a single Gata source string with no imports - tests want to
// go from a string literal straight to tokens, AST, diagnostics, or emitted C without
// touching the filesystem or requiring a real @environment/libgata dir.
/// <summary>
/// Compiles a single, import-free Gata source string in-process for tests.
/// </summary>
internal static class SingleFileCompile
{
    /// <summary>
    /// Tokenizes a source string.
    /// </summary>
    public static List<Token> Tokenize(string src) => new Lexer(src).Tokenize();

    /// <summary>
    /// Parses a source string into its AST. Throws ParseException on a syntax error.
    /// </summary>
    public static Appa.Program Parse(string src) => new Parser(Tokenize(src)).ParseProgram();

    /// <summary>
    /// Runs the full semantic pipeline over a single source string with no imports,
    /// skipping Pipeline.Transpile's disk-based import resolution entirely. A parse
    /// failure is folded into the returned diagnostic bag rather than thrown.
    /// </summary>
    public static (DiagnosticBag Diag, IrModule? Module) Check(string src, string path = "<test>")
    {
        var sources = new SourceSet();
        sources.Add(path, src);
        var diag = new DiagnosticBag(sources);

        Appa.Program? prog = null;
        try { prog = Parse(src); }
        catch (ParseException ex) { diag.Error(Codes.File, path, ex.Span, ex.Message); }
        if (prog == null) return (diag, null);

        var programs = new List<(string path, Appa.Program prog)> { (path, prog) };
        var visible = new Dictionary<string, HashSet<string>> { [path] = [path] };
        var (module, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);
        return (diag, module);
    }

    /// <summary>
    /// Runs Check and, if error-free, emits the final C output. Returns an empty
    /// list if the source failed to parse or check.
    /// </summary>
    public static IReadOnlyList<OutputFile> Emit(string src, string path = "<test>")
    {
        var (diag, module) = Check(src, path);
        if (diag.HasErrors || module == null) return [];
        return Layout.Compose(new Emitter(module, diag).Build());
    }
}
