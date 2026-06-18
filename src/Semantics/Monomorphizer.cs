namespace Appa;

using System.Text.RegularExpressions;

// Pre-resolution pass that stamps out concrete class bodies for each generic
// instantiation used in the source. A `class List[T] { ... }` is a template;
// this pass produces List_int, List_String, etc. from the recorded GenericUses.
// Only used instantiations are emitted; uninstantiated templates are dropped.
/// <summary>
/// Rewrites generic class templates into concrete instantiated classes before
/// symbol collection and type resolution run.
/// </summary>
sealed class Monomorphizer(DiagnosticBag diag)
{
    sealed record Template(ClassDecl Decl, string[] Params, string BaseName);

    /// <summary>
    /// Scans GenericUses in all programs, stamps a concrete class for each distinct
    /// instantiation, and rewrites each program's Items to replace templates with instances.
    /// </summary>
    public void Process(List<(Program prog, string file)> programs)
    {
        var templates = new Dictionary<string, Template>();
        var tmplNames = new HashSet<string>();
        foreach (var (prog, _) in programs)
            foreach (var item in prog.Items)
                if (item is ClassDecl cd && cd.GenericParams.Length > 0)
                {
                    string baseName = BaseNameOf(cd);
                    templates[baseName] = new Template(cd, cd.GenericParams, baseName);
                    tmplNames.Add(cd.Name);
                }

        if (templates.Count == 0) return;

        var requests = new Dictionary<string, (string Base, string[] Args, TextSpan Span, string File)>();
        foreach (var (prog, path) in programs)
            foreach (var use in prog.GenericUses)
            {
                if (!templates.ContainsKey(use.Base)) continue;
                string mangled = use.Base + "_" + string.Join("_", use.Args);
                if (!tmplNames.Contains(mangled))
                    requests.TryAdd(mangled, (use.Base, use.Args, use.Span, path));
            }

        var instancesByBase = new Dictionary<string, List<ClassDecl>>();
        foreach (var (mangled, (baseName, args, span, file)) in requests)
        {
            var tmpl = templates[baseName];
            if (tmpl.Params.Length != args.Length)
            {
                diag.Error(Codes.WrongArgCount, file, span,
                    $"generic '{baseName}' expects {tmpl.Params.Length} type argument(s) " +
                    $"({string.Join(", ", tmpl.Params)}), got {args.Length} ({string.Join(", ", args)})");
                Mangler.RegisterGenericInstance(mangled, baseName, [..args]);
                continue;
            }
            if (Array.Exists(args, a => a.Trim() == "void"))
            {
                diag.Error(Codes.UndefinedType, file, span,
                    $"'void' is not a valid type argument to '{baseName}'");
                Mangler.RegisterGenericInstance(mangled, baseName, [..args]);
                continue;
            }
            var (concrete, _) = Instantiate(tmpl, args, mangled);
            Mangler.RegisterGenericInstance(mangled, baseName, [..args]);
            if (!instancesByBase.TryGetValue(baseName, out var list))
                instancesByBase[baseName] = list = [];
            list.Add(concrete);
        }

        for (int i = 0; i < programs.Count; i++)
        {
            var (prog, file) = programs[i];
            bool changed = false;
            var rewritten = new List<TopLevel>(prog.Items.Length);
            foreach (var item in prog.Items)
            {
                if (item is ClassDecl cd && cd.GenericParams.Length > 0)
                {
                    changed = true;
                    if (instancesByBase.TryGetValue(BaseNameOf(cd), out var instances))
                        rewritten.AddRange(instances);
                }
                else rewritten.Add(item);
            }
            if (changed) programs[i] = (prog with { Items = [..rewritten] }, file);
        }
    }

    /// <summary>
    /// Returns the base name of a generic class by stripping the trailing type-parameter suffix.
    /// </summary>
    static string BaseNameOf(ClassDecl cd)
    {
        string suffix = "_" + string.Join("_", cd.GenericParams);
        return cd.Name.EndsWith(suffix) ? cd.Name[..^suffix.Length] : cd.Name;
    }

    /// <summary>
    /// Clones a generic class template with concrete type arguments, substituting type
    /// parameters throughout signatures, native fields, and statement bodies.
    /// </summary>
    (ClassDecl Concrete, Dictionary<string, string> Binds) Instantiate(
        Template tmpl, string[] args, string mangled)
    {
        var gataMap = new Dictionary<string, string>();
        var cMap = new Dictionary<string, string>();
        for (int i = 0; i < tmpl.Params.Length; i++)
        {
            string p = tmpl.Params[i];
            gataMap[p] = args[i];
            cMap[p] = CTypeOf(args[i]);
        }

        var members = new ClassMember[tmpl.Decl.Members.Length];
        for (int i = 0; i < members.Length; i++)
            members[i] = SubMember(tmpl.Decl.Members[i], gataMap, cMap);
        var concrete = new ClassDecl(mangled, [], tmpl.Decl.Annotations, members, tmpl.Decl.Span);
        return (concrete, gataMap);
    }

    /// <summary>
    /// Substitutes type parameters in a single class member (field, method, or operator).
    /// </summary>
    ClassMember SubMember(ClassMember m, Dictionary<string, string> g, Dictionary<string, string> c)
    {
        ClassMember r = m switch
        {
            FieldsBlock fb => new FieldsBlock(SubNative(fb.Body, c), fb.Span),
            FieldDecl fd => new FieldDecl(fd.Modifiers, SubType(fd.Type, g), fd.Name, fd.Span,
                fd.Init is null ? null : SubExpr(fd.Init, g)),
            MethodDecl md => new MethodDecl(md.Modifiers, md.Annotations, SubType(md.ReturnType, g),
                md.Name, [..SubParams(md.Params, g)], md.IsEntry, md.Throws,
                SubBody(md.Body, g, c), md.Span),
            OperatorDecl od => new OperatorDecl(od.Op, [..SubParams(od.Params, g)],
                SubType(od.ReturnType, g), SubBody(od.Body, g, c), od.Span),
            _ => m
        };
        return r with { Span = m.Span };
    }

    /// <summary>
    /// Substitutes type parameters in a parameter list and returns the rewritten array.
    /// </summary>
    internal static Param[] SubParams(Param[] ps, Dictionary<string, string> g)
    {
        var result = new Param[ps.Length];
        for (int i = 0; i < ps.Length; i++)
            result[i] = new Param(SubType(ps[i].Type, g)!, ps[i].Name, ps[i].Span, ps[i].IsRef);
        return result;
    }

    /// <summary>
    /// Substitutes type parameters in a method body, dispatching to the native or block form.
    /// </summary>
    internal static MethodBody SubBody(
        MethodBody b, Dictionary<string, string> g, Dictionary<string, string> c) => b switch
    {
        NativeMethodBody nmb => new NativeMethodBody(SubNative(nmb.Native, c)),
        BlockBody bb => new BlockBody(SubBlock(bb.Block, g)),
        _ => b
    };

    static NativeBody SubNative(NativeBody nb, Dictionary<string, string> c) =>
        new(SubWords(nb.KernelC, c), SubWords(nb.UserC, c));

    /// <summary>
    /// Substitutes type parameters in a Gata type string, handling both word-boundary
    /// replacement and the mangled generic-suffix pattern (e.g. List_T becomes List_int).
    /// </summary>
    internal static string? SubType(string? t, Dictionary<string, string> g)
    {
        if (t is null) return null;
        string text = SubWords(t, g);
        foreach (var (from, to) in g)
            text = Regex.Replace(text, $@"_{Regex.Escape(from)}(?=_|$)", "_" + to.Replace("$", "$$"));
        return text;
    }

    static string SubWords(string text, Dictionary<string, string> map)
    {
        foreach (var (from, to) in map)
            text = Regex.Replace(text, $@"\b{Regex.Escape(from)}\b", to.Replace("$", "$$"));
        return text;
    }

    /// <summary>
    /// Returns the C-type spelling for a Gata type argument, used when substituting
    /// type parameters inside native struct fields and native bodies.
    /// </summary>
    internal static string CTypeOf(string t)
    {
        if (t.EndsWith('*')) return t;
        if (PrimTypes.IsPrim(t)) return PrimTypes.ToC(t);
        if (t == "String") return $"{Mangler.Class("String")}*";
        if (t is "Process" or "Thread") return "void*";
        return $"{Mangler.Class(t)}*";
    }

    static Block SubBlock(Block b, Dictionary<string, string> g) =>
        new([..b.Stmts.Select(s => SubStmt(s, g))], b.Span);

    static Stmt SubStmt(Stmt s, Dictionary<string, string> g)
    {
        Stmt r = s switch
        {
            Block b => SubBlock(b, g),
            LetStmt ls => new LetStmt(SubType(ls.Type, g), ls.Name,
                ls.Init is null ? null : SubExpr(ls.Init, g), ls.Span),
            AssignStmt a => new AssignStmt(SubExpr(a.Target, g), a.Op, SubExpr(a.Value, g), a.Span),
            ExprStmt es => new ExprStmt(SubExpr(es.E, g), es.Span),
            IfStmt ifs => new IfStmt(SubExpr(ifs.Cond, g), SubStmt(ifs.Then, g),
                ifs.Else is null ? null : SubStmt(ifs.Else, g), ifs.Span),
            WhileStmt ws => new WhileStmt(SubExpr(ws.Cond, g), SubStmt(ws.Body, g), ws.Span),
            ForStmt fs => new ForStmt(
                fs.Init is null ? null : SubStmt(fs.Init, g),
                fs.Cond is null ? null : SubExpr(fs.Cond, g),
                fs.Step is null ? null : SubExpr(fs.Step, g),
                SubBlock(fs.Body, g), fs.Span),
            ForInStmt fi => new ForInStmt(fi.Var, SubExpr(fi.Collection, g),
                SubBlock(fi.Body, g), fi.Span),
            ReturnStmt rs => new ReturnStmt(rs.Value is null ? null : SubExpr(rs.Value, g), rs.Span),
            TryCatchStmt tc => new TryCatchStmt(SubBlock(tc.Try, g), SubBlock(tc.Catch, g), tc.Span),
            UnsafeBlock ub => new UnsafeBlock([..ub.Stmts.Select(x => SubStmt(x, g))], ub.Span),
            DeferStmt dfr => new DeferStmt(SubStmt(dfr.Action, g), dfr.Span),
            SwitchStmt sw => new SwitchStmt(SubExpr(sw.Scrutinee, g),
                [..sw.Cases.Select(c => new SwitchCase(
                    [..c.Labels.Select(l => SubExpr(l, g))],
                    SubBlock(c.Body, g), c.Span))],
                sw.Default is null ? null : SubBlock(sw.Default, g), sw.Span),
            MatchStmt ms => new MatchStmt(SubExpr(ms.Scrutinee, g),
                [..ms.Cases.Select(c => c with { Body = SubBlock(c.Body, g) })],
                ms.Default is null ? null : SubBlock(ms.Default, g), ms.Span),
            _ => s   // NativeStmt, BreakStmt, ContinueStmt, ThrowStmt, DebugStmt, PanicStmt
        };
        return r with { Span = s.Span };
    }

    static Expr SubExpr(Expr e, Dictionary<string, string> g)
    {
        Expr r = e switch
        {
            CastExpr ce => new CastExpr(SubType(ce.TargetType, g)!, SubExpr(ce.Value, g), ce.Span),
            TernaryExpr te => new TernaryExpr(SubExpr(te.Cond, g), SubExpr(te.Then, g),
                SubExpr(te.Else, g), te.Span),
            NewExpr ne => new NewExpr(SubType(ne.Type, g)!,
                [..ne.Args.Select(a => SubExpr(a, g))],
                [..ne.CollectionInit.Select(a => SubExpr(a, g))], ne.Span),
            ArrayLitExpr al => new ArrayLitExpr([..al.Elems.Select(a => SubExpr(a, g))], al.Span),
            CallExpr cx => new CallExpr(SubExpr(cx.Callee, g),
                [..cx.Args.Select(a => SubExpr(a, g))], cx.Span),
            MemberAccessExpr ma => new MemberAccessExpr(SubExpr(ma.Object, g), ma.Member, ma.Span),
            IndexExpr ix => new IndexExpr(SubExpr(ix.Object, g), SubExpr(ix.Index, g), ix.Span),
            BinExpr be => new BinExpr(be.Op, SubExpr(be.Left, g), SubExpr(be.Right, g), be.Span),
            UnaryExpr un => new UnaryExpr(un.Op, SubExpr(un.Operand, g), un.Span),
            PostfixExpr pf => new PostfixExpr(pf.Op, SubExpr(pf.Operand, g), pf.Span),
            AddrOfExpr ao => new AddrOfExpr(SubExpr(ao.Target, g), ao.Span),
            DerefExpr dr => new DerefExpr(SubExpr(dr.Ptr, g), dr.Span),
            RefArgExpr ra => new RefArgExpr(SubExpr(ra.Target, g), ra.Span),
            InterpStrExpr ip => new InterpStrExpr([..ip.Parts.Select(p => SubExpr(p, g))], ip.Span),
            SizeofExpr so => new SizeofExpr(SubType(so.TypeName, g)!, so.Span),
            DefaultExpr de => new DefaultExpr(SubType(de.TypeName, g)!, de.Span),
            _ => e   // literals, IdentExpr, NullExpr
        };
        return r with { Span = e.Span };
    }

    #region Generic function helpers

    /// <summary>
    /// Tries to bind a type parameter inferred from one argument position.
    /// Returns false only on a conflicting re-bind; a concrete (non-parameter) type returns true.
    /// </summary>
    internal static bool UnifyParam(string paramType, IrType argType,
        string[] gparams, Dictionary<string, string> binds)
    {
        string pt = paramType.Trim();
        if (Array.IndexOf(gparams, pt) >= 0) return Bind(pt, GataNameOf(argType), binds);
        if (pt.EndsWith('*'))
        {
            string inner = pt[..^1];
            if (Array.IndexOf(gparams, inner) >= 0 && argType is IrPtrType ptr)
                return Bind(inner, GataNameOf(ptr.Inner), binds);
        }
        // Single-type-argument generic container: declared param `List_T` against a
        // concrete `List_int` argument - bind T=int. Lets generic free functions take
        // List[T]/Stack[T]/etc. parameters and have T inferred from the caller's type.
        if (argType is IrClassRef cr)
            foreach (var g in gparams)
            {
                string suffix = "_" + g;
                if (!pt.EndsWith(suffix)) continue;
                string ptBase = pt[..^suffix.Length];
                int us = cr.ClassName.IndexOf('_');
                if (us < 0) continue;
                if (cr.ClassName[..us] == ptBase) return Bind(g, cr.ClassName[(us + 1)..], binds);
            }
        return true;
    }

    static bool Bind(string param, string name, Dictionary<string, string> binds)
    {
        if (binds.TryGetValue(param, out var prev)) return prev == name;
        binds[param] = name;
        return true;
    }

    /// <summary>
    /// Returns the Gata type spelling for a resolved IR type, used as the binding value
    /// when inferring type arguments from call-site argument types.
    /// </summary>
    internal static string GataNameOf(IrType t) => t switch
    {
        IrPrimType p => p.CName,
        IrClassRef c => c.ClassName,
        IrPtrType pt => GataNameOf(pt.Inner) + "*",
        IrVoidType => "void",
        _ => t.ToCType()
    };

    /// <summary>
    /// Reduces a type name to a valid C-identifier fragment for use in mangled generic names.
    /// Pointer stars become "_p"; all other non-identifier characters are dropped.
    /// </summary>
    internal static string SanitizeTypeName(string t)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in t.Trim())
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            else if (ch == '*') sb.Append("_p");
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    #endregion
}
