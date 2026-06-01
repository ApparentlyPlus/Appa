namespace Appa;

/// <summary>
/// Recursive-descent parser that converts a flat token stream into an untyped AST.
/// One instance per file. Call ParseProgram() once and discard.
/// </summary>
sealed class Parser(IReadOnlyList<Token> tokens)
{
    // Materialize to an array upfront so every indexed access is O(1) with no virtual dispatch.
    private readonly Token[] _tokens = tokens as Token[] ?? Enumerable.ToArray(tokens);
    
    // current position in the token array
    int _pp;

    // end offset of the last consumed token, used by To()  
    int _pe;

    // Recursion depth guard. Without it, (((((...))))) stack-overflows instead of failing cleanly.
    int _depth;
    const int MaxDepth = 200;

    // Increments the recursion depth counter and throws if it exceeds MaxDepth. Call ExitDepth() in a finally block.
    void EnterDepth() { if (++_depth > MaxDepth) Fail("nested too deeply"); }
    void ExitDepth() => _depth--;

    // Generic instantiation sites collected during parsing, consumed by the Monomorphizer.
    readonly List<GenericUse> _gu = [];

    #region Core stream helpers

    // _pp is always clamped to [0, _tokens.Length - 1] by Advance(), so no bounds check needed.
    Token Cur => _tokens[_pp];
    Token Peek(int n = 1) => (_pp + n) < _tokens.Length ? _tokens[_pp + n] : _tokens[^1];

    /// <summary>
    /// Consumes the current token, updates _pe for span construction, and advances _pp.
    /// </summary>
    Token Advance()
    {
        var t = Cur;
        _pe = t.Span.End;
        if (_pp < _tokens.Length - 1) _pp++;
        return t;
    }

    // Builds a TextSpan from a saved start offset to the end of the last consumed token.
    TextSpan To(int start) => new(start, Math.Max(0, _pe - start));

    /// <summary>
    /// Consumes a token of the expected kind, or throws if the current token doesn't match.
    /// </summary>
    Token Expect(TK k)
    {
        if (Cur.Kind != k) Fail($"expected {k}");
        return Advance();
    }

    // Helpers for common token checks.
    bool At(TK k) => Cur.Kind == k;
    bool Try(TK k) { if (At(k)) { Advance(); return true; } return false; }

    // AtP is only for single char operator tokens that remain as TK.Punct, like + - * / % & | ^ < > ! ~
    bool AtP(string v) => Cur.Kind == TK.Punct && Cur.Value == v;

    // Throws a ParseException with the given message at the current token's span.
    void Fail(string m) => throw new ParseException(Cur.Span, m);

    #endregion

    #region Annotations

    /// <summary>
    /// Parses zero or more leading annotations. Uses null-lazy allocation so the common
    /// path (no annotations) returns a static empty array without any heap allocation.
    /// </summary>
    Annotation[] ParseAnnotations()
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
    void RejectAnns(Annotation[] anns, string what, bool allowKeep = false)
    {
        if (anns.Length == 0) return;
        if (allowKeep)
        {
            // Count only the annotations that aren't @keep, since @keep is valid here.
            int bad = 0;
            for (int i = 0; i < anns.Length; i++)
                if (anns[i] is not KeepAnnotation) bad++;
            if (bad > 0) Fail($"annotations have no effect on {what}");
        }
        else
        {
            Fail($"annotations have no effect on {what}");
        }
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
    /// using the same single-pass disambiguation as class members, and an optional generic
    /// parameter list between the name and the opening paren.
    /// </summary>
    FuncDecl ParseFreeFuncDecl(Annotation[] anns, int s)
    {
        var mods = ParseMods();
        bool isEntry = Try(TK.Entry);
        bool isThrow = Try(TK.Throws);

        string? ret;
        if (At(TK.Func) && Peek().Kind == TK.Ident)
            ret = null;
        else
            ret = (At(TK.Ident) || At(TK.Process) || At(TK.Thread)
                || IsPrim(Cur.Kind) || At(TK.LBrack) || At(TK.Func))
                ? ParseTypeSpec()
                : null;

        Expect(TK.Func);
        var name = Expect(TK.Ident).Value;
        var generics = ParseGenericParamList();
        Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
        return new FuncDecl(mods, anns, ret, name, generics, parms, isEntry, isThrow, ParseMethodBody(), To(s));
    }

    /// <summary>
    /// Parses an optional generic parameter list like [T, U]. Returns an empty array if there
    /// is no leading bracket. Used by both class declarations and free function declarations.
    /// </summary>
    string[] ParseGenericParamList()
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
    TopLevel ParseTopLevel()
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
    ImportDecl ParseImport()
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
    /// Wraps a raw native block string into a NativeBody.
    /// </summary>
    static NativeBody ParseNativeBody(string raw) => new(raw, "");

    /// <summary>
    /// Parses a native type declaration. The lexer encodes the type name and body
    /// separated by \x1F in a single NativeTypeDecl token value.
    /// </summary>
    NativeTypeDecl ParseNativeType(Annotation[] anns, int s)
    {
        string raw = Advance().Value;
        int sep = raw.IndexOf('\x1F');
        return new NativeTypeDecl(raw[..sep], raw[(sep + 1)..], To(s), anns);
    }

    /// <summary>
    /// Parses an @extern function pre-declaration. Tells the compiler a C function exists
    /// so it can be called from Gata without a Gata body.
    /// </summary>
    ExternFuncDecl ParseExternDecl(Annotation[] anns, int s)
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
    ContextDecl ParseContextDecl(string kind)
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
    TopLevel ParseContextItem()
    {
        if (At(TK.Kernel) || At(TK.User)) Fail("contexts cannot be nested");
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
    ClassDecl ParseClassDecl(Annotation[] anns, int s)
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
    string ExpectBareGenericParam()
    {
        if (!At(TK.Ident)) Fail("generic parameter must be a plain name");
        var tok = Advance().Value;
        if (At(TK.LBrack)) Fail($"generic parameter '{tok}' cannot itself be generic");
        return tok;
    }

    /// <summary>
    /// Parses a module declaration. Modules are classes where all members are implicitly static.
    /// </summary>
    ClassDecl ParseModuleDecl(Annotation[] anns, int s)
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
    /// C compiler applies the usual increment rule. We accept a trailing comma before the brace.
    /// </summary>
    EnumDecl ParseEnumDecl(Annotation[] anns, int s)
    {
        Expect(TK.Enum);
        var name = Expect(TK.Ident).Value;
        Expect(TK.LBrace);
        List<EnumMember>? members = null;
        while (!At(TK.RBrace) && !At(TK.EOF))
        {
            int ms = Cur.Span.Start;
            var mname = Expect(TK.Ident).Value;
            Expr? value = Try(TK.Eq) ? ParseExpr() : null;
            members ??= [];
            members.Add(new EnumMember(mname, value, To(ms)));
            if (!Try(TK.Comma)) break;
        }
        Expect(TK.RBrace);
        return new EnumDecl(name, members?.ToArray() ?? [], To(s), anns);
    }

    /// <summary>
    /// Parses a union declaration. Each variant is a name followed by an optional parenthesised
    /// field list. A variant with no parens carries no payload.
    /// </summary>
    UnionDecl ParseUnionDecl(Annotation[] anns, int s)
    {
        Expect(TK.Union);
        var name = Expect(TK.Ident).Value;
        Expect(TK.LBrace);
        List<UnionVariant>? variants = null;
        while (!At(TK.RBrace) && !At(TK.EOF))
        {
            int vs = Cur.Span.Start;
            var vname = Expect(TK.Ident).Value;
            Param[] fields = [];
            if (At(TK.LParen)) { Advance(); fields = ParseParamList(); Expect(TK.RParen); }
            variants ??= [];
            variants.Add(new UnionVariant(vname, fields, To(vs)));
            if (!Try(TK.Comma)) break;
        }
        Expect(TK.RBrace);
        return new UnionDecl(name, variants?.ToArray() ?? [], To(s), anns);
    }

    #endregion

    #region Type specs

    /// <summary>
    /// Parses a type name, collecting any generic arguments into the out parameter.
    /// Generic uses are registered in _gu for the Monomorphizer to consume.
    /// </summary>
    string ParseTypeNameStr(out string[] generics)
    {
        EnterDepth();
        try
        {
            generics = [];
            int s = Cur.Span.Start;
            string name = ParseSimpleTypeName();
            if (At(TK.LBrack))
            {
                Advance();
                List<string> args = [ParseTypeArg()];
                while (Try(TK.Comma)) args.Add(ParseTypeArg());
                if (!At(TK.RBrack)) Fail($"invalid type argument in '{name}[...]'");
                Expect(TK.RBrack);
                var argsArray = args.ToArray();
                generics = argsArray;
                _gu.Add(new GenericUse(name, argsArray, To(s)));
                name = name + "_" + string.Join("_", argsArray);
            }
            return name;
        }
        finally { ExitDepth(); }
    }

    // Parses a single type argument inside a generic argument list. May itself be generic.
    string ParseTypeArg() => ParseTypeNameStr(out _);

    /// <summary>
    /// Parses the base name of a type, like an identifier, the Process or Thread keywords
    /// (which are valid type names), or a primitive keyword.
    /// </summary>
    string ParseSimpleTypeName()
    {
        if (At(TK.Ident) || At(TK.Process) || At(TK.Thread)) return Advance().Value;
        if (IsPrim(Cur.Kind)) return PrimName(Advance());
        Fail("expected type name");
        return "";
    }

    /// <summary>
    /// Parses a full type specifier. Fixed-array prefix [N], function pointer type, plain type name,
    /// and optional pointer suffixes.
    /// </summary>
    string ParseTypeSpec()
    {
        EnterDepth();
        try
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
        finally { ExitDepth(); }
    }

    /// <summary>
    /// Parses a function pointer type specifier. Encoded as the string "func(T1,T2)->R"
    /// so TypeResolver can re-parse it later without needing a dedicated AST node.
    /// </summary>
    string ParseFuncTypeSpec()
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
        if (AtP("*")) Fail("pointer to a function type is not supported; use the function type directly");
        return spec;
    }

    // Returns true if the token kind is one of the primitive type keywords.
    static bool IsPrim(TK k) => k is TK.TBool or TK.TInt or TK.TChar or TK.TFloat
        or TK.TDouble or TK.TShort or TK.TVoid or TK.TPrim;

    /// <summary>
    /// Maps a primitive token to its canonical type name string.
    /// TPrim tokens carry their own value (eg. "uint64"), so those fall through to the default.
    /// </summary>
    static string PrimName(Token t) => t.Kind switch
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

    #endregion

    #region Class members

    /// <summary>
    /// Parses a single class member: a fields block, operator overload, method, or field declaration.
    /// </summary>
    ClassMember ParseClassMember()
    {
        int s = Cur.Span.Start;
        if (At(TK.Class) || At(TK.Module)) Fail("classes and modules cannot be nested");
        if (At(TK.Kernel) || At(TK.User)) Fail("context blocks cannot appear inside a class");

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

        // parse the type spec once, then check what follows
        // If it's a func keyword, it's a method; otherwise it's a field.
        bool isMethod;
        string? typeStr;
        if (At(TK.Func) && Peek().Kind == TK.Ident)
        {
            typeStr = null;
            isMethod = true;
        }
        else
        {
            typeStr = (At(TK.Ident) || At(TK.Process) || At(TK.Thread)
                || IsPrim(Cur.Kind) || At(TK.LBrack) || At(TK.Func))
                ? ParseTypeSpec()
                : null;
            isMethod = At(TK.Func);
        }

        // Method. Entry, throws, and annotations are all valid here.
        if (isMethod)
        {
            if (isEntry) Fail("'entry' has no meaning on a class method");
            Expect(TK.Func);
            var name = Expect(TK.Ident).Value;
            Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
            return new MethodDecl(mods, anns, typeStr, name, parms, isEntry, isThrow, ParseMethodBody(), To(s));
        }

        // Field. Entry, throws, annotations, and static are all meaningless here.
        if (isEntry) Fail("'entry' has no meaning on a field");
        if (isThrow) Fail("'throws' has no meaning on a field");
        if (anns.Length > 0) Fail("annotations have no effect on a field");
        bool hasStatic = false;
        for (int i = 0; i < mods.Length; i++)
            if (mods[i] == "static") { hasStatic = true; break; }
        if (hasStatic) Fail("'static' has no meaning on a field");

        var fname = Expect(TK.Ident).Value;
        Expr? init = Try(TK.Eq) ? ParseExpr() : null;
        Expect(TK.Semi);
        return new FieldDecl(mods, typeStr, fname, To(s), init);
    }

    /// <summary>
    /// Parses an operator symbol for an operator overload declaration.
    /// Handles arithmetic, comparison, bitwise, and indexer operators.
    /// </summary>
    string ParseOperatorSymbol()
    {
        if (AtP("+") || AtP("-") || AtP("*") || AtP("/") || AtP("<") || AtP(">")) return Advance().Value;
        if (At(TK.EqEq) || At(TK.NotEq) || At(TK.LtEq) || At(TK.GtEq)) return Advance().Value;
        if (AtP("&") || AtP("|") || AtP("^") || At(TK.Shl) || At(TK.Shr)) return Advance().Value;
        // operator func [](K) -> V for getter, operator func []=(K, V) for setter.
        if (At(TK.LBrack)) { Advance(); Expect(TK.RBrack); return Try(TK.Eq) ? "[]=" : "[]"; }
        Fail("expected operator symbol");
        return "+";
    }

    // Parses a method body. Either a native C block or a Gata statement block.
    MethodBody ParseMethodBody()
    {
        if (At(TK.NativeContent)) return new NativeMethodBody(ParseNativeBody(Advance().Value));
        return new BlockBody(ParseBlock());
    }

    /// <summary>
    /// Parses zero or more access/storage modifiers. Uses null-lazy allocation so the common
    /// path (no modifiers) returns a static empty array without any heap allocation.
    /// </summary>
    string[] ParseMods()
    {
        List<string>? mods = null;
        while (true)
        {
            if (At(TK.Static)) { mods ??= new List<string>(); mods.Add("static"); Advance(); }
            else if (At(TK.Public)) { mods ??= new List<string>(); mods.Add("public"); Advance(); }
            else if (At(TK.Private)) { mods ??= new List<string>(); mods.Add("private"); Advance(); }
            else break;
        }
        return mods?.ToArray() ?? [];
    }

    #endregion

    #region Process and thread

    /// <summary>
    /// Parses a process declaration. Accepts an optional leading foreground/background keyword
    /// and an optional trailing colon-prefixed mode, but rejects both at once.
    /// </summary>
    ProcessDecl ParseProcessDeclTop()
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
            if (modeExplicit) Fail($"'{name}': mode specified twice");
            if (At(TK.Foreground)) { mode = "foreground"; Advance(); }
            else if (At(TK.Background)) { mode = "background"; Advance(); }
        }
        Expect(TK.LBrace);
        List<ThreadDecl> threads = [];
        while (!At(TK.RBrace) && !At(TK.EOF)) threads.Add(ParseThreadDecl());
        Expect(TK.RBrace);
        return new ProcessDecl(name, mode, threads.ToArray(), To(s));
    }

    /// <summary>
    /// Parses a thread declaration inside a process body. A foreground or background keyword
    /// before 'thread' is G043. Threads don't have their own deployment mode, only the process does.
    /// </summary>
    ThreadDecl ParseThreadDecl()
    {
        int s = Cur.Span.Start;
        if (At(TK.Foreground) || At(TK.Background)) Fail("thread mode is set by the process, not the thread (G043)");
        if (!At(TK.Thread)) Fail("a process body may only contain 'thread' declarations");
        Advance();
        var name = Expect(TK.Ident).Value;
        Expect(TK.LBrace);
        var entry = ParseThreadEntry();
        if (!At(TK.RBrace)) Fail("a thread body must contain a single 'entry func' and nothing else");
        Expect(TK.RBrace);
        return new ThreadDecl(name, entry, To(s));
    }

    /// <summary>
    /// Parses the entry function of a thread. Threads are pure topology, not scopes, so nested
    /// threads and helper functions defined inside the body are hard errors. A thread entry is
    /// invoked through a fixed void(*)(void*) ABI so return types and access modifiers are rejected.
    /// </summary>
    EntryFuncDecl ParseThreadEntry()
    {
        int s = Cur.Span.Start;
        if (At(TK.Thread)) Fail("threads cannot be nested");
        var mods = ParseMods();
        if (!Try(TK.Entry)) Fail("a thread body must contain a single 'entry func'");
        string? ret = At(TK.Func) && Peek().Kind == TK.Ident ? null : ParseTypeSpec();
        Expect(TK.Func);
        if (At(TK.Ident)) Advance(); // entry name is documentation only; the thread is what names it
        Expect(TK.LParen); var parms = ParseParamList(); Expect(TK.RParen);
        if (ret != null) Fail("a thread entry has no return value; remove the return type");
        if (mods.Length > 0) Fail($"'{mods[0]}' has no meaning on a thread entry");
        return new EntryFuncDecl(mods, ret, parms, ParseBlock(), To(s));
    }

    #endregion

    #region Parameters

    /// <summary>
    /// Parses a comma-separated parameter list between the surrounding parens (already consumed).
    /// Returns a static empty array for an empty parameter list to avoid an allocation.
    /// </summary>
    Param[] ParseParamList()
    {
        if (At(TK.RParen)) return [];
        List<Param> ps = [ParseParam()];
        while (Try(TK.Comma)) ps.Add(ParseParam());
        return [.. ps];
    }

    /// <summary>
    /// Parses a single parameter: an optional ref keyword, a type specifier, and a name.
    /// </summary>
    Param ParseParam()
    {
        int s = Cur.Span.Start;
        bool isRef = Try(TK.Ref);
        string type = ParseTypeSpec();
        string name = Expect(TK.Ident).Value;
        return new Param(type, name, To(s), isRef);
    }

    #endregion

    #region Stubs

public Block ParseBlock() => throw new NotImplementedException();
    public Expr ParseExpr() => throw new NotImplementedException();

    #endregion
}
