namespace Appa;

/// <summary>
/// Recursive-descent parser that converts a flat token stream into an untyped AST. One instance per
/// file. Call ParseProgram() once and discard.
/// </summary>
internal sealed class Parser(IReadOnlyList<Token> tokens)
{
    // Materialize to an array upfront so every indexed access is O(1) with no virtual dispatch.
    private readonly Token[] _tokens = tokens as Token[] ?? Enumerable.ToArray(tokens);

    // current position in the token array
    private int _pp;

    // end offset of the last consumed token, used by To()  
    private int _pe;

    // Recursion depth guard. Without it, (((((...))))) stack-overflows instead of failing cleanly.
    private int _depth;
    private const int MaxDepth = 200;

    /// <summary>
    /// Increments the recursion depth counter and throws if it exceeds MaxDepth. Always call
    /// ExitDepth in a finally block.
    /// </summary>
    private void EnterDepth() { if (++_depth > MaxDepth) Fail("nested too deeply"); }

    /// <summary>
    /// Decrements the recursion depth counter. Always call in a finally block paired with
    /// EnterDepth.
    /// </summary>
    private void ExitDepth()
    {
        _depth--;
    }

    // Generic instantiation sites collected during parsing, consumed by the Monomorphizer.
    private readonly List<GenericUse> _gu = [];

    // Set the moment any scope qualifier is written, so the ScopeBinder knows whether a file that
    // declares nothing scoped still needs its rewrite sweep.
    private bool _scopedRef;

    #region Core stream helpers

    /// <summary>
    /// Returns the token at the current position. Safe without a bounds check because Advance()
    /// clamps _pp to [0, Length-1].
    /// </summary>
    private Token Cur => _tokens[_pp];

    /// <summary>
    /// Returns the token n positions ahead of the current position, or the last token if the offset
    /// exceeds the stream length.
    /// </summary>
    private Token Peek(int n = 1)
    {
        return (_pp + n) < _tokens.Length ? _tokens[_pp + n] : _tokens[^1];
    }

    /// <summary>
    /// Consumes the current token, updates _pe for span construction, and advances _pp.
    /// </summary>
    private Token Advance()
    {
        var t = Cur;
        _pe = t.Span.End;
        if (_pp < _tokens.Length - 1) _pp++;
        return t;
    }

    /// <summary>
    /// Builds a TextSpan from a saved start offset to the end of the last consumed token.
    /// </summary>
    private TextSpan To(int start)
    {
        return new(start, Math.Max(0, _pe - start));
    }

    /// <summary>
    /// Consumes a token of the expected kind, or throws if the current token doesn't match.
    /// </summary>
    private Token Expect(TK k)
    {
        if (Cur.Kind != k) Fail($"expected {KindName(k)}, found {Found()}");
        return Advance();
    }

    /// <summary>
    /// Describes the current token for an error message: its quoted source text, or 'end of file'.
    /// </summary>
    private string Found()
    {
        return Cur.Kind == TK.EOF ? "end of file" : $"'{Cur.Value}'";
    }

    /// <summary>
    /// Maps a token kind to the human-readable form used in "expected X" messages.
    /// </summary>
    private static string KindName(TK k)
    {
        return k switch
        {
            TK.Ident => "an identifier",
            TK.IntLit => "an integer literal",
            TK.FloatLit => "a float literal",
            TK.StrLit => "a string literal",
            TK.InterpStrEnd => "the closing '\"' of the interpolated string",
            TK.LParen => "'('", TK.RParen => "')'",
            TK.LBrace => "'{'", TK.RBrace => "'}'",
            TK.LBrack => "'['", TK.RBrack => "']'",
            TK.Semi => "';'", TK.Comma => "','", TK.Colon => "':'", TK.ColonColon => "'::'",
            TK.Dot => "'.'", TK.Eq => "'='", TK.Arrow => "'->'",
            TK.EOF => "end of file",
            _ => $"'{k.ToString().ToLowerInvariant()}'"
        };
    }

    /// <summary>
    /// Returns true if the current token has the given kind.
    /// </summary>
    private bool At(TK k)
    {
        return Cur.Kind == k;
    }

    /// <summary>
    /// Returns true if the current token is an identifier spelled exactly as given. The test for a
    /// contextual keyword - a word that only means something in one grammatical position, and is
    /// an ordinary identifier everywhere else.
    /// </summary>
    private bool AtValue(string word)
    {
        return Cur.Kind == TK.Ident && Cur.Value == word;
    }

    /// <summary>
    /// Returns true if a process declaration starts here.
    /// </summary>
    private bool AtProcessStart()
    {
        if (At(TK.Foreground) || At(TK.Background)) return true;
        return AtValue("process") && Peek().Kind == TK.Ident
            && (Peek(2).Kind == TK.LBrace || Peek(2).Kind == TK.Colon);
    }

    /// <summary>
    /// Consumes the current token and returns true if it matches the given kind; otherwise returns
    /// false without consuming.
    /// </summary>
    private bool Try(TK k) { if (At(k)) { Advance(); return true; } return false; }

    /// <summary>
    /// Returns true if the current token is TK.Punct with the given value. Only for operator tokens
    /// kept as TK.Punct: + - * / % and | ^ less-than greater-than ! ~
    /// </summary>
    private bool AtP(string v)
    {
        return Cur.Kind == TK.Punct && Cur.Value == v;
    }

    /// <summary>
    /// Throws a ParseException with the given message at the current token's span.
    /// </summary>
    private void Fail(string m, string code = Codes.Syntax, string[]? hints = null)
    {
        throw new ParseException(Cur.Span, m, code, hints);
    }

    /// <summary>
    /// Throws a ParseException with the given message at an explicit span.
    /// </summary>
    private static void FailAt(TextSpan span, string m, string code = Codes.Syntax, string[]? hints = null)
    {
        throw new ParseException(span, m, code, hints);
    }

    /// <summary>
    /// Returns true if the token kind is '=' or any compound assignment operator.
    /// </summary>
    private static bool IsAssignTk(TK k)
    {
        return k is TK.Eq or TK.PlusEq or TK.MinusEq or TK.StarEq or TK.SlashEq or TK.PercentEq
            or TK.AmpEq or TK.PipeEq or TK.CaretEq or TK.ShlEq or TK.ShrEq;
    }

    /// <summary>
    /// After an expression has been parsed in a position where only an expression is legal, rejects
    /// a trailing assignment operator with a targeted message instead of letting the generic
    /// "expected ')'" error fire.
    /// </summary>
    private void NoAssignHere(string where, string hint)
    {
        if (IsAssignTk(Cur.Kind))
            Fail($"assignment is a statement in Gata, not an expression, and cannot appear in {where}",
                Codes.AssignInExpr, [hint]);
    }

    #endregion

    #region Annotations

    /// <summary>
    /// Parses zero or more leading annotations. Uses null-lazy allocation so the common path (no
    /// annotations) returns a static empty array without any heap allocation.
    /// </summary>
    private Annotation[] ParseAnnotations()
    {
        List<Annotation>? anns = null;
        while (true)
        {
            if (At(TK.AtIntrinsic)) { var t = Advance(); anns ??= []; anns.Add(new IntrinsicAnnotation(t.Value, t.Span)); }
            else if (At(TK.AtPreamble)) { var t = Advance(); anns ??= []; anns.Add(new PreambleAnnotation(t.Value, t.Span)); }
            else if (At(TK.AtKeep)) { var t = Advance(); anns ??= []; anns.Add(new KeepAnnotation(t.Span)); }
            else if (At(TK.AtBuiltin)) { var t = Advance(); anns ??= []; anns.Add(new BuiltinAnnotation(t.Value, t.Span)); }
            else if (At(TK.AtShadows)) { var t = Advance(); anns ??= []; anns.Add(new ShadowsAnnotation(t.Span)); }
            else break;
        }
        return anns?.ToArray() ?? [];
    }

    /// <summary>
    /// Verifies that no invalid annotations were attached to a declaration that cannot use them.
    /// @intrinsic and @preamble bind only to native blocks, native types and functions; @keep and
    /// @builtin are what a class or module may carry, and everything else rejects all of them.
    /// </summary>
    private static void RejectAnns(Annotation[] anns, string what, bool allowKeep = false,
                                   bool allowBuiltin = false, bool allowShadows = true)
    {
        foreach (var a in anns)
        {
            if (allowShadows && a is ShadowsAnnotation) continue;
            if (allowKeep && a is KeepAnnotation) continue;
            if (allowBuiltin && a is BuiltinAnnotation) continue;
            FailAt(AnnSpan(a), $"annotations have no effect on {what}", Codes.BadAnnotation);
        }
    }

    /// <summary>
    /// Returns the source span an annotation was written at.
    /// </summary>
    private static TextSpan AnnSpan(Annotation a)
    {
        return a switch
        {
            IntrinsicAnnotation i => i.Span,
            PreambleAnnotation p => p.Span,
            KeepAnnotation k => k.Span,
            BuiltinAnnotation b => b.Span,
            ShadowsAnnotation s => s.Span,
            _ => TextSpan.None
        };
    }

    #endregion

    #region Top-level declarations

    /// <summary>
    /// Entry point. Parses a complete source file and returns its AST root.
    /// </summary>
    public Program ParseProgram()
    {
        List<TopLevel> items = [];
        while (!At(TK.EOF)) items.Add(ParseTopLevel());
        return new Program([.. items]) { GenericUses = [.. _gu], HasScopedRefs = _scopedRef };
    }

    /// <summary>
    /// Parses a free function declaration. Handles optional modifiers, an optional return type
    /// using ParseOptionalReturnType, and an optional generic parameter list between the name and
    /// the opening paren.
    /// </summary>
    private FuncDecl ParseFreeFuncDecl(Annotation[] anns, int s)
    {
        var modSpan = Cur.Span;
        var mods = ParseMods();
        RejectPublicOnFreeFunc(mods, modSpan);
        bool isEntry = Try(TK.Entry);
        bool isThrow = Try(TK.Throws);
        if (!isEntry) isEntry = Try(TK.Entry);
        TypeSpec? ret = ParseOptionalReturnType();
        if (ret != null && At(TK.LBrace))
            Fail("expected 'func', found '{'", Codes.BadDeclHeader,
                 [$"did you forget 'process' before '{ret}'?",
                  $"e.g. 'foreground process {ret} {{ ... }}'"]);
        Expect(TK.Func);
        var name = Expect(TK.Ident).Value;
        var generics = ParseGenericParamList();
        Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
        if (At(TK.Arrow)) Fail($"'{name}': return type goes before 'func', not after the parameter list", Codes.BadDeclHeader);
        return new FuncDecl(mods, anns, ret, name, generics, parms, isEntry, isThrow, ParseMethodBody(), To(s));
    }

    /// <summary>
    /// Reports 'public' written on a free function, which changes nothing.
    /// </summary>
    private void RejectPublicOnFreeFunc(Modifiers mods, TextSpan span)
    {
        if ((mods & Modifiers.Public) == 0) return;
        FailAt(span, "'public' has no meaning on a free function", Codes.BadDeclHeader,
            ["a free function is already visible to every file that imports this one",
             "remove it, or write 'private' to scope the function to this file"]);
    }

    /// <summary>
    /// Parses an optional generic parameter list like [T, U]. Returns an empty array if there is no
    /// leading bracket. Used by class declarations, free function declarations, and class/module
    /// method declarations.
    /// </summary>
    private string[] ParseGenericParamList()
    {
        if (!At(TK.LBrack)) return [];
        Advance();
        List<string> gp = [ExpectBareGenericParam()];
        while (Try(TK.Comma)) gp.Add(ExpectBareGenericParam());
        Expect(TK.RBrack);
        return [.. gp];
    }

    /// <summary>
    /// Dispatches to the correct top-level parser based on the current token.
    /// </summary>
    private TopLevel ParseTopLevel()
    {
        if (At(TK.Import)) return ParseImport();
        if (At(TK.AtEnvironment)) { int es = Cur.Span.Start; Advance(); return new EnvironmentDecl(To(es)); }
        int s = Cur.Span.Start;
        var anns = ParseAnnotations();
        if (At(TK.Import)) RejectAnns(anns, "an import", allowShadows: false);
        if (At(TK.NativeContent)) return new NativeBlock(ParseNativeBody(Advance()), To(s), anns);
        if (At(TK.NativeTypeDecl)) return ParseNativeType(anns, s);

        // The order of these checks matters. Class and module keywords are valid type names,
        // so they must be checked after the native decls but before the free function decl.
        if (At(TK.Enum)) { RejectAnns(anns, "an enum"); return ParseEnumDecl(anns, s); }
        if (At(TK.Union)) { RejectAnns(anns, "a union"); return ParseUnionDecl(anns, s); }
        if (At(TK.Class)) { RejectAnns(anns, "a class", allowKeep: true, allowBuiltin: true); return ParseClassDecl(anns, s); }
        if (At(TK.Module)) { RejectAnns(anns, "a module", allowKeep: true); return ParseModuleDecl(anns, s); }
        if (At(TK.Realm)) { RejectAnns(anns, "a realm", allowShadows: false); return ParseRealmDecl(); }
        if (At(TK.Kernel)) RequireRealmKeyword();
        if (AtProcessStart())
            Fail("a 'process' must be declared inside a 'realm' block", Codes.TopologyOutsideRealm,
                 ["wrap it in 'realm kernel { ... }' or 'realm userspace { ... }'"]);
        RejectStrayThread();
        RejectModifierOnType();
        if (At(TK.AtExtern)) return ParseExternDecl(anns, s);
        return ParseFreeFuncDecl(anns, s);
    }

    /// <summary>
    /// Reports a visibility or 'static' modifier written on a top-level type declaration. Only a
    /// free function takes one there; without this the modifier is read as the start of a function
    /// and the error lands on the 'class' keyword, naming the wrong thing entirely.
    /// </summary>
    private void RejectModifierOnType()
    {
        if (Cur.Kind is not (TK.Public or TK.Private or TK.Static)) return;
        string what = Peek().Kind switch
        {
            TK.Class => "a class",
            TK.Module => "a module",
            TK.Enum => "an enum",
            TK.Union => "a union",
            TK.NativeTypeDecl => "a native type",
            _ => "",
        };
        if (what.Length == 0) return;

        string mod = Cur.Value;
        FailAt(Cur.Span, $"'{mod}' has no meaning on {what}", Codes.BadDeclHeader,
            [mod == "private"
                ? "a top-level type is visible to every file that imports this one; there is no file-local type"
                : $"remove '{mod}'; only a free function takes 'private' here"]);
    }

    /// <summary>
    /// Parses an import declaration. A string literal import is a filesystem path; a bare
    /// identifier is a module name.
    /// </summary>
    private ImportDecl ParseImport()
    {
        int s = Cur.Span.Start;
        Expect(TK.Import);
        if (At(TK.StrLit))
        {
            string raw = Advance().Value.Trim('"');
            Expect(TK.Semi);
            return new ImportDecl(raw, true, To(s));
        }
        var name = Expect(TK.Ident).Value;
        Expect(TK.Semi);
        return new ImportDecl(name, false, To(s));
    }

    /// <summary>
    /// Wraps a raw native block token into a NativeBody.
    /// </summary>
    private static NativeBody ParseNativeBody(Token tok)
    {
        return new NativeBody(tok.Value);
    }

    /// <summary>
    /// Parses a native type declaration. The lexer encodes the type name and body separated by \x1F
    /// in a single NativeTypeDecl token value.
    /// </summary>
    private NativeTypeDecl ParseNativeType(Annotation[] anns, int s)
    {
        string raw = Advance().Value;
        int sep = raw.IndexOf('\x1F');
        return new NativeTypeDecl(raw[..sep], raw[(sep + 1)..], To(s), anns);
    }

    /// <summary>
    /// Parses an @extern function pre-declaration. Tells the compiler a C function exists so it can
    /// be called from Gata without a Gata body.
    /// </summary>
    private ExternFuncDecl ParseExternDecl(Annotation[] anns, int s)
    {
        Advance(); // @extern
        TypeSpec? ret = ParseOptionalReturnType();
        Expect(TK.Func);
        var name = Expect(TK.Ident).Value;
        Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
        if (At(TK.Arrow)) Fail($"'{name}': return type goes before 'func', not after the parameter list", Codes.BadDeclHeader);
        Expect(TK.Semi);
        return new ExternFuncDecl(ret, name, parms, To(s), anns);
    }

    /// <summary>
    /// Parses a 'realm kernel { … }' or 'realm userspace { … }' block. There are exactly two
    /// realms; 'kernel' is a keyword, 'userspace' is matched by value since nothing else may
    /// follow 'realm'.
    /// </summary>
    private ContextDecl ParseRealmDecl()
    {
        int s = Cur.Span.Start;
        Advance(); // 'realm'
        Realm kind = Realm.None;
        if (At(TK.Kernel)) { kind = Realm.Kernel; Advance(); }
        else if (At(TK.Userspace)) { kind = Realm.User; Advance(); }
        else
            Fail($"unknown realm {Found()}; the only realms are 'kernel' and 'userspace'",
                 Codes.UnknownRealm,
                 Cur.Kind == TK.Ident ? Suggest.Hints(Cur.Value, ["kernel", "userspace"]) : []);
        Expect(TK.LBrace);
        List<TopLevel> items = [];
        while (!At(TK.RBrace) && !At(TK.EOF)) items.Add(ParseContextItem());
        Expect(TK.RBrace);
        return new ContextDecl(kind, [.. items], To(s));
    }

    /// <summary>
    /// Reports a bare 'kernel' that is missing its 'realm' prefix. Kept as a dedicated diagnostic
    /// so the pre-'realm' spelling produces advice rather than a generic syntax error.
    /// </summary>
    private void RequireRealmKeyword()
    {
        Fail("expected 'realm' before 'kernel'", Codes.MissingRealmKeyword,
             ["write 'realm kernel { ... }'"]);
    }

    /// <summary>
    /// Dispatches to the correct parser for a single item inside a realm block. Realm blocks
    /// cannot be nested, so a nested 'realm' is a hard error here.
    /// </summary>
    private TopLevel ParseContextItem()
    {
        if (At(TK.Realm)) Fail("a 'realm' block cannot be nested inside another", Codes.InvalidNesting);
        if (At(TK.Kernel)) RequireRealmKeyword();
        RejectStrayImport();
        RejectStrayThread();
        int s = Cur.Span.Start;
        if (At(TK.AtEnvironment)) { Advance(); return new EnvironmentDecl(To(s)); }
        var anns = ParseAnnotations();
        RejectStrayImport();
        RejectStrayThread();
        if (At(TK.NativeContent)) return new NativeBlock(ParseNativeBody(Advance()), To(s), anns);
        if (At(TK.NativeTypeDecl)) return ParseNativeType(anns, s);
        if (At(TK.AtExtern)) return ParseExternDecl(anns, s);
        if (At(TK.Enum)) { RejectAnns(anns, "an enum"); return ParseEnumDecl(anns, s); }
        if (At(TK.Union)) { RejectAnns(anns, "a union"); return ParseUnionDecl(anns, s); }
        if (At(TK.Class)) { RejectAnns(anns, "a class", allowKeep: true, allowBuiltin: true); return ParseClassDecl(anns, s); }
        if (At(TK.Module)) { RejectAnns(anns, "a module", allowKeep: true); return ParseModuleDecl(anns, s); }
        if (AtProcessStart())
            { RejectAnns(anns, "a process", allowShadows: false); return ParseProcessDeclTop(); }
        return ParseFreeFuncDecl(anns, s);
    }

    #endregion

    #region Class and module

    /// <summary>
    /// Parses a class declaration. The name is mangled with the generic parameter list so the
    /// Monomorphizer can match self-references: "class List[T]" becomes "List_T" in the AST, with
    /// BaseName holding the "List" the user wrote.
    /// </summary>
    private ClassDecl ParseClassDecl(Annotation[] anns, int s)
    {
        Expect(TK.Class);
        int ns = Cur.Span.Start;
        var name = ParseSimpleTypeName();
        string baseName = name;
        List<string> generics = [];
        if (At(TK.LBrack))
        {
            Advance();
            generics.Add(ExpectBareGenericParam());
            while (Try(TK.Comma)) generics.Add(ExpectBareGenericParam());
            Expect(TK.RBrack);
            var genericsArray = generics.ToArray();
            _gu.Add(new GenericUse(name, genericsArray, To(ns)));
            name = Mangler.GenericInstance(name, genericsArray);
        }
        Expect(TK.LBrace);
        List<ClassMember> members = [];
        while (!At(TK.RBrace) && !At(TK.EOF)) members.Add(ParseClassMember());
        Expect(TK.RBrace);
        return new ClassDecl(name, [.. generics], anns, [.. members], To(s)) { BaseName = baseName };
    }

    /// <summary>
    /// Reads a single bare identifier as a generic parameter name. Type arguments at use sites may
    /// nest (List[Map[K,V]]); class parameter declarations may not (class Foo[Bar[Baz]] is
    /// rejected).
    /// </summary>
    private string ExpectBareGenericParam()
    {
        if (!At(TK.Ident)) Fail($"generic parameter must be a plain name, found {Found()}", Codes.BadDeclHeader);
        var tok = Advance().Value;
        if (At(TK.LBrack)) Fail($"generic parameter '{tok}' cannot itself be generic", Codes.BadDeclHeader);
        return tok;
    }

    /// <summary>
    /// Parses a module declaration. Modules are classes where all members are implicitly static.
    /// </summary>
    private ClassDecl ParseModuleDecl(Annotation[] anns, int s)
    {
        Expect(TK.Module);
        var name = ParseSimpleTypeName();
        Expect(TK.LBrace);
        List<ClassMember> members = [];
        while (!At(TK.RBrace) && !At(TK.EOF)) members.Add(ParseClassMember());
        Expect(TK.RBrace);
        return new ClassDecl(name, [], anns, [.. members], To(s), IsModule: true);
    }

    #endregion

    #region Enum and union

    /// <summary>
    /// Parses an enum declaration. Members may carry explicit integer values; if absent the C
    /// compiler applies the usual increment rule. A trailing comma after the last member is a hard
    /// error.
    /// </summary>
    private EnumDecl ParseEnumDecl(Annotation[] anns, int s)
    {
        Expect(TK.Enum);
        var name = Expect(TK.Ident).Value;
        Expect(TK.LBrace);
        List<EnumMember>? members = null;
        if (!At(TK.RBrace) && !At(TK.EOF))
        {
            members = [];
            int ms = Cur.Span.Start;
            members.Add(new EnumMember(Expect(TK.Ident).Value, Try(TK.Eq) ? ParseExpr() : null, To(ms)));
            while (Try(TK.Comma))
            {
                if (At(TK.RBrace)) Fail("trailing comma not allowed after the last enum member; remove it", Codes.TrailingComma);
                ms = Cur.Span.Start;
                members.Add(new EnumMember(Expect(TK.Ident).Value, Try(TK.Eq) ? ParseExpr() : null, To(ms)));
            }
        }
        Expect(TK.RBrace);
        return new EnumDecl(name, members?.ToArray() ?? [], To(s), anns);
    }

    /// <summary>
    /// Parses a union declaration. Each variant is a name followed by an optional parenthesised
    /// field list. A variant with no parens carries no payload. A trailing comma after the last
    /// variant is a hard error.
    /// </summary>
    private UnionDecl ParseUnionDecl(Annotation[] anns, int s)
    {
        Expect(TK.Union);
        int ns = Cur.Span.Start;
        var name = Expect(TK.Ident).Value;
        string baseName = name;

        // Type parameters, registered and mangled exactly as ParseClassDecl does, so the
        // Monomorphizer discovers the template through the same GenericUse channel.
        List<string> generics = [];
        if (At(TK.LBrack))
        {
            Advance();
            generics.Add(ExpectBareGenericParam());
            while (Try(TK.Comma)) generics.Add(ExpectBareGenericParam());
            Expect(TK.RBrack);

            var genericsArray = generics.ToArray();
            _gu.Add(new GenericUse(name, genericsArray, To(ns)));
            name = Mangler.GenericInstance(name, genericsArray);
        }

        Expect(TK.LBrace);
        List<UnionVariant>? variants = null;
        if (!At(TK.RBrace) && !At(TK.EOF))
        {
            variants = [];
            int vs = Cur.Span.Start;
            var vname = Expect(TK.Ident).Value;
            Param[] fields = At(TK.LParen) ? ParseUnionFieldList() : [];
            variants.Add(new UnionVariant(vname, fields, To(vs)));
            while (Try(TK.Comma))
            {
                if (At(TK.RBrace)) Fail("trailing comma not allowed after the last union variant; remove it", Codes.TrailingComma);
                vs = Cur.Span.Start;
                vname = Expect(TK.Ident).Value;
                fields = At(TK.LParen) ? ParseUnionFieldList() : [];
                variants.Add(new UnionVariant(vname, fields, To(vs)));
            }
        }
        Expect(TK.RBrace);
        return new UnionDecl(name, [.. generics], variants?.ToArray() ?? [], To(s), anns) { BaseName = baseName };
    }

    /// <summary>
    /// Parses a union variant's parenthesised field list. A trailing comma right before the closing
    /// paren is a hard error with a specific message, since the shared ParseParamList used for
    /// function parameters does not check for one.
    /// </summary>
    private Param[] ParseUnionFieldList()
    {
        Advance(); // opening (
        if (At(TK.RParen)) { Advance(); return []; }
        List<Param> fields = [ParseParam()];
        while (Try(TK.Comma))
        {
            if (At(TK.RParen)) Fail("trailing comma not allowed after the last field; remove it", Codes.TrailingComma);
            fields.Add(ParseParam());
        }
        Expect(TK.RParen);
        return [.. fields];
    }

    #endregion

    #region Type specs

    /// <summary>
    /// Parses a named type, keeping any generic arguments structurally on the NamedSpec. Generic
    /// uses are registered in _gu for the Monomorphizer to consume.
    /// </summary>
    private NamedSpec ParseTypeName()
    {
        EnterDepth();
        var name = ParseTypeNameInner();
        ExitDepth();
        return name;
    }

    private NamedSpec ParseTypeNameInner()
    {
        int s = Cur.Span.Start;
        string[]? scope = ParseScopeQualifier();
        if (scope != null)
        {
            var path = new List<string>();
            do { path.Add(ExpectIdent("a scope or type name")); } while (Try(TK.Dot));
            scope = [.. scope, .. path[..^1]];
            return FinishTypeName(path[^1], scope, s);
        }
        return FinishTypeName(ParseSimpleTypeName(), null, s);
    }

    /// <summary>
    /// Completes a type name once its base and any explicit scope are known: the optional argument
    /// list, and the instantiation request that goes with it.
    /// </summary>
    private NamedSpec FinishTypeName(string name, string[]? scope, int s)
    {
        if (!At(TK.LBrack)) return new NamedSpec(name, To(s)) { Scope = scope };
        Advance();
        List<NamedSpec> args = [ParseTypeName()];
        while (Try(TK.Comma)) args.Add(ParseTypeName());
        if (!At(TK.RBrack)) Fail($"invalid type argument in '{name}[...]', found {Found()}");
        Expect(TK.RBrack);
        var spec = new NamedSpec(name, [.. args], To(s)) { Scope = scope };
        var mangledArgs = new string[args.Count];
        for (int i = 0; i < args.Count; i++) mangledArgs[i] = args[i].Mangled;
        _gu.Add(new GenericUse(name, mangledArgs, To(s), [.. args]) { Scope = scope });
        return spec;
    }

    /// <summary>
    /// Consumes a leading scope qualifier and returns its segments, or null when there is none.
    /// '::' is the root scope and so has no segments at all.
    /// </summary>
    private string[]? ParseScopeQualifier()
    {
        if (Try(TK.ColonColon)) { _scopedRef = true; return []; }
        if (!At(TK.Kernel) && !At(TK.Userspace)) return null;
        if (Peek().Kind != TK.Dot) return null;
        string realm = Advance().Value;
        Advance();
        _scopedRef = true;
        return [realm];
    }

    /// <summary>
    /// Consumes an identifier, reporting what was wanted rather than the generic token mismatch.
    /// </summary>
    private string ExpectIdent(string what)
    {
        if (At(TK.Ident)) return Advance().Value;
        Fail($"expected {what}, found {Found()}");
        return "";
    }

    /// <summary>
    /// Parses the base name of a type, like an identifier (Process/Thread are ordinary identifiers,
    /// resolved as builtin types later) or a primitive keyword.
    /// </summary>
    private string ParseSimpleTypeName()
    {
        if (At(TK.Ident)) return Advance().Value;
        if (IsPrim(Cur.Kind)) return PrimName(Advance());
        if (At(TK.Let))
            Fail("a variable cannot be declared here", Codes.Syntax,
                 ["a variable belongs inside a function, or directly inside a process, "
                  + "where it becomes state its threads share",
                  "types, modules and functions are what a realm or a file can hold"]);
        Fail($"expected a type name, found {Found()}");
        return "";
    }

    /// <summary>
    /// Parses a full type specifier. Fixed-array prefix [N], function pointer type, plain type
    /// name, and optional pointer suffixes.
    /// </summary>
    private TypeSpec ParseTypeSpec()
    {
        EnterDepth();
        var spec = ParseTypeSpecInner();
        ExitDepth();
        return spec;
    }

    private TypeSpec ParseTypeSpecInner()
    {
        int s = Cur.Span.Start;

        // [N]elem, brackets come before the element type.
        if (At(TK.LBrack) && Peek().Kind == TK.IntLit && Peek(2).Kind == TK.RBrack)
        {
            Advance();
            string n = Advance().Value;
            Expect(TK.RBrack);
            return new ArraySpec(n, ParseTypeSpec(), To(s));
        }
        if (At(TK.Func)) return ParseFuncTypeSpec();
        TypeSpec spec = ParseTypeName();
        while (AtP("*")) { Advance(); spec = new PtrSpec(spec, To(s)); }
        return spec;
    }

    /// <summary>
    /// Parses a function pointer type specifier into a FuncSpec node.
    /// </summary>
    private FuncSpec ParseFuncTypeSpec()
    {
        int s = Cur.Span.Start;
        Expect(TK.Func);
        Expect(TK.LParen);
        List<TypeSpec> ps = [];
        if (!At(TK.RParen))
        {
            ps.Add(ParseTypeSpec());
            while (Try(TK.Comma)) ps.Add(ParseTypeSpec());
        }
        Expect(TK.RParen);
        Expect(TK.Arrow);
        var spec = new FuncSpec([.. ps], ParseTypeSpec(), To(s));
        if (AtP("*")) Fail("pointer to a function type is not supported; use the function type directly", Codes.BadDeclHeader);
        return spec;
    }

    /// <summary>
    /// Returns true if the token kind is one of the primitive type keywords.
    /// </summary>
    private static bool IsPrim(TK k)
    {
        return k is TK.TBool or TK.TInt or TK.TChar or TK.TFloat
        or TK.TDouble or TK.TShort or TK.TVoid or TK.TPrim;
    }

    /// <summary>
    /// Maps a primitive token to its canonical type name string. TPrim tokens carry their own value
    /// (eg. "uint64"), so those fall through to the default.
    /// </summary>
    private static string PrimName(Token t)
    {
        return t.Kind switch
        {
            TK.TBool => "bool",
            TK.TInt => "int",
            TK.TChar => "char",
            TK.TFloat => "float",
            TK.TDouble => "double",
            TK.TShort => "short",
            TK.TVoid => "void",
            _ => t.Value
        };
    }

    #endregion

    #region Class members

    /// <summary>
    /// Parses a single class member: a fields block, operator overload, method, or field
    /// declaration.
    /// </summary>
    private ClassMember ParseClassMember()
    {
        int s = Cur.Span.Start;
        if (At(TK.Class) || At(TK.Module)) Fail("classes and modules cannot be nested", Codes.InvalidNesting);
        if (At(TK.Realm)) Fail("a 'realm' block cannot appear inside a class", Codes.InvalidNesting);

        // fields { } block is a raw C struct fields injected verbatim into the emitted typedef.
        if (At(TK.Fields)) return new FieldsBlock(ParseNativeBody(Advance()), To(s));

        var anns = ParseAnnotations();
        var mods = ParseMods();
        bool isEntry = Try(TK.Entry);
        bool isThrow = Try(TK.Throws);
        if (!isEntry) isEntry = Try(TK.Entry);

        if (At(TK.Operator))
        {
            if (anns.Length > 0) Fail("annotations have no effect on an operator", Codes.BadAnnotation);
            if (isEntry) Fail("'entry' has no meaning on an operator", Codes.BadDeclHeader);
            if (isThrow) Fail("'throws' has no meaning on an operator", Codes.BadDeclHeader);
            if ((mods & Modifiers.Static) != 0) Fail("'static' has no meaning on an operator", Codes.BadDeclHeader);
            Advance();
            TypeSpec? ret = At(TK.Func) && Peek().Kind != TK.LParen ? null : ParseTypeSpec();
            Expect(TK.Func);
            string op = ParseOperatorSymbol();
            Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
            if (At(TK.Arrow)) Fail($"'{op}': return type goes after 'operator', not after the parameter list", Codes.BadDeclHeader);
            return new OperatorDecl(mods, op, parms, ret, ParseMethodBody(), To(s));
        }

        // If we reach here, it must be either a method or a field. Fields don't support
        // entry, throws, or annotations.
        if (LooksLikeMethod())
        {
            if (isEntry) Fail("'entry' has no meaning on a class method", Codes.BadDeclHeader);
            TypeSpec? ret = ParseOptionalReturnType();
            Expect(TK.Func);
            var name = Expect(TK.Ident).Value;
            var generics = ParseGenericParamList();
            Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
            if (At(TK.Arrow)) Fail($"'{name}': return type goes before 'func', not after the parameter list", Codes.BadDeclHeader);
            return new MethodDecl(mods, anns, ret, name, generics, parms, isEntry, isThrow, ParseMethodBody(), To(s));
        }

        // Field. Entry, throws, annotations, and static are all meaningless here.
        if (isEntry) Fail("'entry' has no meaning on a field", Codes.BadDeclHeader);
        if (isThrow) Fail("'throws' has no meaning on a field", Codes.BadDeclHeader);
        if (anns.Length > 0) Fail("annotations have no effect on a field", Codes.BadAnnotation);
        if ((mods & Modifiers.Static) != 0) Fail("'static' has no meaning on a field", Codes.BadDeclHeader);

        // 'name = expr;' declares a field whose type is inferred from its initializer, same as
        // 'let name = expr;'. Anything else starts with a type spec.
        TypeSpec? ftype;
        string fname;
        if (At(TK.Ident) && Peek().Kind == TK.Eq)
        {
            ftype = null;
            fname = Advance().Value;
        }
        else
        {
            ftype = ParseTypeSpec();
            fname = Expect(TK.Ident).Value;
        }
        Expr? init = Try(TK.Eq) ? ParseExpr() : null;
        Expect(TK.Semi);
        return new FieldDecl(mods, ftype, fname, To(s), init);
    }

    /// <summary>
    /// Parses an operator symbol for an operator overload declaration. Handles arithmetic,
    /// comparison, bitwise, and indexer operators.
    /// </summary>
    private string ParseOperatorSymbol()
    {
        if (AtP("+") || AtP("-") || AtP("*") || AtP("/") || AtP("<") || AtP(">")) return Advance().Value;
        if (At(TK.EqEq) || At(TK.NotEq) || At(TK.LtEq) || At(TK.GtEq)) return Advance().Value;
        if (AtP("&") || AtP("|") || AtP("^") || At(TK.Shl) || At(TK.Shr)) return Advance().Value;
        // Unary ('!', '~', 0-param '-') and postfix ('++', '--') operators are overloadable too.
        if (AtP("!") || AtP("~")) return Advance().Value;
        if (At(TK.Inc) || At(TK.Dec)) return Advance().Value;
        // 'operator V func [](K)' for getter, 'operator func []=(K, V)' for setter.
        if (At(TK.LBrack)) { Advance(); Expect(TK.RBrack); return Try(TK.Eq) ? "[]=" : "[]"; }
        // 'operator Target func as(Source s)': a user-defined conversion, invoked by
        // 'value as Target'. Declared on the class being converted TO, static (no self) -
        // it converts its one parameter to self, not the other way around.
        if (At(TK.As)) { Advance(); return "as"; }
        Fail($"expected an operator symbol, found {Found()}");
        return "+";
    }

    /// <summary>
    /// Returns true if the current position looks like the start of a method declaration. 'func
    /// Name' with no return type is a method; 'func(' starts a func-pointer type (a field).
    /// Speculatively parses the type spec and checks what follows; restores position either way.
    /// </summary>
    private bool LooksLikeMethod()
    {
        if (At(TK.Func) && Peek().Kind == TK.Ident) return true;
        int n = SkipTypeSpec(0);
        return n >= 0 && Peek(n).Kind == TK.Func;
    }

    /// <summary>
    /// Parses an optional return type before 'func'. Returns null when 'func' is immediately
    /// followed by an identifier (no return type). Otherwise parses and returns the type spec.
    /// </summary>
    private TypeSpec? ParseOptionalReturnType()
    {
        return At(TK.Func) && Peek().Kind == TK.Ident ? null : ParseTypeSpec();
    }

    /// <summary>
    /// Parses a method body. Either a native C block or a Gata statement block.
    /// </summary>
    private MethodBody ParseMethodBody()
    {
        if (At(TK.NativeContent)) return new NativeMethodBody(ParseNativeBody(Advance()));
        return new BlockBody(ParseBlock());
    }

    /// <summary>
    /// Parses zero or more access/storage modifiers into a single flags value. A repeated modifier
    /// and the contradictory 'public private' pair are hard errors.
    /// </summary>
    private Modifiers ParseMods()
    {
        var mods = Modifiers.None;
        while (true)
        {
            Modifiers m = Cur.Kind switch
            {
                TK.Static => Modifiers.Static,
                TK.Public => Modifiers.Public,
                TK.Private => Modifiers.Private,
                _ => Modifiers.None
            };
            if (m == Modifiers.None) break;
            if ((mods & m) != 0) Fail($"duplicate modifier '{Cur.Value}'", Codes.ConflictingModifiers);
            mods |= m;
            Advance();
        }
        if ((mods & Modifiers.Public) != 0 && (mods & Modifiers.Private) != 0)
            Fail("'public' and 'private' cannot be combined on one declaration", Codes.ConflictingModifiers);
        return mods;
    }

    #endregion

    #region Process and thread

    /// <summary>
    /// Parses a process declaration, requiring exactly one foreground/background mode - leading
    /// keyword or trailing colon form, never both, never neither. The mode owns TTY focus and
    /// scheduling visibility, so it is not allowed to default silently.
    /// </summary>
    private ProcessDecl ParseProcessDeclTop()
    {
        int s = Cur.Span.Start;
        string mode = "foreground";
        bool modeExplicit = false;
        if (At(TK.Foreground)) { mode = "foreground"; modeExplicit = true; Advance(); }
        else if (At(TK.Background)) { mode = "background"; modeExplicit = true; Advance(); }
        if (!AtValue("process")) Fail($"expected 'process', found {Found()}", Codes.BadDeclHeader);
        Advance();
        var name = Expect(TK.Ident).Value;
        if (Try(TK.Colon))
        {
            // Two spellings of the mode is an error; one of them has to go.
            if (modeExplicit) Fail($"'{name}': mode specified twice", Codes.BadDeclHeader);
            if (At(TK.Foreground)) { mode = "foreground"; modeExplicit = true; Advance(); }
            else if (At(TK.Background)) { mode = "background"; modeExplicit = true; Advance(); }
            else Fail($"expected 'foreground' or 'background' after ':', found {Found()}", Codes.BadDeclHeader);
        }
        if (!modeExplicit)
            Fail($"'{name}': process declaration is missing a foreground/background mode", Codes.MissingProcessMode,
                 [$"write 'foreground process {name}' or 'background process {name}'"]);
        Expect(TK.LBrace);
        List<ThreadDecl> threads = [];
        List<TopLevel> items = [];
        while (!At(TK.RBrace) && !At(TK.EOF))
        {
            if (AtNestedProcess()) Fail("a process cannot be nested inside another process", Codes.InvalidNesting);
            if (AtThreadStart()) threads.Add(ParseThreadDecl());
            else items.Add(ParseProcessItem());
        }
        Expect(TK.RBrace);
        return new ProcessDecl(name, mode, [.. threads], To(s)) { Items = [.. items] };
    }

    /// <summary>
    /// Reports an 'import' written anywhere but the top level of a file. Otherwise it reaches the
    /// free-function parser and comes back as "expected a type name, found 'import'".
    /// </summary>
    private void RejectStrayImport()
    {
        if (At(TK.Import))
            Fail("an 'import' must be at the top level of the file", Codes.TopologyOutsideRealm,
                 ["move it above the block; imports apply to the whole file"]);
    }

    /// <summary>
    /// Reports a 'thread' outside a process body. 'thread' is contextual, so a stray one otherwise
    /// parses as a type name and reports a missing 'func'.
    /// </summary>
    private void RejectStrayThread()
    {
        if (AtValue("thread") && Peek().Kind == TK.Ident && Peek(2).Kind == TK.LBrace)
            Fail("a 'thread' must be declared inside a 'process' block", Codes.TopologyOutsideRealm,
                 ["threads are a process's entry points; wrap it in 'foreground process P { ... }'"]);
    }

    /// <summary>
    /// True if a thread declaration starts here. A foreground/background prefix is accepted so the
    /// resolver can reject it as G043 with a message about modes, rather than the parser rejecting
    /// it as an unknown declaration.
    /// </summary>
    private bool AtThreadStart()
    {
        return AtValue("thread") || At(TK.Foreground) || At(TK.Background);
    }

    /// <summary>
    /// True if a process declaration starts here, including the foreground/background-prefixed form
    /// that a thread declaration also uses.
    /// </summary>
    private bool AtNestedProcess()
    {
        if (At(TK.Foreground) || At(TK.Background))
            return Peek().Kind == TK.Ident && Peek().Value == "process";
        return AtProcessStart();
    }

    /// <summary>
    /// Dispatches a single non-thread declaration inside a process body. A process holds the same
    /// declaration forms a realm does, minus the two that cannot nest.
    /// </summary>
    private TopLevel ParseProcessItem()
    {
        if (At(TK.Realm)) Fail("a 'realm' block cannot appear inside a process", Codes.InvalidNesting);
        if (At(TK.Kernel)) RequireRealmKeyword();
        if (AtProcessStart()) Fail("a process cannot be nested inside another process", Codes.InvalidNesting);
        RejectStrayImport();

        int s = Cur.Span.Start;
        if (At(TK.AtEnvironment)) { Advance(); return new EnvironmentDecl(To(s)); }
        var anns = ParseAnnotations();
        RejectStrayImport();
        if (AtValue("thread")) RejectAnns(anns, "a thread", allowShadows: false);
        if (At(TK.NativeContent)) return new NativeBlock(ParseNativeBody(Advance()), To(s), anns);
        if (At(TK.NativeTypeDecl)) return ParseNativeType(anns, s);
        if (At(TK.AtExtern)) return ParseExternDecl(anns, s);
        if (At(TK.Enum)) { RejectAnns(anns, "an enum"); return ParseEnumDecl(anns, s); }
        if (At(TK.Union)) { RejectAnns(anns, "a union"); return ParseUnionDecl(anns, s); }
        if (At(TK.Class)) { RejectAnns(anns, "a class", allowKeep: true, allowBuiltin: true); return ParseClassDecl(anns, s); }
        if (At(TK.Module)) { RejectAnns(anns, "a module", allowKeep: true); return ParseModuleDecl(anns, s); }
        if (At(TK.Let)) { RejectAnns(anns, "a process variable", allowShadows: false); return ParseProcessVarDecl(s); }
        return ParseFreeFuncDecl(anns, s);
    }

    /// <summary>
    /// Parses a process-scoped variable.
    /// </summary>
    private ProcessVarDecl ParseProcessVarDecl(int s)
    {
        Expect(TK.Let);
        var type = ParseTypeSpec();
        var name = Expect(TK.Ident).Value;

        Expr? init = null;
        if (Try(TK.Eq)) init = ParseExpr();
        else
            Fail($"process variable '{name}' has no initial value", Codes.UninitialisedProcessVar,
                 [$"write 'let <type> {name} = <value>;'",
                  "every thread of the process shares this one variable, so there is no point later " +
                  "in the program where a first assignment could be known to have run before a read"]);

        Expect(TK.Semi);
        return new ProcessVarDecl(name, type, init, To(s));
    }

    /// <summary>
    /// Parses a thread declaration inside a process body. A foreground or background keyword before
    /// 'thread' is syntactically accepted and captured in Mode; the type resolver rejects it as
    /// G043, since threads don't have their own deployment mode, only the process does.
    /// </summary>
    private ThreadDecl ParseThreadDecl()
    {
        int s = Cur.Span.Start;
        string? mode = null;
        if (At(TK.Foreground)) { mode = "foreground"; Advance(); }
        else if (At(TK.Background)) { mode = "background"; Advance(); }
        if (!AtValue("thread"))
            Fail($"expected 'thread' after '{mode}', found {Found()}", Codes.BadDeclHeader,
                 ["a process body may contain classes, modules, enums, unions, functions, and threads"]);
        Advance();
        var name = Expect(TK.Ident).Value;
        Expect(TK.LBrace);
        var entry = ParseThreadEntry();
        if (!At(TK.RBrace)) Fail("a thread body must contain a single 'entry func' and nothing else", Codes.BadDeclHeader);
        Expect(TK.RBrace);
        return new ThreadDecl(name, mode, entry, To(s));
    }

    /// <summary>
    /// Parses the entry function of a thread. Threads are pure topology, not scopes, so a nested
    /// thread or helper function in the body is a hard error, and the fixed void(*)(void*) ABI
    /// means return types and access modifiers are rejected too.
    /// </summary>
    private EntryFuncDecl ParseThreadEntry()
    {
        int s = Cur.Span.Start;
        if (AtValue("thread")) Fail("threads cannot be nested", Codes.InvalidNesting);
        var mods = ParseMods();
        bool throwsFirst = Try(TK.Throws);
        if (!Try(TK.Entry)) Fail("a thread body must contain a single 'entry func'", Codes.BadDeclHeader);
        if (throwsFirst || At(TK.Throws))
            Fail("a thread entry cannot be 'throws' - the runtime starts it, so there is no caller to receive the error",
                 Codes.BadEntrySignature,
                 ["handle failure inside the thread: 'let T x = f() catch { assign <fallback>; };'"]);
        TypeSpec? ret = At(TK.Func) && Peek().Kind == TK.Ident ? null : ParseTypeSpec();
        Expect(TK.Func);
        if (At(TK.Ident)) Advance(); // entry name is documentation only; the thread is what names it
        Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
        if (ret != null) Fail("a thread entry has no return value; remove the return type", Codes.BadDeclHeader);
        if (mods != Modifiers.None) Fail("access/storage modifiers have no meaning on a thread entry", Codes.BadDeclHeader);
        if (parms.Length > 0)
            Fail("a thread entry takes no parameters; pass state through fields or module data instead", Codes.BadEntrySignature);
        return new EntryFuncDecl(mods, ret, parms, ParseBlock(), To(s));
    }

    #endregion

    #region Parameters

    /// <summary>
    /// Parses a comma-separated parameter list between the surrounding parens (already consumed).
    /// Returns a static empty array for an empty parameter list to avoid an allocation.
    /// </summary>
    private Param[] ParseParamList()
    {
        if (At(TK.RParen)) return [];
        List<Param> ps = [ParseParam()];
        while (Try(TK.Comma)) ps.Add(ParseParam());
        return [.. ps];
    }

    /// <summary>
    /// Parses a single parameter: an optional ref keyword, a type specifier, and a name.
    /// </summary>
    private Param ParseParam()
    {
        int s = Cur.Span.Start;
        bool isRef = Try(TK.Ref);
        TypeSpec type = ParseTypeSpec();
        string name = Expect(TK.Ident).Value;
        return new Param(type, name, To(s), isRef);
    }

    #endregion

    #region Statements

    /// <summary>
    /// Parses a brace-delimited block of statements.
    /// </summary>
    public Block ParseBlock()
    {
        int s = Cur.Span.Start;
        Expect(TK.LBrace);
        List<Stmt> stmts = [];
        while (!At(TK.RBrace) && !At(TK.EOF)) stmts.Add(ParseStmt());
        Expect(TK.RBrace);
        return new Block([.. stmts], To(s));
    }

    /// <summary>
    /// Dispatches to the correct statement parser based on the current token.
    /// </summary>
    private Stmt ParseStmt()
    {
        EnterDepth();
        var stmt = ParseStmtInner();
        ExitDepth();
        return stmt;
    }

    private Stmt ParseStmtInner()
    {
        int s = Cur.Span.Start;
        if (At(TK.NativeContent)) return new NativeStmt(ParseNativeBody(Advance()), To(s));
        if (At(TK.LBrace)) return ParseBlock();
        if (At(TK.Let)) return ParseLetStmt(s);
        if (At(TK.If)) return ParseIfStmt(s);
        if (At(TK.While)) return ParseWhileStmt(s);
        if (At(TK.For)) return ParseForStmt(s);
        if (At(TK.Switch)) return ParseSwitchStmt(s);
        if (At(TK.Match)) return ParseMatchStmt(s);
        if (At(TK.Try)) return ParseTryCatchStmt(s);
        if (At(TK.Unsafe)) return ParseUnsafeBlock(s);
        if (At(TK.Defer)) return ParseDeferStmt(s);
        if (At(TK.Return)) { Advance(); Expr? v = At(TK.Semi) ? null : ParseExpr(); Expect(TK.Semi); return new ReturnStmt(v, To(s)); }
        if (At(TK.Break)) { Advance(); Expect(TK.Semi); return new BreakStmt(To(s)); }
        if (At(TK.Continue)) { Advance(); Expect(TK.Semi); return new ContinueStmt(To(s)); }

        // Throw and debug statements are not expressions, so they must be handled here instead of
        // in ParseExprOrAssign.
        if (At(TK.Throw)) {
            Advance();
            Expect(TK.Semi);
            return new ThrowStmt(To(s));
        }

        // `assign v;` terminates a catch handler. Parsed here rather than in ParseExprOrAssign
        // for the same reason as throw: it transfers control, it is not an expression.
        if (At(TK.Assign)) {
            Advance();
            var value = ParseExpr();
            Expect(TK.Semi);
            return new AssignValueStmt(value, To(s));
        }
        if (At(TK.Debug)) {
            Advance();
            if (!At(TK.StrLit)) Fail("'debug' takes a string literal", hints: ["e.g. debug \"message\";"]);
            var raw = Advance().Value;
            Expect(TK.Semi);
            return new DebugStmt(raw, To(s));
        }

        // Panic is a statement, not an expression, so it must be handled here instead of in
        // ParseExprOrAssign.
        if (At(TK.Panic)) {
            Advance();
            if (!At(TK.StrLit)) Fail("'panic' takes a string literal", hints: ["e.g. panic \"message\";"]);
            var raw = Advance().Value;
            Expect(TK.Semi);
            return new PanicStmt(raw, To(s));
        }
        if (LooksLikeMissingLet())
            Fail("expected a statement", Codes.MissingLet,
                At(TK.Ident)
                    ? ["missing 'let'?", $"e.g. 'let {Cur.Value} ...'"]
                    : ["missing 'let'?"]);
        return ParseExprOrAssign(s);
    }

    /// <summary>
    /// Parses a let declaration. The type is optional; LooksLikeTypeAndIdent is the single
    /// lookahead deciding whether a declared type precedes the name, shared with the for-init form
    /// so the two positions can never disagree.
    /// </summary>
    private LetStmt ParseLetStmt(int s)
    {
        Expect(TK.Let);
        TypeSpec? type = LooksLikeTypeAndIdent() ? ParseTypeSpec() : null;
        string name = Expect(TK.Ident).Value;
        Expr? init = Try(TK.Eq) ? ParseExpr() : null;
        Expect(TK.Semi);
        return new LetStmt(type, name, init, To(s));
    }

    /// <summary>
    /// Returns the index just past a balanced "[...]" run starting at token offset n, or -1 if it
    /// never closes before EOF. Used by SkipTypeSpec to jump over generic argument lists.
    /// </summary>
    private int SkipBrackets(int n)
    {
        int depth = 0;
        do
        {
            var t = Peek(n);
            if (t.Kind == TK.EOF) return -1;
            if (t.Kind == TK.LBrack) depth++;
            else if (t.Kind == TK.RBrack) depth--;
            n++;
        } while (depth > 0);
        return n;
    }

    /// <summary>
    /// Lookahead mirror of ParseFuncTypeSpec. Returns the index just past the function pointer type
    /// starting at offset n, or -1 if the token stream does not match.
    /// </summary>
    private int SkipFuncTypeSpec(int n)
    {
        if (Peek(n).Kind != TK.Func) return -1;
        n++;
        if (Peek(n).Kind != TK.LParen) return -1;
        n++;
        if (Peek(n).Kind != TK.RParen)
        {
            n = SkipTypeSpec(n);
            if (n < 0) return -1;
            while (Peek(n).Kind == TK.Comma)
            {
                n++;
                n = SkipTypeSpec(n);
                if (n < 0) return -1;
            }
        }
        if (Peek(n).Kind != TK.RParen) return -1;
        n++;
        if (Peek(n).Kind != TK.Arrow) return -1;
        n++;
        return SkipTypeSpec(n);
    }

    /// <summary>
    /// Lookahead mirror of ParseTypeSpec. Returns the index just past the type starting at offset n
    /// (Peek(0) = Cur), or -1 if offset n is not the start of a valid type.
    /// </summary>
    private int SkipTypeSpec(int n)
    {
        while (Peek(n).Kind == TK.LBrack && Peek(n + 1).Kind == TK.IntLit && Peek(n + 2).Kind == TK.RBrack)
            n += 3;
        if (Peek(n).Kind == TK.Func)
        {
            n = SkipFuncTypeSpec(n);
            if (n < 0) return -1;
        }
        else if (IsPrim(Peek(n).Kind))
        {
            n++;
        }
        else if (Peek(n).Kind is TK.Ident or TK.ColonColon or TK.Kernel or TK.Userspace)
        {
            if (Peek(n).Kind == TK.ColonColon) n++;
            else if (Peek(n).Kind is TK.Kernel or TK.Userspace)
            {
                if (Peek(n + 1).Kind != TK.Dot) return -1;
                n += 2;
            }
            if (Peek(n).Kind != TK.Ident) return -1;
            n++;
            while (Peek(n).Kind == TK.Dot && Peek(n + 1).Kind == TK.Ident) n += 2;
            if (Peek(n).Kind == TK.LBrack) { n = SkipBrackets(n); if (n < 0) return -1; }
        }
        else return -1;
        while (Peek(n).Kind == TK.Punct && Peek(n).Value == "*") n++;
        return n;
    }

    /// <summary>
    /// Returns true if the current position looks like a type spec immediately followed by an
    /// identifier, which is always a missing 'let' and never valid expression syntax. Pure
    /// lookahead; never consumes tokens.
    /// </summary>
    private bool LooksLikeMissingLet()
    {
        if (!At(TK.Ident) && !At(TK.LBrack)) return false;
        int n = SkipTypeSpec(0);
        return n >= 0 && Peek(n).Kind == TK.Ident;
    }

    /// <summary>
    /// Returns true if the current position looks like a type specifier followed by an identifier,
    /// meaning the let statement has an explicit type annotation.
    /// </summary>
    private bool LooksLikeTypeAndIdent()
    {
        if (IsPrim(Cur.Kind)) return true;
        if (At(TK.Func)) return true;
        if (At(TK.LBrack) && Peek().Kind == TK.IntLit && Peek(2).Kind == TK.RBrack) return true;
        if (At(TK.ColonColon)) return true;
        if (At(TK.Kernel) || At(TK.Userspace)) return Peek().Kind == TK.Dot;
        if (!At(TK.Ident)) return false;
        return Peek().Kind == TK.Ident
            || Peek().Kind == TK.LBrack
            || (Peek().Kind == TK.Punct && Peek().Value == "*");
    }

    /// <summary>
    /// Parses a let declaration without consuming its trailing semicolon. Used in for-loop init
    /// clauses where the semicolon belongs to the for syntax, not the let.
    /// </summary>
    private LetStmt ParseLetNoSemi()
    {
        int s = Cur.Span.Start;
        Expect(TK.Let);
        TypeSpec? type = LooksLikeTypeAndIdent() ? ParseTypeSpec() : null;
        string name = Expect(TK.Ident).Value;
        Expr? init = Try(TK.Eq) ? ParseExpr() : null;
        return new LetStmt(type, name, init, To(s));
    }

    /// <summary>
    /// Parses an if/else statement. The then and else branches are full statements, so a bare
    /// block, a single statement, or a nested if are all valid without extra rules.
    /// </summary>
    private IfStmt ParseIfStmt(int s)
    {
        Expect(TK.If); Expect(TK.LParen); var cond = ParseExpr();
        NoAssignHere("an 'if' condition", At(TK.Eq) ? "did you mean '=='?" : "assign before the 'if' instead");
        Expect(TK.RParen);
        var then = ParseStmt();
        Stmt? els = Try(TK.Else) ? ParseStmt() : null;
        return new IfStmt(cond, then, els, To(s));
    }

    /// <summary>
    /// Parses a while loop. The condition is parenthesised; the body is a full statement.
    /// </summary>
    private WhileStmt ParseWhileStmt(int s)
    {
        Expect(TK.While); Expect(TK.LParen); var cond = ParseExpr();
        NoAssignHere("a 'while' condition", At(TK.Eq) ? "did you mean '=='?" : "move the update into the loop body");
        Expect(TK.RParen);
        return new WhileStmt(cond, ParseStmt(), To(s));
    }

    /// <summary>
    /// Parses a for loop. Disambiguates between 'for x in col { }' (ForInStmt, no parens) and the
    /// C-style 'for (init; cond; step) { }' (ForStmt) by peeking for the 'in' keyword.
    /// </summary>
    private Stmt ParseForStmt(int s)
    {
        Expect(TK.For);

        // for x in col { } -- range loop, no parens
        if (At(TK.Ident) && Peek().Kind == TK.In)
        {
            string var = Advance().Value;
            Advance(); // consume 'in'
            return new ForInStmt(var, ParseExpr(), ParseBlock(), To(s));
        }

        // C-style for (init; cond; step) { }
        Expect(TK.LParen);
        
        if ((At(TK.Ident) && Peek().Kind == TK.In)
            || (At(TK.Let) && Peek().Kind == TK.Ident && Peek(2).Kind == TK.In))
            Fail("a 'for ... in' loop is written without parentheses",
                 hints: ["write 'for x in xs { ... }'",
                         "the parenthesised form is the C-style loop, which takes " +
                         "'for (init; condition; step)'"]);

        Stmt? init = null;
        if (!At(TK.Semi))
            init = At(TK.Let) ? ParseLetNoSemi() : ParseForClause();
        Expect(TK.Semi);
        Expr? cond = At(TK.Semi) ? null : ParseExpr();
        if (cond != null)
            NoAssignHere("the loop condition", At(TK.Eq) ? "did you mean '=='?" : "move the update into the loop body");
        Expect(TK.Semi);
        Stmt? step = null;
        if (!At(TK.RParen))
        {
            if (At(TK.Let)) Fail("cannot declare a variable in the for-loop step");
            step = ParseForClause();
        }
        Expect(TK.RParen);
        return new ForStmt(init, cond, step, ParseBlock(), To(s));
    }

    /// <summary>
    /// Parses a for-loop init or step clause without a trailing semicolon: an expression,
    /// optionally promoted to an assignment when an assignment operator follows.
    /// </summary>
    private Stmt ParseForClause()
    {
        int es = Cur.Span.Start;
        var lhs = ParseExpr();
        if (IsAssignTk(Cur.Kind))
        {
            var op = AssignOpOf(Cur.Kind); Advance();
            return new AssignStmt(lhs, op, ParseExpr(), To(es));
        }
        return new ExprStmt(lhs, To(es));
    }

    /// <summary>
    /// Parses a try/catch statement. Both the try and catch branches are blocks.
    /// </summary>
    private TryCatchStmt ParseTryCatchStmt(int s)
    {
        Expect(TK.Try);
        Block tryBlock = ParseBlock();
        Expect(TK.Catch);
        return new TryCatchStmt(tryBlock, ParseBlock(), To(s));
    }

    /// <summary>
    /// Parses an unsafe block. Pointer operations inside are permitted; the type checker rejects
    /// them everywhere else.
    /// </summary>
    private UnsafeBlock ParseUnsafeBlock(int s)
    {
        Expect(TK.Unsafe);
        var block = ParseBlock();
        return new UnsafeBlock(block.Stmts, To(s));
    }

    /// <summary>
    /// Parses a defer statement. The deferred action is a single statement that runs on every exit
    /// from the enclosing block, in LIFO order with other defers.
    /// </summary>
    private DeferStmt ParseDeferStmt(int s)
    {
        Expect(TK.Defer);
        return new DeferStmt(ParseStmt(), To(s));
    }

    /// <summary>
    /// Parses an expression statement or assignment. After parsing the left-hand expression, any
    /// assignment operator promotes the result to an AssignStmt; otherwise it's an ExprStmt.
    /// </summary>
    private Stmt ParseExprOrAssign(int s)
    {
        var expr = ParseExpr();
        if (IsAssignTk(Cur.Kind))
        {
            var op = AssignOpOf(Cur.Kind); Advance();
            var val = ParseExpr();
            Expect(TK.Semi);
            return new AssignStmt(expr, op, val, To(s));
        }
        Expect(TK.Semi);
        return new ExprStmt(expr, To(s));
    }

    /// <summary>
    /// Maps an assignment-operator token kind to its AssignOp value.
    /// </summary>
    private static AssignOp AssignOpOf(TK k) => k switch
    {
        TK.Eq => AssignOp.Assign,
        TK.PlusEq => AssignOp.AddAssign,
        TK.MinusEq => AssignOp.SubAssign,
        TK.StarEq => AssignOp.MulAssign,
        TK.SlashEq => AssignOp.DivAssign,
        TK.PercentEq => AssignOp.ModAssign,
        TK.AmpEq => AssignOp.AndAssign,
        TK.PipeEq => AssignOp.OrAssign,
        TK.CaretEq => AssignOp.XorAssign,
        TK.ShlEq => AssignOp.ShlAssign,
        TK.ShrEq => AssignOp.ShrAssign,
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

    #endregion

    #region Expressions

    /// <summary>
    /// Entry point for all expression parsing.
    /// </summary>
    public Expr ParseExpr()
    {
        return ParseTernary();
    }

    /// <summary>
    /// Parses a ternary conditional. Right-associative so nested ternaries chain without parens.
    /// '?' falls through to TK.Punct since it has no dedicated token kind.
    /// </summary>
    private Expr ParseTernary()
    {
        EnterDepth();
        var result = ParseTernaryInner();
        ExitDepth();
        return result;
    }

    private Expr ParseTernaryInner()
    {
        int s = Cur.Span.Start;
        var left = ParseOr();
        if (!AtP("?")) return left;
        Advance();
        var then = ParseExpr();
        if (At(TK.ColonColon))
            Fail("'::' names the root scope and cannot be the ':' of a conditional",
                 Codes.Syntax, ["put a space after the ':', as in 'c ? a : ::Name'"]);
        Expect(TK.Colon);
        return new TernaryExpr(left, then, ParseTernary(), To(s));
    }

    /// <summary>
    /// Parses '||' chains.
    /// </summary>
    private Expr ParseOr()
    {
        int s = Cur.Span.Start;
        var left = ParseAnd();
        while (At(TK.Or)) { Advance(); left = new BinExpr(BinOp.Or, left, ParseAnd(), To(s)); }
        return left;
    }

    /// <summary>
    /// Parses '&amp;&amp;' chains.
    /// </summary>
    private Expr ParseAnd()
    {
        int s = Cur.Span.Start;
        var left = ParseBitOr();
        while (At(TK.And)) { Advance(); left = new BinExpr(BinOp.And, left, ParseBitOr(), To(s)); }
        return left;
    }

    /// <summary>
    /// Parses bitwise '|' chains.
    /// </summary>
    private Expr ParseBitOr()
    {
        int s = Cur.Span.Start;
        var left = ParseBitXor();
        while (AtP("|")) { Advance(); left = new BinExpr(BinOp.BitOr, left, ParseBitXor(), To(s)); }
        return left;
    }

    /// <summary>
    /// Parses bitwise '^' chains.
    /// </summary>
    private Expr ParseBitXor()
    {
        int s = Cur.Span.Start;
        var left = ParseBitAnd();
        while (AtP("^")) { Advance(); left = new BinExpr(BinOp.BitXor, left, ParseBitAnd(), To(s)); }
        return left;
    }

    /// <summary>
    /// Parses bitwise '&amp;' chains.
    /// </summary>
    private Expr ParseBitAnd()
    {
        int s = Cur.Span.Start;
        var left = ParseEquality();
        while (AtP("&")) { Advance(); left = new BinExpr(BinOp.BitAnd, left, ParseEquality(), To(s)); }
        return left;
    }

    /// <summary>
    /// Parses '==' and '!=' chains.
    /// </summary>
    private Expr ParseEquality()
    {
        int s = Cur.Span.Start;
        var left = ParseRelational();
        while (At(TK.EqEq) || At(TK.NotEq))
        {
            var op = At(TK.EqEq) ? BinOp.Eq : BinOp.Ne;
            Advance();
            left = new BinExpr(op, left, ParseRelational(), To(s));
        }
        return left;
    }

    /// <summary>
    /// Parses relational comparisons: less-than, greater-than, and their equal variants.
    /// </summary>
    private Expr ParseRelational()
    {
        int s = Cur.Span.Start;
        var left = ParseShift();
        while (AtP("<") || AtP(">") || At(TK.LtEq) || At(TK.GtEq))
        {
            var op = AtP("<") ? BinOp.Lt : AtP(">") ? BinOp.Gt : At(TK.LtEq) ? BinOp.Le : BinOp.Ge;
            Advance();
            left = new BinExpr(op, left, ParseShift(), To(s));
        }
        return left;
    }

    /// <summary>
    /// Parses '&lt;&lt;' and '&gt;&gt;' chains.
    /// </summary>
    private Expr ParseShift()
    {
        int s = Cur.Span.Start;
        var left = ParseAdditive();
        while (At(TK.Shl) || At(TK.Shr))
        {
            var op = At(TK.Shl) ? BinOp.Shl : BinOp.Shr;
            Advance();
            left = new BinExpr(op, left, ParseAdditive(), To(s));
        }
        return left;
    }

    /// <summary>
    /// Parses '+' and '-' chains.
    /// </summary>
    private Expr ParseAdditive()
    {
        int s = Cur.Span.Start;
        var left = ParseMultiplicative();
        while (AtP("+") || AtP("-"))
        {
            var op = AtP("+") ? BinOp.Add : BinOp.Sub;
            Advance();
            left = new BinExpr(op, left, ParseMultiplicative(), To(s));
        }
        return left;
    }

    /// <summary>
    /// Parses '*', '/', and '%' chains.
    /// </summary>
    private Expr ParseMultiplicative()
    {
        int s = Cur.Span.Start;
        var left = ParseAs();
        while (AtP("*") || AtP("/") || AtP("%"))
        {
            var op = AtP("*") ? BinOp.Mul : AtP("/") ? BinOp.Div : BinOp.Mod;
            Advance();
            left = new BinExpr(op, left, ParseAs(), To(s));
        }
        return left;
    }

    /// <summary>
    /// Parses 'expr as Type' casts. Tighter than '*' so 'x * y as T' means 'x * (y as T)'.
    /// User-defined type casts use 'as'; primitive casts use the C-style '(PrimType)' form.
    /// </summary>
    private Expr ParseAs()
    {
        int s = Cur.Span.Start;
        var expr = ParseUnary();
        while (At(TK.As)) { Advance(); expr = new CastExpr(ParseTypeSpec(), expr, To(s)); }
        return expr;
    }

    /// <summary>
    /// Parses prefix unary operators. '&amp;' and '*' are only legal inside unsafe blocks but are
    /// accepted here; the type checker enforces the restriction.
    /// </summary>
    private Expr ParseUnary()
    {
        EnterDepth();
        var result = ParseUnaryInner();
        ExitDepth();
        return result;
    }

    private Expr ParseUnaryInner()
    {
        int s = Cur.Span.Start;
        if (AtP("!")) { Advance(); return new UnaryExpr(UnOp.Not, ParseUnary(), To(s)); }
        if (AtP("~")) { Advance(); return new UnaryExpr(UnOp.BitNot, ParseUnary(), To(s)); }
        if (AtP("-")) { Advance(); return new UnaryExpr(UnOp.Neg, ParseUnary(), To(s)); }
        if (AtP("&")) { Advance(); return new AddrOfExpr(ParseUnary(), To(s)); }
        if (AtP("*")) { Advance(); return new DerefExpr(ParseUnary(), To(s)); }
        return ParsePostfix();
    }

    /// <summary>
    /// Parses postfix operators: '++', '--', '.member', '[index]', and '(args)' call.
    /// </summary>
    private Expr ParsePostfix()
    {
        int s = Cur.Span.Start;
        var expr = ParsePrimary();
        while (true)
        {
            if (At(TK.Inc)) { Advance(); expr = new PostfixExpr(PostfixOp.Inc, expr, To(s)); }
            else if (At(TK.Dec)) { Advance(); expr = new PostfixExpr(PostfixOp.Dec, expr, To(s)); }
            else if (At(TK.Dot)) { Advance(); expr = new MemberAccessExpr(expr, Expect(TK.Ident).Value, To(s)); }
            else if (At(TK.LBrack)) { expr = ParseBracketed(expr, s); }
            else if (At(TK.LParen)) { Advance(); var args = ParseArgList(); Expect(TK.RParen); expr = new CallExpr(expr, args, To(s)); }
            else if (At(TK.Catch))
            {
                if (expr is not CallExpr)
                    Fail("'catch' here must follow a call to a 'throws' function",
                        hints: ["e.g. let int x = Parse(s) catch { assign 0; };"]);
                Advance();
                expr = new CatchCallExpr(expr, ParseBlock(), To(s));
            }
            else break;
        }
        return expr;
    }

    /// <summary>
    /// Parses '[ ... ]' after an expression: an index, a generic type reference, or a node carrying
    /// both for the resolver. Only 'Ident[...].' can be a type, and a failed reading is rolled back
    /// whole - cursor, depth and generic-use registrations.
    /// </summary>
    private Expr ParseBracketed(Expr expr, int s)
    {
        RejectExplicitTypeArgs();
        if (expr is not IdentExpr id) return ParseIndexRest(expr, s);

        var start = Mark();

        // The type reading. It needs a '.' after the brackets: 'Maybe[int]' on its own is a type
        // in value position, which is never legal, and reading it as one only worsens the error.
        NamedSpec[]? typeArgs = null;
        var typeEnd = start;
        List<GenericUse> typeUses = [];
        try
        {
            Advance();
            var args = new List<NamedSpec> { ParseTypeName() };
            while (Try(TK.Comma)) args.Add(ParseTypeName());
            if (At(TK.RBrack))
            {
                Advance();
                if (At(TK.Dot))
                {
                    typeArgs = [.. args];
                    typeEnd = Mark();
                    typeUses = _gu.GetRange(start.Uses, _gu.Count - start.Uses);
                }
            }
        }
        catch (ParseException) { /* not a type list; the index reading stands alone */ }

        Rewind(start);
        if (typeArgs == null) return ParseIndexRest(expr, s);

        // The index reading, from the same starting token.
        Expr? indexForm = null;
        try
        {
            Advance();
            var idx = ParseExpr();
            if (At(TK.RBrack)) { Advance(); indexForm = idx; }
        }
        catch (ParseException) { /* not an expression */ }

        Rewind(typeEnd with { Uses = start.Uses });
        _gu.AddRange(typeUses);

        var outerArgs = new string[typeArgs.Length];
        for (int i = 0; i < typeArgs.Length; i++) outerArgs[i] = typeArgs[i].Mangled;
        _gu.Add(new GenericUse(id.Name, outerArgs, To(s), typeArgs));

        return new GenericTypeRefExpr(id.Name, typeArgs, indexForm, To(s));
    }

    /// <summary>
    /// True for a token that can only ever begin a type. The primitive spellings are split across
    /// several kinds rather than sharing one, so every branch has to be named.
    /// </summary>
    private static bool IsTypeKeyword(TK k) =>
        k is TK.TPrim or TK.TInt or TK.TBool or TK.TChar or TK.TFloat or TK.TDouble
             or TK.TShort or TK.TVoid;

    /// <summary>
    /// Reports an attempt to pass explicit type arguments to a call - 'Sort[int](xs)'.
    /// </summary>
    private void RejectExplicitTypeArgs()
    {
        int i = _pp + 1;
        int end = _tokens.Length;
        bool sawPrim = false;
        for (int depth = 1; i < end; i++)
        {
            var k = _tokens[i].Kind;
            if (k == TK.LBrack) depth++;
            else if (k == TK.RBrack && --depth == 0) break;
            else if (IsTypeKeyword(k)) sawPrim = true;
            else if (k is TK.LParen or TK.Semi or TK.LBrace) return;   // not a bracket group at all
        }
        if (!sawPrim || i >= end || _tokens[i].Kind != TK.RBrack) return;
        if (i + 1 >= end || _tokens[i + 1].Kind != TK.LParen) return;

        Fail("a function call cannot take explicit type arguments",
             Codes.ExplicitTypeArgs,
             hints: ["type parameters are inferred from the argument types, so write 'f(x)' rather " +
                     "than 'f[T](x)'",
                     "if the element at an index is what you meant to call, the index has to be an " +
                     "expression - a type name is not one"]);
    }

    /// <summary>
    /// Everything a speculative parse may advance, so it can be put back exactly.
    /// </summary>
    private readonly record struct Snapshot(int Pos, int End, int Depth, int Uses);

    private Snapshot Mark() => new(_pp, _pe, _depth, _gu.Count);

    /// <summary>
    /// Restores the parser to a snapshot. Depth is part of it because a ParseException unwinds past
    /// every ExitDepth, and the leak is cumulative: 195 ordinary 'a[0].x' expressions reached
    /// MaxDepth and were rejected as nested too deeply.
    /// </summary>
    private void Rewind(Snapshot m)
    {
        _pp = m.Pos;
        _pe = m.End;
        _depth = m.Depth;
        if (_gu.Count > m.Uses) _gu.RemoveRange(m.Uses, _gu.Count - m.Uses);
    }

    /// <summary>
    /// Parses the remainder of an index expression, with '[' as the current token.
    /// </summary>
    private Expr ParseIndexRest(Expr obj, int s)
    {
        Advance();
        var idx = ParseExpr();
        Expect(TK.RBrack);
        return new IndexExpr(obj, idx, To(s));
    }

    /// <summary>
    /// Parses a comma-separated argument list terminated by ')'. Returns an empty array immediately
    /// if ')' is already the current token, avoiding an allocation on every empty call.
    /// </summary>
    private Expr[] ParseArgList()
    {
        if (At(TK.RParen)) return [];
        List<Expr> args = [ParseArg()];
        while (Try(TK.Comma)) args.Add(ParseArg());
        return [.. args];
    }

    /// <summary>
    /// Parses a single call argument. 'ref' is only valid at the call-argument level, not as a
    /// general unary prefix, so it is handled here rather than in ParseUnary.
    /// </summary>
    private Expr ParseArg()
    {
        int s = Cur.Span.Start;
        if (Try(TK.Ref)) return new RefArgExpr(ParseExpr(), To(s));
        return ParseExpr();
    }

    /// <summary>
    /// Parses a primary expression. EnterDepth guards against pathological nesting like
    /// ((((((...)))))) producing a stack overflow instead of a clean diagnostic.
    /// </summary>
    private Expr ParsePrimary()
    {
        EnterDepth();
        var result = ParsePrimaryInner();
        ExitDepth();
        return result;
    }

    /// <summary>
    /// Dispatches to the correct primary form: literal, ident, sizeof, default, new, array literal,
    /// grouped expression, primitive cast, or interpolated string.
    /// </summary>
    private Expr ParsePrimaryInner()
    {
        int s = Cur.Span.Start;

        // A scope qualifier swallows the dotted run after it: which segment ends the scope, which
        // is the name, and which are member accesses is a question only the scope tree can answer.
        if (ParseScopeQualifier() is { } scope)
        {
            List<string> path = [ExpectIdent("a scope or declaration name")];
            while (At(TK.Dot) && Peek().Kind == TK.Ident) { Advance(); path.Add(Advance().Value); }

            if (At(TK.LBrack))
            {
                var spec = FinishTypeName(path[^1], [.. scope, .. path[..^1]], s);
                List<string> members = [];
                while (At(TK.Dot) && Peek().Kind == TK.Ident) { Advance(); members.Add(Advance().Value); }
                return new ScopedNameExpr(scope, [.. members], To(s), spec);
            }
            return new ScopedNameExpr(scope, [.. path], To(s));
        }

        // Literals and identifiers are all single-token forms.
        if (At(TK.IntLit)) { var t = Advance(); return new IntLitExpr(t.Value, t.Span); }
        if (At(TK.FloatLit)) { var t = Advance(); return new FloatLitExpr(t.Value, t.Span); }
        if (At(TK.BoolLit)) { var t = Advance(); return new BoolLitExpr(t.Value, t.Span); }
        if (At(TK.CharLit)) { var t = Advance(); return new CharLitExpr(int.Parse(t.Value), t.Span); }
        if (At(TK.StrLit)) { var t = Advance(); return new StrLitExpr(t.Value, t.Span); }
        if (At(TK.Null)) { Advance(); return new NullExpr(To(s)); }
        if (At(TK.InterpStrStart)) return ParseInterpStr(s);

        // sizeof(Type) and default(Type) are special forms that take a type specifier in
        // parentheses.
        if (At(TK.Sizeof))
        {
            Advance(); Expect(TK.LParen);
            var t = ParseTypeSpec();
            Expect(TK.RParen);
            return new SizeofExpr(t, To(s));
        }
        if (At(TK.Default))
        {
            Advance(); Expect(TK.LParen);
            var t = ParseTypeSpec();
            Expect(TK.RParen);
            return new DefaultExpr(t, To(s));
        }

        // 'new Type(...)' or 'new Type[...]' or 'new Type' for fixed-size arrays.
        if (At(TK.New)) return ParseNewExpr(s);

        //  [elem1, elem2, ...] or [] for an empty array.
        if (At(TK.LBrack))
        {
            Advance();
            if (At(TK.RBrack)) { Advance(); return new ArrayLitExpr([], To(s)); }
            List<Expr> elems = [ParseExpr()];
            while (Try(TK.Comma)) elems.Add(ParseExpr());
            Expect(TK.RBrack);
            return new ArrayLitExpr([.. elems], To(s));
        }

        // Parenthesised expression or primitive cast. Unambiguous because the type must be a
        // primitive keyword or identifier and the cast must be followed by a unary expression.
        // User-defined types are not allowed here - they would collide with a grouped expression.
        if (At(TK.LParen))
        {
            Advance();
            // (PrimType) expr is an unambiguous C-style cast. User-type casts use 'as'.
            if (IsPrim(Cur.Kind))
            {
                var targetType = ParseTypeSpec();
                Expect(TK.RParen);
                return new CastExpr(targetType, ParseUnary(), To(s));
            }
            var e = ParseExpr();
            Expect(TK.RParen);
            return e;
        }

        if (At(TK.Ident)) { var t = Advance(); return new IdentExpr(t.Value, t.Span); }

        Fail($"expected an expression, found {Found()}");
        return new NullExpr(To(s)); // unreachable
    }

    /// <summary>
    /// Parses an interpolated string. The lexer emits InterpStrStart, then alternating StrLit and
    /// Punct("{") ... Punct("}") pairs for embedded expressions, then InterpStrEnd.
    /// </summary>
    private InterpStrExpr ParseInterpStr(int s)
    {
        Advance(); // consume InterpStrStart
        List<Expr> parts = [];
        while (!At(TK.InterpStrEnd) && !At(TK.EOF))
        {
            if (At(TK.StrLit)) { var t = Advance(); parts.Add(new StrLitExpr(t.Value, t.Span)); }
            else if (AtP("{")) { Advance(); parts.Add(ParseExpr()); if (!AtP("}")) Fail($"expected '}}' to close the interpolated expression, found {Found()}"); Advance(); }
            else break;
        }
        Expect(TK.InterpStrEnd);
        return new InterpStrExpr([.. parts], To(s));
    }

    /// <summary>
    /// Parses a 'new' expression. An optional constructor arg list and an optional collection
    /// initializer may each follow the type spec, independently. A bare 'new Type' parses too; the
    /// resolver rejects it with NewOnNonClass for anything but a class.
    /// </summary>
    private NewExpr ParseNewExpr(int s)
    {
        Expect(TK.New);
        TypeSpec type = ParseTypeSpec();
        Expr[] args = [];
        if (At(TK.LParen))
        {
            Advance(); args = ParseArgList(); Expect(TK.RParen);
        }
        if (At(TK.LBrace)) return new NewExpr(type, args, ParseCollectionInit(TK.RBrace), To(s));
        if (At(TK.LBrack)) return new NewExpr(type, args, ParseCollectionInit(TK.RBrack), To(s));
        return new NewExpr(type, args, [], To(s));
    }

    /// <summary>
    /// Parses a delimited, comma-separated element list for a 'new' collection initializer. Returns
    /// an empty array for an empty delimiter pair.
    /// </summary>
    private Expr[] ParseCollectionInit(TK close)
    {
        Advance(); // opening delimiter
        if (At(close)) { Advance(); return []; }
        List<Expr> elems = [ParseExpr()];
        while (Try(TK.Comma)) elems.Add(ParseExpr());
        Expect(close);
        return [.. elems];
    }

    #endregion

    #region Switch and match

    /// <summary>
    /// Parses a switch statement. Each 'case' arm carries one or more comma-separated labels and a
    /// block body. An optional 'default' arm catches all unmatched values.
    /// </summary>
    private SwitchStmt ParseSwitchStmt(int s)
    {
        Expect(TK.Switch); Expect(TK.LParen); var scrut = ParseExpr(); Expect(TK.RParen);
        Expect(TK.LBrace);
        List<SwitchCase> cases = [];
        Block? def = null;
        while (!At(TK.RBrace) && !At(TK.EOF))
        {
            if (At(TK.Default))
            {
                Advance();
                if (def != null) Fail("'switch' already has a 'default' arm; remove one", Codes.DuplicateName);
                def = ParseBlock();
                continue;
            }
            int cs = Cur.Span.Start;
            Expect(TK.Case);
            List<Expr> labels = [ParseExpr()];
            while (Try(TK.Comma)) labels.Add(ParseExpr());
            cases.Add(new SwitchCase([.. labels], ParseBlock(), To(cs)));
        }
        Expect(TK.RBrace);
        return new SwitchStmt(scrut, [.. cases], def, To(s));
    }

    /// <summary>
    /// Parses a match statement. Each 'case' arm names a union variant and optionally binds its
    /// payload fields. An optional 'default' arm catches unmatched variants.
    /// </summary>
    private MatchStmt ParseMatchStmt(int s)
    {
        Expect(TK.Match); Expect(TK.LParen); var scrut = ParseExpr(); Expect(TK.RParen);
        Expect(TK.LBrace);
        List<MatchCase> cases = [];
        Block? def = null;
        while (!At(TK.RBrace) && !At(TK.EOF))
        {
            if (At(TK.Default))
            {
                Advance();
                if (def != null) Fail("'match' already has a 'default' arm; remove one", Codes.DuplicateName);
                def = ParseBlock();
                continue;
            }
            int cs = Cur.Span.Start;
            Expect(TK.Case);
            string variant = Expect(TK.Ident).Value;
            List<string> binds = [];
            if (At(TK.LParen))
            {
                Advance();
                if (!At(TK.RParen))
                {
                    binds.Add(Expect(TK.Ident).Value);
                    while (Try(TK.Comma)) binds.Add(Expect(TK.Ident).Value);
                }
                Expect(TK.RParen);
            }
            cases.Add(new MatchCase(variant, [.. binds], ParseBlock(), To(cs)));
        }
        Expect(TK.RBrace);
        return new MatchStmt(scrut, [.. cases], def, To(s));
    }

    #endregion
}
