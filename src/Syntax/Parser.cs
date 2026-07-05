namespace Appa;

/// <summary>
/// Recursive-descent parser that converts a flat token stream into an untyped AST.
/// One instance per file. Call ParseProgram() once and discard.
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
    /// Increments the recursion depth counter and throws if it exceeds MaxDepth. Always call ExitDepth in a finally block.
    /// </summary>
    private void EnterDepth() { if (++_depth > MaxDepth) Fail("nested too deeply"); }

    /// <summary>
    /// Decrements the recursion depth counter. Always call in a finally block paired with EnterDepth.
    /// </summary>
    private void ExitDepth()
    {
        _depth--;
    }

    // Generic instantiation sites collected during parsing, consumed by the Monomorphizer.
    private readonly List<GenericUse> _gu = [];

    #region Core stream helpers

    /// <summary>
    /// Returns the token at the current position. Safe without a bounds check because Advance() clamps _pp to [0, Length-1].
    /// </summary>
    private Token Cur => _tokens[_pp];

    /// <summary>
    /// Returns the token n positions ahead of the current position, or the last token if the offset exceeds the stream length.
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
            TK.Semi => "';'", TK.Comma => "','", TK.Colon => "':'",
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
    /// Consumes the current token and returns true if it matches the given kind; otherwise returns false without consuming.
    /// </summary>
    private bool Try(TK k) { if (At(k)) { Advance(); return true; } return false; }

    /// <summary>
    /// Returns true if the current token is TK.Punct with the given value. Only for operator tokens kept as TK.Punct: + - * / % and | ^ less-than greater-than ! ~
    /// </summary>
    private bool AtP(string v)
    {
        return Cur.Kind == TK.Punct && Cur.Value == v;
    }

    /// <summary>
    /// Throws a ParseException with the given message at the current token's span.
    /// </summary>
    private void Fail(string m, string code = Codes.Syntax)
    {
        throw new ParseException(Cur.Span, m, code);
    }

    /// <summary>
    /// Throws a ParseException with the given message at an explicit span.
    /// </summary>
    private static void FailAt(TextSpan span, string m, string code = Codes.Syntax)
    {
        throw new ParseException(span, m, code);
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
    /// After an expression has been parsed in a position where only an expression is legal,
    /// rejects a trailing assignment operator with a targeted message instead of letting the
    /// generic "expected ')'" error fire.
    /// </summary>
    private void NoAssignHere(string where, string hint)
    {
        if (IsAssignTk(Cur.Kind))
            Fail($"assignment is a statement in Gata, not an expression, and cannot appear in {where}; {hint}",
                Codes.AssignInExpr);
    }

    #endregion

    #region Annotations

    /// <summary>
    /// Parses zero or more leading annotations. Uses null-lazy allocation so the common
    /// path (no annotations) returns a static empty array without any heap allocation.
    /// </summary>
    private Annotation[] ParseAnnotations()
    {
        List<Annotation>? anns = null;
        while (true)
        {
            if (At(TK.AtIntrinsic)) { var t = Advance(); anns ??= []; anns.Add(new IntrinsicAnnotation(t.Value, t.Span)); }
            else if (At(TK.AtPreamble)) { var t = Advance(); anns ??= []; anns.Add(new PreambleAnnotation(t.Value, t.Span)); }
            else if (At(TK.AtKeep)) { var t = Advance(); anns ??= []; anns.Add(new KeepAnnotation(t.Span)); }
            else break;
        }
        return anns?.ToArray() ?? [];
    }

    /// <summary>
    /// Verifies that no invalid annotations were attached to a declaration that can't use them.
    /// @intrinsic and @preamble only bind to native blocks, native types, and functions.
    /// @keep is the one annotation a class or module can carry; everything else rejects all of them.
    /// </summary>
    private void RejectAnns(Annotation[] anns, string what, bool allowKeep = false)
    {
        foreach (var a in anns)
        {
            if (allowKeep && a is KeepAnnotation) continue;
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
        return new Program([.. items]) { GenericUses = [.. _gu] };
    }

    /// <summary>
    /// Parses a free function declaration. Handles optional modifiers, an optional return type
    /// using ParseOptionalReturnType, and an optional generic parameter list between the name
    /// and the opening paren.
    /// </summary>
    private FuncDecl ParseFreeFuncDecl(Annotation[] anns, int s)
    {
        var mods = ParseMods();
        bool isEntry = Try(TK.Entry);
        bool isThrow = Try(TK.Throws);
        string? ret = ParseOptionalReturnType();
        Expect(TK.Func);
        var name = Expect(TK.Ident).Value;
        var generics = ParseGenericParamList();
        Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
        if (At(TK.Arrow)) Fail($"'{name}': return type goes before 'func', not after the parameter list", Codes.BadDeclHeader);
        return new FuncDecl(mods, anns, ret, name, generics, parms, isEntry, isThrow, ParseMethodBody(), To(s));
    }

    /// <summary>
    /// Parses an optional generic parameter list like [T, U]. Returns an empty array if there
    /// is no leading bracket. Used by both class declarations and free function declarations.
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
        if (At(TK.NativeContent)) return new NativeBlock(ParseNativeBody(Advance().Value), To(s), anns);
        if (At(TK.NativeTypeDecl)) return ParseNativeType(anns, s);

        // The order of these checks matters. Class and module keywords are valid type names,
        // so they must be checked after the native decls but before the free function decl.
        if (At(TK.Enum)) { RejectAnns(anns, "an enum"); return ParseEnumDecl(anns, s); }
        if (At(TK.Union)) { RejectAnns(anns, "a union"); return ParseUnionDecl(anns, s); }
        if (At(TK.Class)) { RejectAnns(anns, "a class", allowKeep: true); return ParseClassDecl(anns, s); }
        if (At(TK.Module)) { RejectAnns(anns, "a module", allowKeep: true); return ParseModuleDecl(anns, s); }
        if (At(TK.Kernel)) { RejectAnns(anns, "kernel"); return ParseContextDecl("kernel"); }
        if (At(TK.User)) { RejectAnns(anns, "user"); return ParseContextDecl("user"); }
        if (At(TK.Process) || At(TK.Foreground) || At(TK.Background))
            { RejectAnns(anns, "a process"); return ParseProcessDeclTop(); }
        if (At(TK.AtExtern)) return ParseExternDecl(anns, s);
        return ParseFreeFuncDecl(anns, s);
    }

    /// <summary>
    /// Parses an import declaration. A string literal import is a filesystem path;
    /// a bare identifier is a module name.
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
    /// Wraps a raw native block string into a NativeBody, splitting on #kernel:/#user: markers.
    /// </summary>
    private static NativeBody ParseNativeBody(string raw)
    {
        var (kc, uc) = NativeC.Split(raw);
        return new NativeBody(kc, uc);
    }

    /// <summary>
    /// Parses a native type declaration. The lexer encodes the type name and body
    /// separated by \x1F in a single NativeTypeDecl token value.
    /// </summary>
    private NativeTypeDecl ParseNativeType(Annotation[] anns, int s)
    {
        string raw = Advance().Value;
        int sep = raw.IndexOf('\x1F');
        return new NativeTypeDecl(raw[..sep], raw[(sep + 1)..], To(s), anns);
    }

    /// <summary>
    /// Parses an @extern function pre-declaration. Tells the compiler a C function exists
    /// so it can be called from Gata without a Gata body.
    /// </summary>
    private ExternFuncDecl ParseExternDecl(Annotation[] anns, int s)
    {
        Advance(); // @extern
        Expect(TK.Func);
        var name = Expect(TK.Ident).Value;
        Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
        string? ret = null;
        if (At(TK.Arrow)) { Advance(); ret = ParseTypeSpec(); }
        Expect(TK.Semi);
        return new ExternFuncDecl(ret, name, parms, To(s), anns);
    }

    /// <summary>
    /// Parses a kernel or user block. The kind string is the keyword that opened it.
    /// </summary>
    private ContextDecl ParseContextDecl(string kind)
    {
        int s = Cur.Span.Start; Advance(); Expect(TK.LBrace);
        List<TopLevel> items = [];
        while (!At(TK.RBrace) && !At(TK.EOF)) items.Add(ParseContextItem());
        Expect(TK.RBrace);
        return new ContextDecl(kind, [.. items], To(s));
    }

    /// <summary>
    /// Dispatches to the correct parser for a single item inside a kernel or user block.
    /// Context blocks cannot be nested, so kernel and user keywords are hard errors here.
    /// </summary>
    private TopLevel ParseContextItem()
    {
        if (At(TK.Kernel) || At(TK.User)) Fail("contexts cannot be nested", Codes.InvalidNesting);
        int s = Cur.Span.Start;
        var anns = ParseAnnotations();
        if (At(TK.NativeContent)) return new NativeBlock(ParseNativeBody(Advance().Value), To(s), anns);
        if (At(TK.NativeTypeDecl)) return ParseNativeType(anns, s);
        if (At(TK.AtExtern)) return ParseExternDecl(anns, s);
        if (At(TK.Enum)) { RejectAnns(anns, "an enum"); return ParseEnumDecl(anns, s); }
        if (At(TK.Union)) { RejectAnns(anns, "a union"); return ParseUnionDecl(anns, s); }
        if (At(TK.Class)) { RejectAnns(anns, "a class", allowKeep: true); return ParseClassDecl(anns, s); }
        if (At(TK.Module)) { RejectAnns(anns, "a module", allowKeep: true); return ParseModuleDecl(anns, s); }
        if (At(TK.Process) || At(TK.Foreground) || At(TK.Background))
            { RejectAnns(anns, "a process"); return ParseProcessDeclTop(); }
        return ParseFreeFuncDecl(anns, s);
    }

    #endregion

    #region Class and module

    /// <summary>
    /// Parses a class declaration. The name is mangled with the generic parameter list
    /// so the Monomorphizer can match self-references. "class List[T]" becomes "List_T" in the AST.
    /// </summary>
    private ClassDecl ParseClassDecl(Annotation[] anns, int s)
    {
        Expect(TK.Class);
        int ns = Cur.Span.Start;
        var name = ParseSimpleTypeName();
        List<string> generics = [];
        if (At(TK.LBrack))
        {
            Advance();
            generics.Add(ExpectBareGenericParam());
            while (Try(TK.Comma)) generics.Add(ExpectBareGenericParam());
            Expect(TK.RBrack);

            // Register the instantiation site and mangle the name before parsing the body.
            var genericsArray = generics.ToArray();
            _gu.Add(new GenericUse(name, genericsArray, To(ns)));
            name = name + "_" + string.Join("_", genericsArray);
        }
        Expect(TK.LBrace);
        List<ClassMember> members = [];
        while (!At(TK.RBrace) && !At(TK.EOF)) members.Add(ParseClassMember());
        Expect(TK.RBrace);
        return new ClassDecl(name, [.. generics], anns, [.. members], To(s));
    }

    /// <summary>
    /// Reads a single bare identifier as a generic parameter name. Type arguments at use sites
    /// may nest (List[Map[K,V]]); class parameter declarations may not (class Foo[Bar[Baz]] is rejected).
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
    /// Parses an enum declaration. Members may carry explicit integer values; if absent the
    /// C compiler applies the usual increment rule. A trailing comma after the last member
    /// is a hard error.
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
        var name = Expect(TK.Ident).Value;
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
        return new UnionDecl(name, variants?.ToArray() ?? [], To(s), anns);
    }

    /// <summary>
    /// Parses a union variant's parenthesised field list. A trailing comma right
    /// before the closing paren is a hard error with a specific message, since the
    /// shared ParseParamList used for function parameters does not check for one.
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
    /// Parses a type name, collecting any generic arguments into the out parameter.
    /// Generic uses are registered in _gu for the Monomorphizer to consume.
    /// </summary>
    private string ParseTypeNameStr(out string[] generics)
    {
        EnterDepth();
        var name = ParseTypeNameStrInner(out generics);
        ExitDepth();
        return name;
    }

    private string ParseTypeNameStrInner(out string[] generics)
    {
        generics = [];
        int s = Cur.Span.Start;
        string name = ParseSimpleTypeName();
        if (At(TK.LBrack))
        {
            Advance();
            List<string> args = [ParseTypeArg()];
            while (Try(TK.Comma)) args.Add(ParseTypeArg());
            if (!At(TK.RBrack)) Fail($"invalid type argument in '{name}[...]', found {Found()}");
            Expect(TK.RBrack);
            var argsArray = args.ToArray();
            generics = argsArray;
            _gu.Add(new GenericUse(name, argsArray, To(s)));
            name = name + "_" + string.Join("_", argsArray);
        }
        return name;
    }

    /// <summary>
    /// Parses a single type argument inside a generic argument list. May itself be generic.
    /// </summary>
    private string ParseTypeArg()
    {
        return ParseTypeNameStr(out _);
    }

    /// <summary>
    /// Parses the base name of a type, like an identifier, the Process or Thread keywords
    /// (which are valid type names), or a primitive keyword.
    /// </summary>
    private string ParseSimpleTypeName()
    {
        if (At(TK.Ident) || At(TK.Process) || At(TK.Thread)) return Advance().Value;
        if (IsPrim(Cur.Kind)) return PrimName(Advance());
        Fail($"expected a type name, found {Found()}");
        return "";
    }

    /// <summary>
    /// Parses a full type specifier. Fixed-array prefix [N], function pointer type, plain type name,
    /// and optional pointer suffixes.
    /// </summary>
    private string ParseTypeSpec()
    {
        EnterDepth();
        var spec = ParseTypeSpecInner();
        ExitDepth();
        return spec;
    }

    private string ParseTypeSpecInner()
    {
        // [N]elem, brackets come before the element type.
        if (At(TK.LBrack) && Peek().Kind == TK.IntLit && Peek(2).Kind == TK.RBrack)
        {
            Advance();
            string n = Advance().Value;
            Expect(TK.RBrack);
            return $"[{n}]{ParseTypeSpec()}";
        }
        if (At(TK.Func)) return ParseFuncTypeSpec();
        string name = ParseTypeNameStr(out _);
        while (AtP("*")) { Advance(); name += "*"; }
        return name;
    }

    /// <summary>
    /// Parses a function pointer type specifier. Encoded as the string "func(T1,T2)->R"
    /// so TypeResolver can re-parse it later without needing a dedicated AST node.
    /// </summary>
    private string ParseFuncTypeSpec()
    {
        Expect(TK.Func);
        Expect(TK.LParen);
        List<string> ps = [];
        if (!At(TK.RParen))
        {
            ps.Add(ParseTypeSpec());
            while (Try(TK.Comma)) ps.Add(ParseTypeSpec());
        }
        Expect(TK.RParen);
        Expect(TK.Arrow);
        string ret = ParseTypeSpec();
        string spec = $"func({string.Join(",", ps)})->{ret}";
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
    /// Maps a primitive token to its canonical type name string.
    /// TPrim tokens carry their own value (eg. "uint64"), so those fall through to the default.
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
    /// Parses a single class member: a fields block, operator overload, method, or field declaration.
    /// </summary>
    private ClassMember ParseClassMember()
    {
        int s = Cur.Span.Start;
        if (At(TK.Class) || At(TK.Module)) Fail("classes and modules cannot be nested", Codes.InvalidNesting);
        if (At(TK.Kernel) || At(TK.User)) Fail("context blocks cannot appear inside a class", Codes.InvalidNesting);

        // fields { } block is a raw C struct fields injected verbatim into the emitted typedef.
        if (At(TK.Fields)) return new FieldsBlock(ParseNativeBody(Advance().Value), To(s));

        if (At(TK.Operator))
        {
            Advance(); Expect(TK.Func);
            string op = ParseOperatorSymbol();
            Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
            string? ret = null;
            if (At(TK.Arrow)) { Advance(); ret = ParseTypeSpec(); }
            return new OperatorDecl(op, parms, ret, ParseMethodBody(), To(s));
        }

        var anns = ParseAnnotations();
        var mods = ParseMods();
        bool isEntry = Try(TK.Entry);
        bool isThrow = Try(TK.Throws);

        // If we reach here, it must be either a method or a field. Fields don't support
        // entry, throws, or annotations.
        if (LooksLikeMethod())
        {
            if (isEntry) Fail("'entry' has no meaning on a class method", Codes.BadDeclHeader);
            string? ret = ParseOptionalReturnType();
            Expect(TK.Func);
            var name = Expect(TK.Ident).Value;
            Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
            return new MethodDecl(mods, anns, ret, name, parms, isEntry, isThrow, ParseMethodBody(), To(s));
        }

        // Field. Entry, throws, annotations, and static are all meaningless here.
        if (isEntry) Fail("'entry' has no meaning on a field", Codes.BadDeclHeader);
        if (isThrow) Fail("'throws' has no meaning on a field", Codes.BadDeclHeader);
        if (anns.Length > 0) Fail("annotations have no effect on a field", Codes.BadAnnotation);
        if (mods.HasFlag(Modifiers.Static)) Fail("'static' has no meaning on a field", Codes.BadDeclHeader);

        string? ftype = (At(TK.Ident) || At(TK.Process) || At(TK.Thread)
            || IsPrim(Cur.Kind) || At(TK.LBrack) || At(TK.Func)) ? ParseTypeSpec() : null;
        var fname = Expect(TK.Ident).Value;
        Expr? init = Try(TK.Eq) ? ParseExpr() : null;
        Expect(TK.Semi);
        return new FieldDecl(mods, ftype, fname, To(s), init);
    }

    /// <summary>
    /// Parses an operator symbol for an operator overload declaration.
    /// Handles arithmetic, comparison, bitwise, and indexer operators.
    /// </summary>
    private string ParseOperatorSymbol()
    {
        if (AtP("+") || AtP("-") || AtP("*") || AtP("/") || AtP("<") || AtP(">")) return Advance().Value;
        if (At(TK.EqEq) || At(TK.NotEq) || At(TK.LtEq) || At(TK.GtEq)) return Advance().Value;
        if (AtP("&") || AtP("|") || AtP("^") || At(TK.Shl) || At(TK.Shr)) return Advance().Value;
        // operator func [](K) -> V for getter, operator func []=(K, V) for setter.
        if (At(TK.LBrack)) { Advance(); Expect(TK.RBrack); return Try(TK.Eq) ? "[]=" : "[]"; }
        Fail($"expected an operator symbol, found {Found()}");
        return "+";
    }

    /// <summary>
    /// Returns true if the current position looks like the start of a method declaration.
    /// 'func Name' with no return type is a method; 'func(' starts a func-pointer type (a field).
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
    private string? ParseOptionalReturnType()
    {
        return At(TK.Func) && Peek().Kind == TK.Ident ? null : ParseTypeSpec();
    }

    /// <summary>
    /// Parses a method body. Either a native C block or a Gata statement block.
    /// </summary>
    private MethodBody ParseMethodBody()
    {
        if (At(TK.NativeContent)) return new NativeMethodBody(ParseNativeBody(Advance().Value));
        return new BlockBody(ParseBlock());
    }

    /// <summary>
    /// Parses zero or more access/storage modifiers into a single flags value.
    /// </summary>
    private Modifiers ParseMods()
    {
        var mods = Modifiers.None;
        while (true)
        {
            if (At(TK.Static)) { mods |= Modifiers.Static; Advance(); }
            else if (At(TK.Public)) { mods |= Modifiers.Public; Advance(); }
            else if (At(TK.Private)) { mods |= Modifiers.Private; Advance(); }
            else break;
        }
        return mods;
    }

    #endregion

    #region Process and thread

    /// <summary>
    /// Parses a process declaration. Accepts an optional leading foreground/background keyword
    /// and an optional trailing colon-prefixed mode, but rejects both at once.
    /// </summary>
    private ProcessDecl ParseProcessDeclTop()
    {
        int s = Cur.Span.Start;
        string mode = "foreground";
        bool modeExplicit = false;
        if (At(TK.Foreground)) { mode = "foreground"; modeExplicit = true; Advance(); }
        else if (At(TK.Background)) { mode = "background"; modeExplicit = true; Advance(); }
        Expect(TK.Process);
        var name = Expect(TK.Ident).Value;
        if (Try(TK.Colon))
        {
            // Two spellings of the mode is an error; one of them has to go.
            if (modeExplicit) Fail($"'{name}': mode specified twice", Codes.BadDeclHeader);
            if (At(TK.Foreground)) { mode = "foreground"; Advance(); }
            else if (At(TK.Background)) { mode = "background"; Advance(); }
            else Fail($"expected 'foreground' or 'background' after ':', found {Found()}", Codes.BadDeclHeader);
        }
        Expect(TK.LBrace);
        List<ThreadDecl> threads = [];
        while (!At(TK.RBrace) && !At(TK.EOF)) threads.Add(ParseThreadDecl());
        Expect(TK.RBrace);
        return new ProcessDecl(name, mode, [.. threads], To(s));
    }

    /// <summary>
    /// Parses a thread declaration inside a process body. A foreground or background keyword
    /// before 'thread' is syntactically accepted and captured in Mode; the type resolver
    /// rejects it as G043, since threads don't have their own deployment mode, only the
    /// process does.
    /// </summary>
    private ThreadDecl ParseThreadDecl()
    {
        int s = Cur.Span.Start;
        string? mode = null;
        if (At(TK.Foreground)) { mode = "foreground"; Advance(); }
        else if (At(TK.Background)) { mode = "background"; Advance(); }
        if (!At(TK.Thread)) Fail("a process body may only contain 'thread' declarations", Codes.BadDeclHeader);
        Advance();
        var name = Expect(TK.Ident).Value;
        Expect(TK.LBrace);
        var entry = ParseThreadEntry();
        if (!At(TK.RBrace)) Fail("a thread body must contain a single 'entry func' and nothing else", Codes.BadDeclHeader);
        Expect(TK.RBrace);
        return new ThreadDecl(name, mode, entry, To(s));
    }

    /// <summary>
    /// Parses the entry function of a thread. Threads are pure topology, not scopes, so nested
    /// threads and helper functions defined inside the body are hard errors. A thread entry is
    /// invoked through a fixed void(*)(void*) ABI so return types and access modifiers are rejected.
    /// </summary>
    private EntryFuncDecl ParseThreadEntry()
    {
        int s = Cur.Span.Start;
        if (At(TK.Thread)) Fail("threads cannot be nested", Codes.InvalidNesting);
        var mods = ParseMods();
        if (!Try(TK.Entry)) Fail("a thread body must contain a single 'entry func'", Codes.BadDeclHeader);
        string? ret = At(TK.Func) && Peek().Kind == TK.Ident ? null : ParseTypeSpec();
        Expect(TK.Func);
        if (At(TK.Ident)) Advance(); // entry name is documentation only; the thread is what names it
        Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
        if (ret != null) Fail("a thread entry has no return value; remove the return type", Codes.BadDeclHeader);
        if (mods != Modifiers.None) Fail("access/storage modifiers have no meaning on a thread entry", Codes.BadDeclHeader);
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
        string type = ParseTypeSpec();
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
        if (At(TK.NativeContent)) return new NativeStmt(ParseNativeBody(Advance().Value), To(s));
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

        // Throw and debug statements are not expressions, so they must be handled here instead of in ParseExprOrAssign.
        if (At(TK.Throw)) {
            Advance();
            Expect(TK.Semi);
            return new ThrowStmt(To(s));
        }
        if (At(TK.Debug)) {
            Advance();
            if (!At(TK.StrLit)) Fail("'debug' takes a string literal, e.g. debug \"message\";");
            var raw = Advance().Value;
            Expect(TK.Semi);
            return new DebugStmt(raw, To(s));
        }

        // Panic is a statement, not an expression, so it must be handled here instead of in ParseExprOrAssign.
        if (At(TK.Panic)) {
            Advance();
            if (!At(TK.StrLit)) Fail("'panic' takes a string literal, e.g. panic \"message\";");
            var raw = Advance().Value;
            Expect(TK.Semi);
            return new PanicStmt(raw, To(s));
        }
        if (LooksLikeMissingLet())
            Fail(At(TK.Ident)
                ? $"expected a statement -- missing 'let'? (e.g. 'let {Cur.Value} ...')"
                : "expected a statement -- missing 'let'?", Codes.MissingLet);
        return ParseExprOrAssign(s);
    }

    /// <summary>
    /// Parses a let declaration. The type is optional; if the next two tokens are both
    /// valid type-name starts, the first is taken as the declared type.
    /// </summary>
    private LetStmt ParseLetStmt(int s)
    {
        Expect(TK.Let);
        string? type = null;
        if (IsPrim(Cur.Kind) || At(TK.LBrack) || At(TK.Func) || At(TK.Process) || At(TK.Thread)
            || (At(TK.Ident) && (Peek().Kind == TK.Ident || Peek().Kind == TK.LBrack
                || (Peek().Kind == TK.Punct && Peek().Value == "*"))))
            type = ParseTypeSpec();
        string name = Expect(TK.Ident).Value;
        Expr? init = Try(TK.Eq) ? ParseExpr() : null;
        Expect(TK.Semi);
        return new LetStmt(type, name, init, To(s));
    }

    /// <summary>
    /// Returns the index just past a balanced "[...]" run starting at token offset n, or -1 if
    /// it never closes before EOF. Used by SkipTypeSpec to jump over generic argument lists.
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
    /// Lookahead mirror of ParseTypeSpec. Returns the index just past the type starting at
    /// offset n (Peek(0) = Cur), or -1 if offset n is not the start of a valid type.
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
        else if (Peek(n).Kind == TK.Ident || Peek(n).Kind == TK.Process || Peek(n).Kind == TK.Thread)
        {
            n++;
            if (Peek(n).Kind == TK.LBrack) { n = SkipBrackets(n); if (n < 0) return -1; }
        }
        else return -1;
        while (Peek(n).Kind == TK.Punct && Peek(n).Value == "*") n++;
        return n;
    }

    /// <summary>
    /// Returns true if the current position looks like a type spec immediately followed by
    /// an identifier, which is always a missing 'let' and never valid expression syntax.
    /// Pure lookahead; never consumes tokens.
    /// </summary>
    private bool LooksLikeMissingLet()
    {
        if (!At(TK.Ident) && !At(TK.Process) && !At(TK.Thread) && !At(TK.LBrack)) return false;
        int n = SkipTypeSpec(0);
        return n >= 0 && Peek(n).Kind == TK.Ident;
    }

    /// <summary>
    /// Returns true if the current position looks like a type specifier followed by an
    /// identifier, meaning the let statement has an explicit type annotation.
    /// </summary>
    private bool LooksLikeTypeAndIdent()
    {
        if (IsPrim(Cur.Kind)) return true;
        if (At(TK.Func)) return true;
        if (At(TK.LBrack) && Peek().Kind == TK.IntLit && Peek(2).Kind == TK.RBrack) return true;
        if (!At(TK.Ident) && !At(TK.Process) && !At(TK.Thread)) return false;
        return Peek().Kind == TK.Ident
            || Peek().Kind == TK.LBrack
            || (Peek().Kind == TK.Punct && Peek().Value == "*");
    }

    /// <summary>
    /// Parses a let declaration without consuming its trailing semicolon. Used in for-loop
    /// init clauses where the semicolon belongs to the for syntax, not the let.
    /// </summary>
    private LetStmt ParseLetNoSemi()
    {
        int s = Cur.Span.Start;
        Expect(TK.Let);
        string? type = LooksLikeTypeAndIdent() ? ParseTypeSpec() : null;
        string name = Expect(TK.Ident).Value;
        Expr? init = Try(TK.Eq) ? ParseExpr() : null;
        return new LetStmt(type, name, init, To(s));
    }

    /// <summary>
    /// Parses an if/else statement. The then and else branches are full statements, so a
    /// bare block, a single statement, or a nested if are all valid without extra rules.
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
    /// Parses a for loop. Disambiguates between 'for x in col { }' (ForInStmt, no parens) and
    /// the C-style 'for (init; cond; step) { }' (ForStmt) by peeking for the 'in' keyword.
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
    /// Parses an unsafe block. Pointer operations inside are permitted; the type checker
    /// rejects them everywhere else.
    /// </summary>
    private UnsafeBlock ParseUnsafeBlock(int s)
    {
        Expect(TK.Unsafe);
        var block = ParseBlock();
        return new UnsafeBlock(block.Stmts, To(s));
    }

    /// <summary>
    /// Parses a defer statement. The deferred action is a single statement that runs on
    /// every exit from the enclosing block, in LIFO order with other defers.
    /// </summary>
    private DeferStmt ParseDeferStmt(int s)
    {
        Expect(TK.Defer);
        return new DeferStmt(ParseStmt(), To(s));
    }

    /// <summary>
    /// Parses an expression statement or assignment. After parsing the left-hand expression,
    /// any assignment operator promotes the result to an AssignStmt; otherwise it's an ExprStmt.
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
    /// Parses '&&' chains.
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
    /// Parses bitwise '&' chains.
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
    /// Parses '<<' and '>>' chains.
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
    /// Parses prefix unary operators. '&' and '*' are only legal inside unsafe blocks but
    /// are accepted here; the type checker enforces the restriction.
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
            else if (At(TK.LBrack)) { Advance(); var idx = ParseExpr(); Expect(TK.RBrack); expr = new IndexExpr(expr, idx, To(s)); }
            else if (At(TK.LParen)) { Advance(); var args = ParseArgList(); Expect(TK.RParen); expr = new CallExpr(expr, args, To(s)); }
            else break;
        }
        return expr;
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
    /// Parses a single call argument. 'ref' is only valid at the call-argument level, not as
    /// a general unary prefix, so it is handled here rather than in ParseUnary.
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
    /// Dispatches to the correct primary form: literal, ident, sizeof, default, new,
    /// array literal, grouped expression, primitive cast, or interpolated string.
    /// </summary>
    private Expr ParsePrimaryInner()
    {
        int s = Cur.Span.Start;

        // Literals and identifiers are all single-token forms.
        if (At(TK.IntLit)) { var t = Advance(); return new IntLitExpr(t.Value, t.Span); }
        if (At(TK.FloatLit)) { var t = Advance(); return new FloatLitExpr(t.Value, t.Span); }
        if (At(TK.BoolLit)) { var t = Advance(); return new BoolLitExpr(t.Value, t.Span); }
        if (At(TK.CharLit)) { var t = Advance(); return new CharLitExpr(int.Parse(t.Value), t.Span); }
        if (At(TK.StrLit)) { var t = Advance(); return new StrLitExpr(t.Value, t.Span); }
        if (At(TK.Null)) { Advance(); return new NullExpr(To(s)); }
        if (At(TK.InterpStrStart)) return ParseInterpStr(s);

        // sizeof(Type) and default(Type) are special forms that take a type specifier in parentheses.
        if (At(TK.Sizeof))
        {
            Advance(); Expect(TK.LParen);
            string t = ParseTypeSpec();
            Expect(TK.RParen);
            return new SizeofExpr(t, To(s));
        }
        if (At(TK.Default))
        {
            Advance(); Expect(TK.LParen);
            string t = ParseTypeSpec();
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

        // Parenthesised expression or primitive cast. The type specifier is unambiguous because
        // it must be a primitive keyword or identifier, and the cast is unambiguous because
        // it must be followed by a unary expression. The parser does not allow user-defined
        // types in parentheses because that would be ambiguous with a grouped expression.
        if (At(TK.LParen))
        {
            Advance();
            // (PrimType) expr is an unambiguous C-style cast. User-type casts use 'as'.
            if (IsPrim(Cur.Kind))
            {
                string targetType = ParseTypeSpec();
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
    /// Parses an interpolated string. The lexer emits InterpStrStart, then alternating StrLit
    /// and Punct("{") ... Punct("}") pairs for embedded expressions, then InterpStrEnd.
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
    /// Parses a 'new' expression. After the type spec, an optional '(' constructor arg list
    /// and an optional collection initializer (either '{' or '[' delimited) may each follow,
    /// independently of one another - 'new Foo(args) { init }' is legal. A bare 'new Type'
    /// with neither is valid for fixed-size array types like '[5]char' that carry their size
    /// in the type string.
    /// </summary>
    private NewExpr ParseNewExpr(int s)
    {
        Expect(TK.New);
        string type = ParseTypeSpec();
        Expr[] args = [];
        if (At(TK.LParen))
        {
            Advance(); args = ParseArgList(); Expect(TK.RParen);
        }
        if (At(TK.LBrace)) return new NewExpr(type, args, ParseCollectionInit(TK.LBrace, TK.RBrace), To(s));
        if (At(TK.LBrack)) return new NewExpr(type, args, ParseCollectionInit(TK.LBrack, TK.RBrack), To(s));
        return new NewExpr(type, args, [], To(s));
    }

    /// <summary>
    /// Parses a delimited, comma-separated element list for a 'new' collection initializer.
    /// Returns an empty array for an empty delimiter pair.
    /// </summary>
    private Expr[] ParseCollectionInit(TK open, TK close)
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
    /// Parses a switch statement. Each 'case' arm carries one or more comma-separated labels
    /// and a block body. An optional 'default' arm catches all unmatched values.
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
    /// Parses a match statement. Each 'case' arm names a union variant and optionally binds
    /// its payload fields. An optional 'default' arm catches unmatched variants.
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
