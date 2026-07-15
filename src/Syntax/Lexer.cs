namespace Appa;

using System.Runtime.CompilerServices;

/// <summary>
/// Thrown by the lexer or parser when source text cannot be tokenized or parsed.
/// Caught at every call site so it never escapes as an unhandled exception.
/// </summary>
internal sealed class ParseException(TextSpan span, string message, string code = Codes.Syntax, string[]? hints = null) : Exception(message)
{
    public TextSpan Span { get; } = span;
    public string Code { get; } = code;
    public string[] Hints { get; } = hints ?? [];
}

/// <summary>
/// Converts a Gata source string into a flat list of tokens.
/// One instance per file. Call Tokenize() once and discard.
/// Man, .NET 10 is rad. I love records and primary constructors.
/// </summary>
internal sealed class Lexer(string src)
{
    // Position pointer into the source string. _pp points to the index of the next character to read.
    private int _pp;
    // Start index of the current token being read. _ts is set to _pp when a new token starts.
    private int _ts;

    // The list of tokens produced by the lexer. This is the output of Tokenize().
    private readonly List<Token> _tokens = [];

    // Keyword lookup table. Maps keyword strings to their corresponding token kinds.
    private static readonly Dictionary<string, TK> kw = new()
    {
        ["import"]      = TK.Import,
        ["kernel"]      = TK.Kernel,
        ["user"]        = TK.User,
        ["process"]     = TK.Process,
        ["thread"]      = TK.Thread,
        ["foreground"]  = TK.Foreground,
        ["background"]  = TK.Background,
        ["class"]       = TK.Class,
        ["enum"]        = TK.Enum,
        ["module"]      = TK.Module,
        ["func"]        = TK.Func,
        ["static"]      = TK.Static,
        ["public"]      = TK.Public,    
        ["private"]     = TK.Private,
        ["entry"]       = TK.Entry,
        ["throws"]      = TK.Throws,
        ["operator"]    = TK.Operator,
        ["as"]          = TK.As,
        ["fields"]      = TK.Fields,
        ["ref"]         = TK.Ref,
        ["return"]      = TK.Return,
        ["if"]          = TK.If,
        ["else"]        = TK.Else,
        ["while"]       = TK.While,
        ["for"]         = TK.For,       
        ["in"]          = TK.In,
        ["switch"]      = TK.Switch,    
        ["case"]        = TK.Case,
        ["break"]       = TK.Break,     
        ["continue"]    = TK.Continue,
        ["debug"]       = TK.Debug,     
        ["panic"]       = TK.Panic,
        ["try"]         = TK.Try,
        ["catch"]       = TK.Catch,     
        ["new"]         = TK.New,
        ["let"]         = TK.Let,       
        ["null"]        = TK.Null,
        ["unsafe"]      = TK.Unsafe,    
        ["throw"]       = TK.Throw,
        ["sizeof"]      = TK.Sizeof,    
        ["default"]     = TK.Default,
        ["defer"]       = TK.Defer,     
        ["match"]       = TK.Match,
        ["union"]       = TK.Union,
        ["bool"]        = TK.TBool,     
        ["int"]         = TK.TInt,
        ["char"]        = TK.TChar,     
        ["float"]       = TK.TFloat,
        ["double"]      = TK.TDouble,   
        ["short"]       = TK.TShort,
        ["void"]        = TK.TVoid,

        // Width explicit family

        ["int64"]       = TK.TPrim,
        ["uint"]        = TK.TPrim,
        ["uint64"]      = TK.TPrim,
        ["ushort"]      = TK.TPrim,
        ["byte"]        = TK.TPrim,
        ["sbyte"]       = TK.TPrim,
        ["usize"]       = TK.TPrim,
        ["uintptr"]     = TK.TPrim,
        ["true"]        = TK.BoolLit,
        ["false"]       = TK.BoolLit,
    };

    // Alternate lookup for kw using ReadOnlySpan<char> to avoid allocations during tokenization
    private static readonly Dictionary<string, TK>.AlternateLookup<ReadOnlySpan<char>> KeywordsLookup = 
        kw.GetAlternateLookup<ReadOnlySpan<char>>();

    // Array of keyword strings indexed by their corresponding TK enum values for quicker access
    private static readonly string[] kwstr;

    // Cache for character literal integer string values to avoid allocations
    private static readonly string[] chrstrs;

    // Cache for single-character punctuation token values to avoid allocations
    private static readonly string[] punctstrs;

    // Constructor to initialize the static keyword string array based on the kw dictionary
    static Lexer()
    {
        kwstr = new string[Enum.GetValues<TK>().Length];
        foreach (var kvp in kw)
        {
            kwstr[(int)kvp.Value] = kvp.Key;
        }

        chrstrs = new string[256];
        for (int i = 0; i < 256; i++) chrstrs[i] = i.ToString();

        punctstrs = new string[128];
        for (int i = 0; i < 128; i++) punctstrs[i] = ((char)i).ToString();
    }

    // Public method to tokenize the source string and return a list of tokens
    public List<Token> Tokenize()
    {
        // Main loop to read tokens until the end of the source string is reached
        while (_pp < src.Length) ReadOne();

        // Emit an EOF token at the end of the token list to signify the end of input
        _tokens.Add(new Token(TK.EOF, "", new TextSpan(src.Length, 0)));
        return _tokens;
    }

    // Helper methods for character inspection and token emission
    private char Cur => _pp < src.Length ? src[_pp] : '\0';
    private char Peek(int n = 1)
    {
        return (_pp + n) < src.Length ? src[_pp + n] : '\0';
    }

    private void Advance(int n = 1)
    {
        _pp += n;
    }

    // Emits a token of the specified kind with the given value, using the current token start and position pointers to create a TextSpan.
    private void Emit(TK kind, string value)
    {
        _tokens.Add(new Token(kind, value, new TextSpan(_ts, _pp - _ts)));
    }

    // Throws a ParseException with the given message and a TextSpan covering the current token being read.
    private void Fail(string m, string code = Codes.Syntax, string[]? hints = null)
    {
        throw new ParseException(new TextSpan(_ts, Math.Max(1, _pp - _ts)), m, code, hints);
    }

    /// <summary>
    /// Reads the next token from the source string and adds it to the token list.
    /// </summary>
    private void ReadOne()
    {
        // Whitespace
        if (IsWhiteSpace(Cur)) { Advance(); return; }

        // Line and block comments should be consumed silently
        if (Cur == '/' && Peek() == '/') 
        {
            while (_pp < src.Length && Cur != '\n')
                Advance(); 
            return;
        }

        // For multiline comments
        if (Cur == '/' && Peek() == '*')
        {
            // Consume block comment
            _ts = _pp;
            Advance(2);

            // Keep consuming until we find the closing '*/' or reach the end of the source string
            while (_pp < src.Length - 1 && !(Cur == '*' && Peek() == '/')) Advance();

            // A comment that never closes would otherwise silently swallow the rest of the file.
            if (!(Cur == '*' && Peek() == '/'))
                Fail("unterminated block comment; missing closing '*/'", Codes.UnterminatedLiteral);

            // Consume the closing '*/'
            Advance(2);
            return;
        }

        // About to read a new token, so set the token start pointer to the current position
        _ts = _pp;

        // Annotations like @intrinsic(role)  @preamble(target)  @extern  @environment  @keep
        if (Cur == '@')
        {
            Advance();
            int start = _pp;
            while (_pp < src.Length && IsIdentPart(Cur)) Advance();
            ReadOnlySpan<char> nn = src.AsSpan(start, _pp - start);

            switch (nn)
            {
                case "intrinsic": Emit(TK.AtIntrinsic, ReadParenArg("@intrinsic")); return;
                case "preamble": Emit(TK.AtPreamble, ReadParenArg("@preamble")); return;
                case "extern": Emit(TK.AtExtern, "@extern"); return;
                case "environment": Emit(TK.AtEnvironment, "@environment"); return;
                case "keep": Emit(TK.AtKeep, "@keep"); return;
                case "builtin": Emit(TK.AtBuiltin, ReadParenArg("@builtin")); return;
                default:
                    // If we reach here, it means the annotation name is not recognized.
                    // Throw a ParseException with a message indicating the unknown annotation and the expected ones.
                    Fail($"unknown annotation '@{nn}'; expected '@intrinsic', '@preamble', '@extern', '@environment', '@keep', or '@builtin'",
                        Codes.BadAnnotation);
                    return;
            }
        }

        // native { }  or  native type Name { }
        if (MatchKw("native"))
        {
            // Save the current position in case we need to backtrack
            int start = _pp; Advance(6); SkipWS();

            // If the next character is '{', we have a native block. Read the balanced content and emit a NativeContent token.
            if (Cur == '{') { Emit(TK.NativeContent, ReadBalanced()); return; }

            // If the next characters spell "type", we have a native type declaration. Read the type name and the balanced body, then emit a NativeTypeDecl token.
            if (MatchKw("type"))
            {
                Advance(4); SkipWS();

                // Read the type name, which must be a valid identifier. If we find a '{' after the type name, read the balanced body and emit a NativeTypeDecl token.
                int ns = _pp;
                while (_pp < src.Length && IsIdentPart(Cur)) Advance();
                string tname = src[ns.._pp]; SkipWS();
                if (Cur == '{' && !string.IsNullOrEmpty(tname))
                {
                    string body = ReadBalanced();
                    Emit(TK.NativeTypeDecl, tname + "\x1F" + body);
                    return;
                }
            }

            // If we didn't find a valid native block or type declaration, backtrack and read an identifier instead.
            _pp = start; 
            ReadID(); 
            
            return;
        }

        // fields { }
        if (MatchKw("fields"))
        {
            int start = _pp; Advance(6); SkipWS();
            if (Cur != '{') { _pp = start; ReadID(); return; }
            Emit(TK.Fields, ReadBalanced());
            return;
        }

        // identifiers
        if (IsIDStart(Cur)) { ReadID(); return; }

        // Check for interpolated strings and emit them as tokens
        if (Cur == '$' && Peek() == '"') { ReadInterp(); return; }

        // Check for single-character punctuation and emit it as a token
        if (Cur == '"')  { Emit(TK.StrLit, ReadString()); return; }

        // Check for character literals and emit them as tokens
        if (Cur == '\'') { ReadCharLit(); return; }

        // Check for numeric literals and emit them as tokens
        if (Cur >= '0' && Cur <= '9') { ReadNumber(); return; }

        // Compound assignment and multi character operators
        switch (Cur)
        {
            case '+':
                if (Peek() == '=') { Advance(2); Emit(TK.PlusEq, "+="); return; }
                if (Peek() == '+') { Advance(2); Emit(TK.Inc, "++"); return; }
                break;
            case '-':
                if (Peek() == '=') { Advance(2); Emit(TK.MinusEq, "-="); return; }
                if (Peek() == '>') { Advance(2); Emit(TK.Arrow, "->"); return; }
                if (Peek() == '-') { Advance(2); Emit(TK.Dec, "--"); return; }
                break;
            case '*':
                if (Peek() == '=') { Advance(2); Emit(TK.StarEq, "*="); return; }
                break;
            case '/':
                if (Peek() == '=') { Advance(2); Emit(TK.SlashEq, "/="); return; }
                break;
            case '%':
                if (Peek() == '=') { Advance(2); Emit(TK.PercentEq, "%="); return; }
                break;
            case '&':
                if (Peek() == '=') { Advance(2); Emit(TK.AmpEq, "&="); return; }
                if (Peek() == '&') { Advance(2); Emit(TK.And, "&&"); return; }
                break;
            case '|':
                if (Peek() == '=') { Advance(2); Emit(TK.PipeEq, "|="); return; }
                if (Peek() == '|') { Advance(2); Emit(TK.Or, "||"); return; }
                break;
            case '^':
                if (Peek() == '=') { Advance(2); Emit(TK.CaretEq, "^="); return; }
                break;
            case '=':
                if (Peek() == '=') { Advance(2); Emit(TK.EqEq, "=="); return; }
                Advance(); Emit(TK.Eq, "="); return;
            case '!':
                if (Peek() == '=') { Advance(2); Emit(TK.NotEq, "!="); return; }
                break;
            case '<':
                if (Peek() == '<')
                {
                    if (Peek(2) == '=') { Advance(3); Emit(TK.ShlEq, "<<="); return; }
                    Advance(2); Emit(TK.Shl, "<<"); return;
                }
                if (Peek() == '=') { Advance(2); Emit(TK.LtEq, "<="); return; }
                break;
            case '>':
                if (Peek() == '>')
                {
                    if (Peek(2) == '=') { Advance(3); Emit(TK.ShrEq, ">>="); return; }
                    Advance(2); Emit(TK.Shr, ">>"); return;
                }
                if (Peek() == '=') { Advance(2); Emit(TK.GtEq, ">="); return; }
                break;
        }

        // Single character punctuation fallthrough
        char c = Cur; Advance();
        Emit(c switch
        {
            '(' => TK.LParen,
            ')' => TK.RParen,
            '{' => TK.LBrace,
            '}' => TK.RBrace,
            '[' => TK.LBrack,
            ']' => TK.RBrack,
            ';' => TK.Semi,
            ',' => TK.Comma,
            ':' => TK.Colon,
            '.' => TK.Dot,
            _ => TK.Punct
        }, c < 128 ? punctstrs[c] : c.ToString());
    }

    /// <summary>
    /// True when the next characters in src spell exactly kw and are not followed
    /// by a letter, digit, or underscore (ie. it is a complete word boundary).
    /// </summary>
    private bool MatchKw(string kw)
    {
        // Check if the next characters in src match kw and are not followed by an identifier part
        if (_pp + kw.Length > src.Length) return false;

        // Use AsSpan to avoid creating a new string for comparison
        if (!src.AsSpan(_pp, kw.Length).Equals(kw, StringComparison.Ordinal)) return false;

        // Check if the character after kw is not an identifier part (letter, digit, or underscore)
        int after = _pp + kw.Length;
        return after >= src.Length || (!IsIdentPart(src[after]));
    }

    /// <summary>
    /// Consumes whitespace characters starting from the current position in the source string.
    /// </summary>
    private void SkipWS() { while (_pp < src.Length && IsWhiteSpace(Cur)) Advance(); }

    /// <summary>
    /// Reads the required (identifier) argument after an annotation keyword, like @intrinsic(retain).
    /// A missing, empty, or unclosed argument list is a lex-time error.
    /// </summary>
    private string ReadParenArg(string ann)
    {
        SkipWS();
        if (Cur != '(') Fail($"'{ann}' requires a parenthesized argument", Codes.BadAnnotation, [$"e.g. {ann}(name)"]);
        Advance();
        SkipWS();
        int s = _pp;

        // Read until we find a character that is not part of an identifier (letter, digit, or underscore)
        while (_pp < src.Length && IsIdentPart(Cur)) Advance();
        string arg = src[s.._pp];
        if (arg.Length == 0) Fail($"'{ann}' argument must be a name", Codes.BadAnnotation, [$"e.g. {ann}(name)"]);
        SkipWS();
        if (Cur != ')') Fail($"missing ')' after '{ann}({arg}'", Codes.BadAnnotation);
        Advance();
        return arg;
    }

    /// <summary>
    /// Reads a balanced block of text enclosed in braces '{' and '}'.
    /// Understands C style line comments, block comments, and string/char literals
    /// so a brace inside any of those does not alter the nesting depth.
    /// </summary>
    private string ReadBalanced()
    {
        Advance(); // opening {
        int start = _pp;
        int depth = 1;
        while (_pp < src.Length && depth > 0)
        {
            char cur = Cur;
            char peek = Peek();

            // Handle comments, string literals, and character literals to avoid misinterpreting braces inside them
            if (cur == '/' && peek == '/')
            {
                while (_pp < src.Length && Cur != '\n') Advance();
            }
            // Handle block comments
            else if (cur == '/' && peek == '*')
            {
                Advance(2);
                while (_pp < src.Length && !(Cur == '*' && Peek() == '/')) Advance();
                if (_pp < src.Length) Advance(2);
            }
            // Handle string literals
            else if (cur == '"' || cur == '\'')
            {
                char quote = cur; 
                Advance();
                while (_pp < src.Length && Cur != quote)
                {
                    if (Cur == '\\' && _pp + 1 < src.Length) Advance();
                    Advance();
                }
                if (_pp < src.Length) Advance();
            }
            // Handle nested braces
            else if (cur == '{') { depth++; Advance(); }
            else if (cur == '}') { depth--; Advance(); }
            else { Advance(); }
        }

        // If we reached the end of the source string and depth is still greater than 0, it means we have an unterminated native block. Throw a ParseException in that case.
        if (depth > 0) Fail("Unterminated native block, missing closing '}'", Codes.UnterminatedLiteral);
        return src[start..(_pp - 1)];
    }

    /// <summary>
    /// Reads an identifier or keyword from the source string starting at the current position.
    /// </summary>
    private void ReadID()
    {
        // Save the starting position of the identifier
        int start = _pp;
        while (_pp < src.Length && IsIdentPart(Cur)) Advance();

        // Use ReadOnlySpan<char> to avoid allocating a new string for the identifier
        ReadOnlySpan<char> span = src.AsSpan(start, _pp - start);

        // Check if the identifier matches a keyword in the KeywordsLookup dictionary.
        // If it does, emit the corresponding keyword token. Otherwise, emit an identifier token.
        if (KeywordsLookup.TryGetValue(span, out var kw))
        {
            // Several TK kinds have more than one valid spelling (true/false -> BoolLit;
            // the whole TPrim family). The cached canonical spelling only matches the actual
            // source text for single-spelling keywords; anywhere it differs, the
            // token's value must carry the real spelling, not the cached one, or
            // the two spellings collapse into whichever happened to be cached.
            string canonical = kwstr[(int)kw];
            Emit(kw, span.Equals(canonical, StringComparison.Ordinal) ? canonical : new string(span));
        }
        else
        {
            Emit(TK.Ident, new string(span));
        }
    }

    /// <summary>
    /// Reads a numeric literal: hex (0x…), integer, or float with optional suffix.
    /// The full lexeme including any suffix is stored verbatim as the token value.
    /// </summary>
    private void ReadNumber()
    {
        int start = _pp;

        // Hex literal
        if (Cur == '0' && (Peek() == 'x' || Peek() == 'X'))
        {
            Advance(2);
            int digits = _pp;
            while (_pp < src.Length && IsHexDigit(Cur)) Advance();
            if (_pp == digits) Fail("hex literal '0x' has no digits", Codes.BadNumber);
            ReadIntSuffix();
            if (IsIdentPart(Cur)) Fail($"invalid character '{Cur}' in hex literal", Codes.BadNumber);
            Emit(TK.IntLit, src[start.._pp]);
            return;
        }

        // Decimal integer or float literal
        while (_pp < src.Length && Cur >= '0' && Cur <= '9') Advance();

        bool isFloat = false;

        // Decimal point followed by a digit starts the fractional part
        if (Cur == '.' && Peek() >= '0' && Peek() <= '9')
        {
            isFloat = true;
            Advance();
            while (_pp < src.Length && Cur >= '0' && Cur <= '9') Advance();
        }

        // e/E, optional sign, then at least one digit
        if ((Cur == 'e' || Cur == 'E') &&
            (Peek() >= '0' && Peek() <= '9' || (Peek() == '+' || Peek() == '-') && Peek(2) >= '0' && Peek(2) <= '9'))
        {
            isFloat = true;
            Advance();
            if (Cur == '+' || Cur == '-') Advance();
            while (_pp < src.Length && Cur >= '0' && Cur <= '9') Advance();
        }

        if (isFloat)
        {
            if (Cur == 'f' || Cur == 'F') Advance(); // single-precision suffix
            if (IsIdentPart(Cur)) Fail($"invalid suffix on float literal (found '{Cur}' after '{src[start.._pp]}')", Codes.BadNumber);
            Emit(TK.FloatLit, src[start.._pp]);
        }
        else
        {
            ReadIntSuffix();
            if (IsIdentPart(Cur)) Fail($"invalid suffix on integer literal (found '{Cur}' after '{src[start.._pp]}')", Codes.BadNumber);
            Emit(TK.IntLit, src[start.._pp]);
        }
    }

    /// <summary>
    /// Consumes a trailing integer suffix: any run of u/U/l/L (e.g. ULL, u, L).
    /// </summary>
    private void ReadIntSuffix()
    {
        while (_pp < src.Length && (Cur is 'u' or 'U' or 'l' or 'L')) Advance();
    }

    /// <summary>
    /// Maps a single escape character to its value. Returns false for unrecognized escapes.
    /// </summary>
    private static bool TryEscape(char c, out char val)
    {
        val = c switch
        {
            'n'  => '\n',
            't'  => '\t',
            'r'  => '\r',
            '0'  => '\0',
            '\'' => '\'',
            '\\' => '\\',
            '"'  => '"',
            _    => '\0'
        };
        return c is 'n' or 't' or 'r' or '0' or '\'' or '\\' or '"';
    }

    /// <summary>
    /// Reads an interpolated string $"…{expr}…" as a sequence of distinct tokens.
    /// Emits InterpStrStart, StrLit, Punct for braces, standard expression tokens, and InterpStrEnd.
    /// </summary>
    private void ReadInterp()
    {
        Advance(2); // consume $"
        Emit(TK.InterpStrStart, "$\"");

        while (_pp < src.Length && Cur != '"' && Cur != '\n')
        {
            if (Cur == '{' && Peek() != '{')
            {
                // Emit opening '{'
                _ts = _pp; Advance();
                Emit(TK.Punct, "{");

                int brdepth = 1;
                while (_pp < src.Length && brdepth > 0)
                {
                    if (IsWhiteSpace(Cur)) { Advance(); continue; }

                    if (Cur == '{') brdepth++;
                    else if (Cur == '}')
                    {
                        brdepth--;
                        if (brdepth == 0) break; // don't let ReadOne consume the final '}'
                    }

                    // recursively lex standard tokens inside the expression
                    ReadOne();
                }

                // If we reached the end of the source string and brdepth is still greater than 0,
                // it means we have an unterminated '{' in the interpolated string. Throw a ParseException in that case.
                if (brdepth > 0) Fail("unterminated '{' in interpolated string", Codes.UnterminatedLiteral);

                // emit closing '}'
                _ts = _pp; Advance();
                Emit(TK.Punct, "}");
            }
            // emit a string literal segment until the next real '{', '"', or newline.
            // `{{`/`}}` are literal-brace escapes (C#-style) and collapse to a single
            // brace in the emitted text rather than starting a real interpolation.
            else
            {
                _ts = _pp;
                int start = _pp;
                var sb = new System.Text.StringBuilder();
                while (_pp < src.Length && Cur != '"' && Cur != '\n' && !(Cur == '{' && Peek() != '{'))
                {
                    if (Cur == '{' && Peek() == '{') { sb.Append(src, start, _pp - start); Advance(2); sb.Append('{'); start = _pp; }
                    else if (Cur == '}' && Peek() == '}') { sb.Append(src, start, _pp - start); Advance(2); sb.Append('}'); start = _pp; }
                    else if (Cur == '\\')
                    {
                        Advance();
                        if (_pp >= src.Length) break;
                        if (!TryEscape(Cur, out _)) Fail($"unrecognized escape '\\{Cur}' in interpolated string", Codes.BadEscape);
                        Advance();
                    }
                    else Advance();
                }

                // Quoted to match ReadString()'s convention - the resolver and emitter
                // both expect a StrLit token's value to be a ready-to-emit C string
                // literal, quotes included, not just the bare decoded content.
                string content = sb.Length == 0 ? src[start.._pp] : sb.Append(src, start, _pp - start).ToString();
                Emit(TK.StrLit, "\"" + content + "\"");
            }
        }

        if (Cur != '"') Fail("unterminated interpolated string", Codes.UnterminatedLiteral);

        // emit closing '"'
        _ts = _pp; Advance();
        Emit(TK.InterpStrEnd, "\"");
    }

    /// <summary>
    /// Reads a string literal from the source string starting at the current position.
    /// </summary>
    private string ReadString()
    {
        int start = _pp;
        Advance(); // opening "

        // Read until the closing quote or a newline is encountered, handling escape sequences
        while (_pp < src.Length && Cur != '"' && Cur != '\n')
        {
            if (Cur == '\\')
            {
                Advance();
                if (_pp >= src.Length) break;
                if (!TryEscape(Cur, out _)) Fail($"unrecognized escape '\\{Cur}' in string literal", Codes.BadEscape);
                Advance();
            }
            else Advance();
        }

        // If we reached the end of the source string or a newline without finding a closing quote, throw a ParseException for an unterminated string literal.
        if (Cur != '"') Fail("unterminated string literal", Codes.UnterminatedLiteral);
        Advance(); // closing "
        return src[start.._pp];
    }

    /// <summary>
    /// Reads a character literal from the source string starting at the current position.
    /// </summary>
    private void ReadCharLit()
    {
        Advance(); // opening '
        int val = 0;

        // Handle escape sequences and ensure the character literal contains exactly one character
        if (Cur == '\\')
        {
            Advance();
            if (!TryEscape(Cur, out char e)) Fail($"unrecognized escape '\\{Cur}' in char literal", Codes.BadEscape);
            val = e; Advance();
        }
        else if (Cur == '\'') Fail("empty char literal", Codes.UnterminatedLiteral);
        else if (Cur == '\n' || _pp >= src.Length) Fail("unterminated char literal", Codes.UnterminatedLiteral);
        else { val = Cur; Advance(); }

        // Ensure the character literal contains exactly one character
        if (Cur != '\'') Fail("char literal must hold exactly one character", Codes.UnterminatedLiteral);
        Advance(); // closing '
        
        string vstr = (val >= 0 && val < 256) ? chrstrs[val] : val.ToString();
        Emit(TK.CharLit, vstr);
    }

    // Helper methods for character classification

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWhiteSpace(char c)
    {
        return c == ' ' || (c >= '\t' && c <= '\r');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIDStart(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentPart(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
