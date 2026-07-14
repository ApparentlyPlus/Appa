namespace Appa;
using System.Text;

/// <summary>
/// ANSI escape codes for terminal output.
/// </summary>
internal static class EscapeCodes
{
    public const string NC      = "\x1b[0m";
    public const string GREEN   = "\x1b[1;32m";
    public const string RED     = "\x1b[1;31m";
    public const string YELLOW  = "\x1b[1;33m";
    public const string BLUE    = "\x1b[1;34m";
    public const string CYAN    = "\x1b[1;36m";
    public const string BOLD    = "\x1b[1m";
    public const string DIM     = "\x1b[2m";
}

/// <summary>
/// Severity of a diagnostic, either warning or error. Warnings do not prevent compilation, but errors do.
/// </summary>
internal enum Severity { Warning, Error }

/// <summary>
/// Where a diagnostic points. A file and the TextSpan to underline.
/// </summary>
internal readonly record struct Loc(string File, TextSpan Span);

/// <summary>
/// A diagnostic is data. It consists of a stable code, a severity, a concise message, and a
/// location. The message states the problem outright. Hints are optional, separate lines of
/// suggested fixes, rendered after the source snippet a la rustc's "= help:" lines.
/// </summary>
internal sealed record Diagnostic(Severity Severity, string Code, string Message, Loc Loc, string[] Hints)
{
    public Diagnostic(Severity Severity, string Code, string Message, Loc Loc) : this(Severity, Code, Message, Loc, []) { }
}

/// <summary>
/// This class contains all the diagnostic codes used in the compiler. 
/// Each code is a string that starts with "G" followed by a three digit number.
/// </summary>
internal static class Codes
{
    public const string File                  = "G000";
    public const string DuplicateContext      = "G001";
    public const string MissingEntryPoint     = "G002";
    public const string DuplicateName         = "G003";
    public const string TypeMismatch          = "G004";
    public const string UndefinedVariable     = "G005";
    public const string UndefinedMethod       = "G006";
    public const string UndefinedType         = "G007";
    public const string WrongArgCount         = "G008";
    public const string ArgTypeMismatch       = "G009";
    public const string ReturnTypeMismatch    = "G010";
    public const string NewOnNonClass         = "G011";
    public const string IndexOnNonCollection  = "G012";
    public const string StaticOnInstance      = "G013";
    public const string InstanceOnStatic      = "G014";
    public const string AmbiguousOverload     = "G015";
    public const string NoMatchingOverload    = "G016";
    public const string UnknownIntrinsic      = "G017";
    public const string DuplicateIntrinsic    = "G018";
    public const string MissingIntrinsic      = "G019";
    public const string MissingFloorBind      = "G020";
    public const string ThrowsOutsideTry      = "G021";
    public const string BreakOutsideLoop      = "G022";
    public const string UnusedVariable        = "G023";
    public const string UnreachableCode       = "G024";
    public const string EmptyBlock            = "G025";
    public const string RedundantReturn       = "G026";
    public const string MissingReturn         = "G027";
    public const string InvalidCast           = "G028";
    public const string ConditionNotBool      = "G029";
    public const string CallToEntry           = "G030";
    public const string PanicOutsideKernel    = "G031";
    public const string NotIterable           = "G032";
    public const string UnsafeRequired        = "G033";
    public const string NotAnLvalue           = "G034";
    public const string PrivateMember         = "G035";
    public const string DiagInRelease         = "G036";
    public const string RefArgMismatch        = "G037";
    public const string NoIndexSetter         = "G038";
    public const string NonExhaustiveMatch    = "G039";
    public const string StaticOnFreeFunc      = "G040";
    public const string WrongAnnotationKind   = "G041";
    public const string UnknownPreambleTarget = "G042";
    public const string ThreadModeNotAllowed  = "G043";
    public const string Syntax                = "G044";
    public const string AssignInExpr          = "G045";
    public const string UnterminatedLiteral   = "G046";
    public const string BadEscape             = "G047";
    public const string BadAnnotation         = "G048";
    public const string BadNumber             = "G049";
    public const string MissingLet            = "G050";
    public const string InvalidNesting        = "G051";
    public const string TrailingComma         = "G052";
    public const string BadDeclHeader         = "G053";
    public const string CannotInfer           = "G054";
    public const string KernelBlockInHosted    = "G055";
    public const string MissingUserRealm       = "G056";
    public const string DuplicateUserRealm     = "G057";
    public const string MissingUserEntry       = "G058";
    public const string DuplicateUserEntry     = "G059";
    public const string MissingProcessMode     = "G060";
}

/// <summary>
/// "Did you mean ...?" suggestions for misspelled identifiers, by edit distance.
/// </summary>
internal static class Suggest
{
    /// <summary>
    /// Returns the candidate closest to typed by Levenshtein distance, or null if none is
    /// close enough to plausibly be a typo of it (distance more than half of typed's length).
    /// </summary>
    public static string? Closest(string typed, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (var c in candidates)
        {
            int d = Distance(typed, c);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        int maxAllowed = Math.Max(1, typed.Length / 2);
        return bestDist <= maxAllowed ? best : null;
    }

    /// <summary>
    /// Formats a "did you mean 'X'?" suffix for a diagnostic message, or "" if typed has no
    /// close-enough match among candidates.
    /// </summary>
    public static string Hint(string typed, IEnumerable<string> candidates)
    {
        return Closest(typed, candidates) is { } best ? $" — did you mean '{best}'?" : "";
    }

    /// <summary>
    /// Classic iterative (two-row) Levenshtein edit distance between two strings.
    /// </summary>
    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>
/// Collects diagnostics during compilation. It can render them in a human readable format,
/// with source code context and ANSI colors.
/// </summary>
internal sealed class DiagnosticBag(SourceSet sources)
{
    private readonly List<Diagnostic> _d = [];
    private int _errCount;
    private int _warnCount;

    // Sources are needed to render diagnostics with source code context
    public SourceSet Sources => sources;

    // All diagnostics, in the order they were added. This is a read only view of the internal list.
    public IReadOnlyList<Diagnostic> All => _d;

    // Public properties that wrap the internal counters.
    public bool HasErrors => _errCount > 0;
    public int ErrorCount => _errCount;
    public int WarningCount => _warnCount;

    /// <summary>
    /// Adds an error diagnostic to the bag. Hints are optional "= help:" lines rendered after
    /// the source snippet.
    /// </summary>
    public void Error(string code, string file, TextSpan span, string message, string[]? hints = null)
    {
        _d.Add(new Diagnostic(Severity.Error, code, message, new Loc(file, span), hints ?? []));
        _errCount++;
    }

    /// <summary>
    /// Adds a warning diagnostic to the bag. Hints are optional "= help:" lines rendered after
    /// the source snippet.
    /// </summary>
    public void Warn(string code, string file, TextSpan span, string message, string[]? hints = null)
    {
        _d.Add(new Diagnostic(Severity.Warning, code, message, new Loc(file, span), hints ?? []));
        _warnCount++;
    }

    /// <summary>
    /// Gets the line number of the specified diagnostic.
    /// </summary>
    public int LineOf(Diagnostic d)
    {
        return sources.Get(d.Loc.File) is { } s && !d.Loc.Span.IsNone ? s.LineCol(d.Loc.Span.Start).Line : 0;
    }

    /// <summary>
    /// Renders a diagnostic as a string, with source code context and ANSI colors. 
    /// If the source file is not available, it will render only the file name and message.
    /// </summary>
    public string Render(Diagnostic d)
    {
        // Grab the label and color for the diagnostic based on its severity
        var (label, color) = d.Severity == Severity.Error ? ("error", EscapeCodes.RED) : ("warning", EscapeCodes.YELLOW);
        
        // Find the source text for the diagnostic's file, if available
        var src = sources.Get(d.Loc.File);

        // Avoid string allocs by using Span based slicing
        ReadOnlySpan<char> fpspn = d.Loc.File.AsSpan();

        // Extract the file name from the full path, using the last slash or backslash as a separator
        int lastSlash = fpspn.LastIndexOfAny('/', '\\');
        ReadOnlySpan<char> nspn = lastSlash >= 0 ? fpspn[(lastSlash + 1)..] : fpspn;

        // Pre size StringBuilder to 256 to prevent internal buffer resizing allocations
        // This *might* come back to bite me if the source lines are long.
        var sb = new StringBuilder(256);

        // Precompute the name span for the diagnostic header, like "file.g:12:34: error[G001]: "
        if (src == null || d.Loc.Span.IsNone)
        {
            sb.Append(nspn)
                .Append(": ")
                .Append(color)
                .Append(label)
                .Append('[')
                .Append(d.Code)
                .Append(']')
                .Append(EscapeCodes.NC)
                .Append(": ")
                .Append(d.Message);
            return sb.ToString();
        }

        // Get the line and column of the diagnostic's span start
        var (line, col) = src.LineCol(d.Loc.Span.Start);

        // Render the diagnostic header, like "file.g:12:34: error[G001]: "
        sb.Append(nspn)
            .Append(':')
            .Append(line)
            .Append(':')
            .Append(col)
            .Append(": ")
            .Append(color)
            .Append(label)
            .Append('[')
            .Append(d.Code)
            .Append(']')
            .Append(EscapeCodes.NC)
            .Append(": ")
            .AppendLine(d.Message);

        // Retrieve the source line as a Span
        ReadOnlySpan<char> tspn = src.LineSpan(line);

        // Slice the text span from the source line, clamping to the line length
        int gutterlen = GetDigitCount(line);

        // Draw empty gutter line
        sb.Append(' ', gutterlen)
            .Append(' ')
            .Append(EscapeCodes.BLUE)
            .Append('|')
            .AppendLine(EscapeCodes.NC);

        // Draw source line with line number and gutter
        sb.Append(EscapeCodes.BLUE)
            .Append(line)
            .Append(" |")
            .Append(EscapeCodes.NC)
            .Append(' ')
            .Append(tspn)
            .AppendLine();

        // Draw caret underline
        int caretLen = Math.Max(1, Math.Min(d.Loc.Span.Length, Math.Max(0, tspn.Length - (col - 1))));
        sb.Append(' ', gutterlen)
            .Append(' ')
            .Append(EscapeCodes.BLUE)
            .Append('|')
            .Append(EscapeCodes.NC)
            .Append(' ')
            .Append(' ', col - 1)
            .Append(color)
            .Append('^', caretLen)
            .Append(EscapeCodes.NC);

        // Render each hint as a rustc-style "= help: ..." line under a blank gutter row.
        if (d.Hints.Length > 0)
        {
            sb.AppendLine()
                .Append(' ', gutterlen)
                .Append(' ')
                .Append(EscapeCodes.BLUE)
                .Append('|')
                .Append(EscapeCodes.NC);
            for (int i = 0; i < d.Hints.Length; i++)
            {
                sb.AppendLine()
                    .Append(' ', gutterlen)
                    .Append(' ')
                    .Append(EscapeCodes.BLUE)
                    .Append('=')
                    .Append(EscapeCodes.NC)
                    .Append(' ')
                    .Append(EscapeCodes.CYAN)
                    .Append("help")
                    .Append(EscapeCodes.NC)
                    .Append(": ")
                    .Append(d.Hints[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the number of digits in a positive integer.
    /// </summary>
    private static int GetDigitCount(int value)
    {
        if (value < 0) value = value == int.MinValue ? int.MaxValue : -value;
        if (value < 10) return 1;
        if (value < 100) return 2;
        if (value < 1000) return 3;
        if (value < 10000) return 4;
        if (value < 100000) return 5;
        if (value < 1000000) return 6;
        if (value < 10000000) return 7;
        if (value < 100000000) return 8;
        if (value < 1000000000) return 9;
        return 10;
    }
}