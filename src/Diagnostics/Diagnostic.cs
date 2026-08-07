namespace Appa;
using System.Text;

/// <summary>
/// Severity of a diagnostic, either warning or error. Warnings do not prevent compilation, but
/// errors do.
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
/// This class contains all the diagnostic codes used in the compiler. Each code is a string that
/// starts with "G" followed by a three digit number.
/// </summary>
internal static class Codes
{
    public const string File                  = "G000";
    public const string TopologyOutsideRealm  = "G001";
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
    public const string MissingRealm           = "G056";
    public const string ShadowedFunction       = "G057";
    public const string MissingEntry           = "G058";
    public const string DuplicateEntry         = "G059";
    public const string MissingProcessMode     = "G060";
    public const string BadEntrySignature      = "G061";
    public const string DeferTransfer          = "G062";
    public const string ModuleField            = "G063";
    public const string MisplacedEnvironment   = "G064";
    public const string ConflictingModifiers   = "G065";
    public const string BadThrowsReturnType    = "G066";
    public const string LifecycleThrows        = "G067";
    public const string EntryOutsideKernel     = "G068";
    public const string AmbiguousCall          = "G069";
    public const string ShadowedVariable       = "G070";
    public const string SelfAssignment         = "G071";
    public const string NoEffect               = "G072";
    public const string ConstantCondition      = "G073";
    public const string RedundantCast          = "G074";
    public const string DivisionByZero         = "G075";
    public const string UnusedParameter        = "G076";
    public const string UnreachableCase        = "G077";
    public const string SelfComparison         = "G078";
    public const string BadShiftCount          = "G079";
    public const string MissingInterpolation   = "G080";
    public const string AssignOutsideCatch     = "G081";
    public const string CatchHandlerNoAssign   = "G082";
    public const string IdentityPayloadComparison  = "G083";
    public const string ImprecisePayloadComparison = "G084";
    public const string MissingRealmKeyword        = "G085";
    public const string UnknownRealm               = "G086";
    public const string ScopedNameNotVisible       = "G087";
    public const string UnmarkedShadow             = "G088";
    public const string ScopeNotEnclosing          = "G089";
    public const string UnknownInScope             = "G090";
    public const string ProcessWithoutThreads      = "G091";
    public const string PartialOperatorSet         = "G092";
    public const string UnsafeAllocatingTemporary  = "G093";
    public const string ManagedFixedArray          = "G094";
    public const string MixedSignedness            = "G095";
    public const string CharArithmetic             = "G096";
    public const string ExplicitTypeArgs           = "G097";
    public const string UseBeforeAssignment        = "G098";
    public const string DiscardedRetain            = "G099";
    public const string UninitialisedProcessVar    = "G100";
    public const string ReferenceCycle             = "G101";
}

internal static class Suggest
{
    /// <summary>
    /// Returns the candidate closest to typed by Levenshtein distance, or null if none is close
    /// enough to plausibly be a typo of it (distance more than half of typed's length).
    /// </summary>
    public static string? Closest(string typed, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDist = int.MaxValue;
        int maxAllowed = Math.Max(1, typed.Length / 2);
        foreach (var c in candidates)
        {
            // The distance is at least the length difference
            if (Math.Abs(c.Length - typed.Length) > maxAllowed) continue;
            int d = Distance(typed, c);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return bestDist <= maxAllowed ? best : null;
    }

    /// <summary>
    /// A one-element "did you mean 'X'?" hints array, or empty if nothing is close enough. Goes to
    /// diag.Error's hints parameter, not the message - it renders on its own "= help:" line rather
    /// than appended to the error text.
    /// </summary>
    public static string[] Hints(string typed, IEnumerable<string> candidates)
    {
        return Closest(typed, candidates) is { } best ? [$"did you mean '{best}'?"] : [];
    }

    /// <summary>
    /// Classic iterative Levenshtein edit distance between two strings. Identifiers are short, so
    /// the two work rows live on the stack. Absurdly long names fall back to heap.
    /// </summary>
    private static int Distance(string a, string b)
    {
        int w = b.Length + 1;
        Span<int> prev = w <= 128 ? stackalloc int[w] : new int[w];
        Span<int> cur = w <= 128 ? stackalloc int[w] : new int[w];
        for (int j = 0; j < w; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j < w; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            var tmp = prev; prev = cur; cur = tmp;
        }
        return prev[b.Length];
    }
}

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

    // The generic instantiation currently being resolved, or null outside one. Set by the
    // resolver as it walks a stamped instance's members.
    private string? _instanceScope;
    private readonly HashSet<(string Scope, string Code, string Message)> _instanceSeen = [];

    /// <summary>
    /// Drops every diagnostic added after the given count.
    /// </summary>
    public void TruncateTo(int count)
    {
        if (count >= _d.Count) return;
        for (int i = count; i < _d.Count; i++)
            if (_d[i].Severity == Severity.Error) _errCount--; else _warnCount--;
        _d.RemoveRange(count, _d.Count - count);
    }

    /// <summary>
    /// Marks diagnostics until disposal as coming from one generic instantiation, where the same
    /// complaint is reported once: a stamped instance is a copy, so one bad type argument is one
    /// mistake however many lines touch it. Errors only.
    /// </summary>
    public IDisposable InstanceScope(string? instance)
    {
        var previous = _instanceScope;
        _instanceScope = instance;
        return new ScopeReset(this, previous);
    }

    private sealed class ScopeReset(DiagnosticBag bag, string? previous) : IDisposable
    {
        public void Dispose() => bag._instanceScope = previous;
    }

    /// <summary>
    /// Adds an error diagnostic to the bag. Hints are optional "= help:" lines rendered after the
    /// source snippet.
    /// </summary>
    public void Error(string code, string file, TextSpan span, string message, string[]? hints = null)
    {
        if (_instanceScope is { } scope && !_instanceSeen.Add((scope, code, message))) return;
        _d.Add(new Diagnostic(Severity.Error, code, message, new Loc(file, span), hints ?? []));
        _errCount++;
    }

    /// <summary>
    /// Adds a warning diagnostic to the bag. Hints are optional "= help:" lines rendered after the
    /// source snippet.
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
    /// Renders a diagnostic as a string, with source code context and ANSI colors. If the source
    /// file is not available, it will render only the file name and message.
    /// </summary>
    public string Render(Diagnostic d)
    {
        // Grab the label and color for the diagnostic based on its severity
        var (label, color) = d.Severity == Severity.Error ? ("error", C.RED) : ("warning", C.YELLOW);
        
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
                .Append(C.NC)
                .Append(": ")
                .Append(d.Message);

            for (int i = 0; i < d.Hints.Length; i++)
                sb.AppendLine()
                    .Append("  ")
                    .Append(C.SAND)
                    .Append('=')
                    .Append(C.NC)
                    .Append(' ')
                    .Append(C.CYAN)
                    .Append("help")
                    .Append(C.NC)
                    .Append(": ")
                    .Append(d.Hints[i]);

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
            .Append(C.NC)
            .Append(": ")
            .AppendLine(d.Message);

        // Retrieve the source line as a Span
        ReadOnlySpan<char> tspn = src.LineSpan(line);

        // Slice the text span from the source line, clamping to the line length
        int gutterlen = GetDigitCount(line);

        // Draw empty gutter line
        sb.Append(' ', gutterlen)
            .Append(' ')
            .Append(C.SAND)
            .Append('|')
            .AppendLine(C.NC);

        // Draw source line with line number and gutter
        sb.Append(C.SAND)
            .Append(line)
            .Append(" |")
            .Append(C.NC)
            .Append(' ')
            .Append(tspn)
            .AppendLine();

        // Draw caret underline
        int caretLen = Math.Max(1, Math.Min(d.Loc.Span.Length, Math.Max(0, tspn.Length - (col - 1))));
        sb.Append(' ', gutterlen)
            .Append(' ')
            .Append(C.SAND)
            .Append('|')
            .Append(C.NC)
            .Append(' ');

        // Padding fix for tabs in the source line
        for (int i = 0; i < col - 1; i++)
            sb.Append(i < tspn.Length && tspn[i] == '\t' ? '\t' : ' ');
        sb.Append(color)
            .Append('^', caretLen)
            .Append(C.NC);

        // Render each hint as a rustc-style "= help: ..." line under a blank gutter row
        if (d.Hints.Length > 0)
        {
            sb.AppendLine()
                .Append(' ', gutterlen)
                .Append(' ')
                .Append(C.SAND)
                .Append('|')
                .Append(C.NC);
            for (int i = 0; i < d.Hints.Length; i++)
            {
                sb.AppendLine()
                    .Append(' ', gutterlen)
                    .Append(' ')
                    .Append(C.SAND)
                    .Append('=')
                    .Append(C.NC)
                    .Append(' ')
                    .Append(C.CYAN)
                    .Append("help")
                    .Append(C.NC)
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