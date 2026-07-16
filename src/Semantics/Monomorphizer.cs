namespace Appa;


/// <summary>
/// Rewrites generic class templates into concrete instantiated classes before
/// symbol collection and type resolution run.
/// </summary>
internal sealed class Monomorphizer(DiagnosticBag diag)
{
    private sealed record Template(ClassDecl Decl, string[] Params, string BaseName);

    internal sealed class SubstitutionContext(Dictionary<string, TypeSpec> g, Dictionary<string, string>? c)
    {
        public readonly Dictionary<string, TypeSpec> SpecMap = g;
        public readonly Dictionary<string, string> CMap = c ?? [];

        /// <summary>
        /// Substitutes type parameters in raw native C text, replacing whole words that match
        /// a type parameter with its concrete C type. Native bodies are the one place where
        /// substitution is genuinely textual. Everything else is rewritten structurally.
        /// </summary>
        public string SubWords(string text)
        {
            bool containsParam = false;
            foreach (var key in CMap.Keys)
            {
                if (text.Contains(key, StringComparison.Ordinal)) { containsParam = true; break; }
            }
            if (!containsParam) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            int idx = 0;
            while (idx < text.Length)
            {
                char c = text[idx];
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    int start = idx;
                    while (idx < text.Length && (char.IsLetterOrDigit(text[idx]) || text[idx] == '_'))
                    {
                        idx++;
                    }
                    string word = text[start..idx];
                    sb.Append(CMap.TryGetValue(word, out var replacement) ? replacement : word);
                }
                else
                {
                    sb.Append(c);
                    idx++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Structurally substitutes type parameters in a type spec tree. Returns the same
        /// reference when nothing changed so callers can cheaply detect no-ops.
        /// </summary>
        public TypeSpec? SubType(TypeSpec? t)
        {
            switch (t)
            {
                case null:
                    return null;
                case NamedSpec { Args.Length: 0 } n:
                    return SpecMap.TryGetValue(n.Name, out var bound) ? bound : t;
                case NamedSpec n:
                {
                    NamedSpec[]? newArgs = null;
                    for (int i = 0; i < n.Args.Length; i++)
                    {
                        var na = SubArg(n.Args[i]);
                        if (!ReferenceEquals(na, n.Args[i]) && newArgs == null)
                        {
                            newArgs = new NamedSpec[n.Args.Length];
                            Array.Copy(n.Args, newArgs, i);
                        }
                        if (newArgs != null) newArgs[i] = na;
                    }
                    return newArgs == null ? t : n with { Args = newArgs };
                }
                case PtrSpec p:
                {
                    var inner = SubType(p.Inner)!;
                    return ReferenceEquals(inner, p.Inner) ? t : p with { Inner = inner };
                }
                case ArraySpec a:
                {
                    var elem = SubType(a.Elem)!;
                    return ReferenceEquals(elem, a.Elem) ? t : a with { Elem = elem };
                }
                case FuncSpec f:
                {
                    TypeSpec[]? newPs = null;
                    for (int i = 0; i < f.Params.Length; i++)
                    {
                        var np = SubType(f.Params[i])!;
                        if (!ReferenceEquals(np, f.Params[i]) && newPs == null)
                        {
                            newPs = new TypeSpec[f.Params.Length];
                            Array.Copy(f.Params, newPs, i);
                        }
                        if (newPs != null) newPs[i] = np;
                    }
                    var nr = SubType(f.Ret)!;
                    if (newPs == null && ReferenceEquals(nr, f.Ret)) return t;
                    return f with { Params = newPs ?? f.Params, Ret = nr };
                }
                default:
                    return t;
            }
        }

        /// <summary>
        /// Substitutes one generic argument slot. Argument slots hold named types only, so a
        /// binding to a non named spec (like a pointer bound by generic-function inference)
        /// folds to its sanitized mangled fragment to stay a valid slot.
        /// </summary>
        private NamedSpec SubArg(NamedSpec a)
        {
            return SubType(a) switch
            {
                NamedSpec ns => ns,
                var sub => new NamedSpec(SanitizeTypeName(sub!.ToSpecString()), a.Span)
            };
        }
    }

    /// <summary>
    /// Scans GenericUses in all programs, stamps a concrete class for each distinct
    /// instantiation breadth-first, and rewrites each program's Items to replace templates
    /// with instances. Deferred inner uses (template bodies that reference other templates
    /// via their own type parameters) are replayed once their owning instantiation is done.
    /// </summary>
    public void Process(List<(string path, Program prog)> programs)
    {
        var templates = new Dictionary<string, Template>();
        var tmplNames = new HashSet<string>();
        foreach (var (_, prog) in programs)
            foreach (var item in prog.Items)
                if (item is ClassDecl cd && cd.GenericParams.Length > 0)
                {
                    string baseName = BaseNameOf(cd);
                    templates[baseName] = new Template(cd, cd.GenericParams, baseName);
                    tmplNames.Add(cd.Name);
                }

        if (templates.Count == 0) return;

        var directUses = new List<(GenericUse Use, string File)>();
        var deferredByOwner = new Dictionary<string, List<(GenericUse Use, string File)>>();
        foreach (var (path, prog) in programs)
        {
            var ownersInFile = templates.Values.Where(t => prog.Items.Contains(t.Decl)).ToList();
            // Generic *function* templates also own uses whose args are their own type
            // parameters (e.g. 'Pair[T, T]' in 'func GetFirst[T](Pair[T, T] p)'). Those are
            // not instantiable here - ResolveGenericCall stamps the function against concrete
            // types during resolution - so they must not become bogus Pair_T_T requests.
            var funcOwners = new List<FuncDecl>();
            void CollectFuncOwners(TopLevel[] items)
            {
                foreach (var item in items)
                {
                    if (item is FuncDecl { GenericParams.Length: > 0 } fdt) funcOwners.Add(fdt);
                    else if (item is ContextDecl cdt) CollectFuncOwners(cdt.Items);
                }
            }
            CollectFuncOwners(prog.Items);
            foreach (var use in prog.GenericUses)
            {
                if (funcOwners.Any(fd =>
                        use.Span.Start >= fd.Span.Start && use.Span.End <= fd.Span.End &&
                        use.Args.Any(a => Array.IndexOf(fd.GenericParams, a) >= 0)))
                    continue;
                var owner = ownersInFile.FirstOrDefault(t =>
                    t.BaseName != use.Base &&
                    use.Span.Start >= t.Decl.Span.Start && use.Span.End <= t.Decl.Span.End &&
                    use.Args.All(a => Array.IndexOf(t.Params, a) >= 0));
                if (owner != null)
                {
                    if (!deferredByOwner.TryGetValue(owner.BaseName, out var l))
                        deferredByOwner[owner.BaseName] = l = [];
                    l.Add((use, path));
                }
                else directUses.Add((use, path));
            }
        }

        var requests = new Dictionary<string, (string Base, string[] Args, TextSpan Span, string File)>();
        bool AddRequest(string b, string[] a, TextSpan sp, string file)
        {
            if (!templates.ContainsKey(b)) return false;
            string mangled = b + "_" + string.Join("_", a);
            if (tmplNames.Contains(mangled)) return false;
            return requests.TryAdd(mangled, (b, a, sp, file));
        }
        foreach (var (use, file) in directUses) AddRequest(use.Base, use.Args, use.Span, file);

        var instancesByBase = new Dictionary<string, List<ClassDecl>>();
        var pending = new Queue<string>(requests.Keys);
        var done = new HashSet<string>();
        while (pending.Count > 0)
        {
            string mangled = pending.Dequeue();
            if (!done.Add(mangled)) continue;
            var (baseName, args, span, file) = requests[mangled];
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
            var (concrete, binds) = Instantiate(tmpl, args, mangled);
            Mangler.RegisterGenericInstance(mangled, baseName, [..args]);
            if (!instancesByBase.TryGetValue(baseName, out var list))
                instancesByBase[baseName] = list = [];
            list.Add(concrete);

            if (deferredByOwner.TryGetValue(baseName, out var deferred))
                foreach (var (du, dfile) in deferred)
                {
                    var concreteArgs = du.Args.Select(a => binds.GetValueOrDefault(a, a)).ToArray();
                    if (AddRequest(du.Base, concreteArgs, du.Span, dfile))
                        pending.Enqueue(du.Base + "_" + string.Join("_", concreteArgs));
                }
        }

        for (int i = 0; i < programs.Count; i++)
        {
            var (path, prog) = programs[i];
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
            if (changed) programs[i] = (path, prog with { Items = [..rewritten] });
        }
    }

    /// <summary>
    /// Returns the base name of a generic class by stripping the trailing type-parameter suffix.
    /// </summary>
    private static string BaseNameOf(ClassDecl cd)
    {
        string suffix = "_" + string.Join("_", cd.GenericParams);
        return cd.Name.EndsWith(suffix) ? cd.Name[..^suffix.Length] : cd.Name;
    }

    /// <summary>
    /// Clones a generic class template with concrete type arguments, substituting type
    /// parameters throughout signatures, native fields, and statement bodies.
    /// </summary>
    private (ClassDecl Concrete, Dictionary<string, string> Binds) Instantiate(
        Template tmpl, string[] args, string mangled)
    {
        var gataMap = new Dictionary<string, string>(tmpl.Params.Length);
        var specMap = new Dictionary<string, TypeSpec>(tmpl.Params.Length);
        var cMap = new Dictionary<string, string>(tmpl.Params.Length);
        for (int i = 0; i < tmpl.Params.Length; i++)
        {
            string p = tmpl.Params[i];
            gataMap[p] = args[i];
            var spec = new NamedSpec(args[i]);
            specMap[p] = spec;
            cMap[p] = CTypeOf(spec);
        }
        var ctx = new SubstitutionContext(specMap, cMap);

        var members = new ClassMember[tmpl.Decl.Members.Length];
        bool changed = false;
        for (int i = 0; i < members.Length; i++)
        {
            var m = tmpl.Decl.Members[i];
            var sm = SubMember(m, ctx);
            members[i] = sm;
            if (!ReferenceEquals(m, sm)) changed = true;
        }

        var concrete = changed
            ? new ClassDecl(mangled, [], tmpl.Decl.Annotations, members, tmpl.Decl.Span)
            : tmpl.Decl with { Name = mangled };
        return (concrete, gataMap);
    }

    /// <summary>
    /// Substitutes type parameters in a single class member (field, method, or operator).
    /// </summary>
    private static ClassMember SubMember(ClassMember m, SubstitutionContext ctx)
    {
        ClassMember r = m switch
        {
            FieldsBlock fb => new FieldsBlock(SubNative(fb.Body, ctx), fb.Span),
            FieldDecl fd => SubFieldDecl(fd, ctx),
            MethodDecl md => SubMethodDecl(md, ctx),
            OperatorDecl od => SubOperatorDecl(od, ctx),
            _ => m
        };
        return r with { Span = m.Span };
    }

    /// <summary>
    /// Substitutes type parameters in a field declaration, including its type and initializer expression.
    /// </summary>
    private static FieldDecl SubFieldDecl(FieldDecl fd, SubstitutionContext ctx)
    {
        var newType = ctx.SubType(fd.Type);
        var newInit = fd.Init is null ? null : SubExpr(fd.Init, ctx);
        if (ReferenceEquals(newType, fd.Type) && ReferenceEquals(newInit, fd.Init))
            return fd;
        return new FieldDecl(fd.Modifiers, newType, fd.Name, fd.Span, newInit);
    }

    /// <summary>
    /// Substitutes type parameters in a method declaration, including its return type, parameters, and body.
    /// </summary>
    private static MethodDecl SubMethodDecl(MethodDecl md, SubstitutionContext ctx)
    {
        var newRet = ctx.SubType(md.ReturnType);
        var newParams = SubParams(md.Params, ctx);
        var newBody = SubBody(md.Body, ctx);
        if (ReferenceEquals(newRet, md.ReturnType) && ReferenceEquals(newParams, md.Params) && ReferenceEquals(newBody, md.Body))
            return md;
        return new MethodDecl(md.Modifiers, md.Annotations, newRet, md.Name, newParams, md.IsEntry, md.Throws, newBody, md.Span);
    }

    /// <summary>
    /// Substitutes type parameters in an operator declaration, including its return type, parameters, and body.
    /// </summary>
    private static OperatorDecl SubOperatorDecl(OperatorDecl od, SubstitutionContext ctx)
    {
        var newParams = SubParams(od.Params, ctx);
        var newRet = ctx.SubType(od.ReturnType);
        var newBody = SubBody(od.Body, ctx);
        if (ReferenceEquals(newParams, od.Params) && ReferenceEquals(newRet, od.ReturnType) && ReferenceEquals(newBody, od.Body))
            return od;
        return new OperatorDecl(od.Modifiers, od.Op, newParams, newRet, newBody, od.Span);
    }

    /// <summary>
    /// Substitutes type parameters in a parameter list and returns the rewritten array.
    /// </summary>
    internal static Param[] SubParams(Param[] ps, Dictionary<string, TypeSpec> g)
    {
        var ctx = new SubstitutionContext(g, null);
        return SubParams(ps, ctx);
    }

    internal static Param[] SubParams(Param[] ps, SubstitutionContext ctx)
    {
        Param[]? newParams = null;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var newType = ctx.SubType(p.Type);
            if (!ReferenceEquals(newType, p.Type))
            {
                if (newParams == null)
                {
                    newParams = new Param[ps.Length];
                    Array.Copy(ps, newParams, i);
                }
            }
            newParams?[i] = new Param(newType!, p.Name, p.Span, p.IsRef);
        }
        return newParams ?? ps;
    }

    /// <summary>
    /// Substitutes type parameters in a method body, dispatching to the native or block form.
    /// </summary>
    internal static MethodBody SubBody(
        MethodBody b, Dictionary<string, TypeSpec> g, Dictionary<string, string> c)
    {
        var ctx = new SubstitutionContext(g, c);
        return SubBody(b, ctx);
    }

    /// <summary>
    /// Substitutes type parameters in a method body, dispatching to the native or block form.
    /// </summary>
    internal static MethodBody SubBody(MethodBody b, SubstitutionContext ctx)
    {
        return b switch
        {
            NativeMethodBody nmb => new NativeMethodBody(SubNative(nmb.Native, ctx)),
            BlockBody bb => new BlockBody(SubBlock(bb.Block, ctx)),
            _ => b
        };
    }

    /// <summary>
    /// Substitutes type parameters in a native method body, replacing type parameters in the kernel and user code strings.
    /// </summary>
    private static NativeBody SubNative(NativeBody nb, SubstitutionContext ctx)
    {
        var newKernel = ctx.SubWords(nb.KernelC);
        var newUser = ctx.SubWords(nb.UserC);
        if (ReferenceEquals(newKernel, nb.KernelC) && ReferenceEquals(newUser, nb.UserC))
            return nb;
        return new NativeBody(newKernel, newUser);
    }

    /// <summary>
    /// Structurally substitutes type parameters in a type spec using the given bindings.
    /// </summary>
    internal static TypeSpec? SubType(TypeSpec? t, Dictionary<string, TypeSpec> g)
    {
        var ctx = new SubstitutionContext(g, null);
        return ctx.SubType(t);
    }

    /// <summary>
    /// Returns the C-type spelling for a Gata type argument, used when substituting
    /// type parameters inside native struct fields and native bodies.
    /// </summary>
    internal static string CTypeOf(TypeSpec t)
    {
        switch (t)
        {
            case PtrSpec p:
                return CTypeOf(p.Inner) + "*";
            case NamedSpec n:
            {
                string name = n.Mangled;
                if (name == "void") return "void";
                if (PrimTypes.IsPrim(name)) return PrimTypes.ToC(name);
                if (name == BuiltinTypes.String) return $"{Mangler.Class(BuiltinTypes.String)}*";
                if (name is BuiltinTypes.Process or BuiltinTypes.Thread) return "void*";
                return $"{Mangler.Class(name)}*";
            }
            default:
                // Array/function specs cannot appear as generic type arguments.
                return t.ToSpecString();
        }
    }

    /// <summary>
    /// Substitutes type parameters in a block of statements, returning a new block if any substitutions occurred.
    /// </summary>
    private static Block SubBlock(Block b, SubstitutionContext ctx)
    {
        Stmt[]? newStmts = null;
        for (int i = 0; i < b.Stmts.Length; i++)
        {
            var s = b.Stmts[i];
            var ns = SubStmt(s, ctx);
            if (!ReferenceEquals(s, ns))
            {
                if (newStmts == null)
                {
                    newStmts = new Stmt[b.Stmts.Length];
                    Array.Copy(b.Stmts, newStmts, i);
                }
            }
            newStmts?[i] = ns;
        }
        if (newStmts == null) return b;
        return new Block(newStmts, b.Span);
    }

    /// <summary>
    /// Substitutes type parameters in a single statement, recursively processing any nested statements or expressions.
    /// </summary>
    private static Stmt SubStmt(Stmt s, SubstitutionContext ctx)
    {
        switch (s)
        {
            case Block b:
                var nb = SubBlock(b, ctx);
                if (ReferenceEquals(b, nb)) return s;
                return nb with { Span = s.Span };

            case LetStmt ls:
                var newType = ctx.SubType(ls.Type);
                var newInit = ls.Init is null ? null : SubExpr(ls.Init, ctx);
                if (ReferenceEquals(newType, ls.Type) && ReferenceEquals(newInit, ls.Init))
                    return s;
                return new LetStmt(newType, ls.Name, newInit, ls.Span) { Span = s.Span };

            case AssignStmt a:
                var newTarget = SubExpr(a.Target, ctx);
                var newValue = SubExpr(a.Value, ctx);
                if (ReferenceEquals(newTarget, a.Target) && ReferenceEquals(newValue, a.Value))
                    return s;
                return new AssignStmt(newTarget, a.Op, newValue, a.Span) { Span = s.Span };

            case ExprStmt es:
                var newE = SubExpr(es.E, ctx);
                if (ReferenceEquals(newE, es.E)) return s;
                return new ExprStmt(newE, es.Span) { Span = s.Span };

            case IfStmt ifs:
                var newCond = SubExpr(ifs.Cond, ctx);
                var newThen = SubStmt(ifs.Then, ctx);
                var newElse = ifs.Else is null ? null : SubStmt(ifs.Else, ctx);
                if (ReferenceEquals(newCond, ifs.Cond) && ReferenceEquals(newThen, ifs.Then) && ReferenceEquals(newElse, ifs.Else))
                    return s;
                return new IfStmt(newCond, newThen, newElse, ifs.Span) { Span = s.Span };

            case WhileStmt ws:
                var newWCond = SubExpr(ws.Cond, ctx);
                var newWBody = SubStmt(ws.Body, ctx);
                if (ReferenceEquals(newWCond, ws.Cond) && ReferenceEquals(newWBody, ws.Body))
                    return s;
                return new WhileStmt(newWCond, newWBody, ws.Span) { Span = s.Span };

            case ForStmt fs:
                var newFInit = fs.Init is null ? null : SubStmt(fs.Init, ctx);
                var newFCond = fs.Cond is null ? null : SubExpr(fs.Cond, ctx);
                var newFStep = fs.Step is null ? null : SubStmt(fs.Step, ctx);
                var newFBody = SubBlock(fs.Body, ctx);
                if (ReferenceEquals(newFInit, fs.Init) && ReferenceEquals(newFCond, fs.Cond) &&
                    ReferenceEquals(newFStep, fs.Step) && ReferenceEquals(newFBody, fs.Body))
                    return s;
                return new ForStmt(newFInit, newFCond, newFStep, newFBody, fs.Span) { Span = s.Span };

            case ForInStmt fi:
                var newFiColl = SubExpr(fi.Collection, ctx);
                var newFiBody = SubBlock(fi.Body, ctx);
                if (ReferenceEquals(newFiColl, fi.Collection) && ReferenceEquals(newFiBody, fi.Body))
                    return s;
                return new ForInStmt(fi.Var, newFiColl, newFiBody, fi.Span) { Span = s.Span };

            case ReturnStmt rs:
                var newRv = rs.Value is null ? null : SubExpr(rs.Value, ctx);
                if (ReferenceEquals(newRv, rs.Value)) return s;
                return new ReturnStmt(newRv, rs.Span) { Span = s.Span };

            case TryCatchStmt tc:
                var newTry = SubBlock(tc.Try, ctx);
                var newCatch = SubBlock(tc.Catch, ctx);
                if (ReferenceEquals(newTry, tc.Try) && ReferenceEquals(newCatch, tc.Catch))
                    return s;
                return new TryCatchStmt(newTry, newCatch, tc.Span) { Span = s.Span };

            case DeferStmt dfr:
                var newDAction = SubStmt(dfr.Action, ctx);
                if (ReferenceEquals(newDAction, dfr.Action)) return s;
                return new DeferStmt(newDAction, dfr.Span) { Span = s.Span };

            case UnsafeBlock ub:
                Stmt[]? newUbStmts = null;
                for (int i = 0; i < ub.Stmts.Length; i++)
                {
                    var x = ub.Stmts[i];
                    var nx = SubStmt(x, ctx);
                    if (!ReferenceEquals(x, nx))
                    {
                        if (newUbStmts == null)
                        {
                            newUbStmts = new Stmt[ub.Stmts.Length];
                            Array.Copy(ub.Stmts, newUbStmts, i);
                        }
                    }
                    newUbStmts?[i] = nx;
                }
                if (newUbStmts == null) return s;
                return new UnsafeBlock(newUbStmts, ub.Span) { Span = s.Span };

            case SwitchStmt sw:
                var newSwScrut = SubExpr(sw.Scrutinee, ctx);
                SwitchCase[]? newSwCases = null;
                for (int i = 0; i < sw.Cases.Length; i++)
                {
                    var c = sw.Cases[i];
                    Expr[]? newSwLabels = null;
                    for (int j = 0; j < c.Labels.Length; j++)
                    {
                        var l = c.Labels[j];
                        var nl = SubExpr(l, ctx);
                        if (!ReferenceEquals(l, nl))
                        {
                            if (newSwLabels == null)
                            {
                                newSwLabels = new Expr[c.Labels.Length];
                                Array.Copy(c.Labels, newSwLabels, j);
                            }
                        }
                        newSwLabels?[j] = nl;
                    }
                    var newSwBody = SubBlock(c.Body, ctx);
                    if (newSwLabels != null || !ReferenceEquals(newSwBody, c.Body))
                    {
                        if (newSwCases == null)
                        {
                            newSwCases = new SwitchCase[sw.Cases.Length];
                            Array.Copy(sw.Cases, newSwCases, i);
                        }
                        newSwCases[i] = new SwitchCase(newSwLabels ?? c.Labels, newSwBody, c.Span);
                    }
                    else
                    {
                        newSwCases?[i] = c;
                    }
                }
                var newSwDefault = sw.Default is null ? null : SubBlock(sw.Default, ctx);
                if (ReferenceEquals(newSwScrut, sw.Scrutinee) && newSwCases == null && ReferenceEquals(newSwDefault, sw.Default))
                    return s;
                return new SwitchStmt(newSwScrut, newSwCases ?? sw.Cases, newSwDefault, sw.Span) { Span = s.Span };

            case MatchStmt ms:
                var newMsScrut = SubExpr(ms.Scrutinee, ctx);
                MatchCase[]? newMsCases = null;
                for (int i = 0; i < ms.Cases.Length; i++)
                {
                    var c = ms.Cases[i];
                    var newMsBody = SubBlock(c.Body, ctx);
                    if (!ReferenceEquals(newMsBody, c.Body))
                    {
                        if (newMsCases == null)
                        {
                            newMsCases = new MatchCase[ms.Cases.Length];
                            Array.Copy(ms.Cases, newMsCases, i);
                        }
                        newMsCases[i] = c with { Body = newMsBody };
                    }
                    else
                    {
                        newMsCases?[i] = c;
                    }
                }
                var newMsDefault = ms.Default is null ? null : SubBlock(ms.Default, ctx);
                if (ReferenceEquals(newMsScrut, ms.Scrutinee) && newMsCases == null && ReferenceEquals(newMsDefault, ms.Default))
                    return s;
                return new MatchStmt(newMsScrut, newMsCases ?? ms.Cases, newMsDefault, ms.Span) { Span = s.Span };

            default:
                return s;   // NativeStmt, BreakStmt, ContinueStmt, ThrowStmt, DebugStmt, PanicStmt
        }
    }

    /// <summary>
    /// Substitutes type parameters in an expression, recursively processing any sub-expressions and types.
    /// </summary>
    private static Expr SubExpr(Expr e, SubstitutionContext ctx)
    {
        switch (e)
        {
            case CastExpr ce:
                var newType = ctx.SubType(ce.TargetType);
                var newVal = SubExpr(ce.Value, ctx);
                if (ReferenceEquals(newType, ce.TargetType) && ReferenceEquals(newVal, ce.Value))
                    return e;
                return new CastExpr(newType!, newVal, ce.Span) { Span = e.Span };

            case TernaryExpr te:
                var newCond = SubExpr(te.Cond, ctx);
                var newThen = SubExpr(te.Then, ctx);
                var newElse = SubExpr(te.Else, ctx);
                if (ReferenceEquals(newCond, te.Cond) && ReferenceEquals(newThen, te.Then) && ReferenceEquals(newElse, te.Else))
                    return e;
                return new TernaryExpr(newCond, newThen, newElse, te.Span) { Span = e.Span };

            case NewExpr ne:
                var newNeType = ctx.SubType(ne.Type);
                Expr[]? newNeArgs = null;
                for (int i = 0; i < ne.Args.Length; i++)
                {
                    var a = ne.Args[i];
                    var na = SubExpr(a, ctx);
                    if (!ReferenceEquals(a, na))
                    {
                        if (newNeArgs == null)
                        {
                            newNeArgs = new Expr[ne.Args.Length];
                            Array.Copy(ne.Args, newNeArgs, i);
                        }
                    }
                    newNeArgs?[i] = na;
                }
                Expr[]? newNeColl = null;
                for (int i = 0; i < ne.CollectionInit.Length; i++)
                {
                    var a = ne.CollectionInit[i];
                    var na = SubExpr(a, ctx);
                    if (!ReferenceEquals(a, na))
                    {
                        if (newNeColl == null)
                        {
                            newNeColl = new Expr[ne.CollectionInit.Length];
                            Array.Copy(ne.CollectionInit, newNeColl, i);
                        }
                    }
                    newNeColl?[i] = na;
                }
                if (ReferenceEquals(newNeType, ne.Type) && newNeArgs == null && newNeColl == null)
                    return e;
                return new NewExpr(newNeType!, newNeArgs ?? ne.Args, newNeColl ?? ne.CollectionInit, ne.Span) { Span = e.Span };

            case ArrayLitExpr al:
                Expr[]? newAlElems = null;
                for (int i = 0; i < al.Elems.Length; i++)
                {
                    var a = al.Elems[i];
                    var na = SubExpr(a, ctx);
                    if (!ReferenceEquals(a, na))
                    {
                        if (newAlElems == null)
                        {
                            newAlElems = new Expr[al.Elems.Length];
                            Array.Copy(al.Elems, newAlElems, i);
                        }
                    }
                    newAlElems?[i] = na;
                }
                if (newAlElems == null) return e;
                return new ArrayLitExpr(newAlElems, al.Span) { Span = e.Span };

            case CallExpr cx:
                var newCallee = SubExpr(cx.Callee, ctx);
                Expr[]? newCxArgs = null;
                for (int i = 0; i < cx.Args.Length; i++)
                {
                    var a = cx.Args[i];
                    var na = SubExpr(a, ctx);
                    if (!ReferenceEquals(a, na))
                    {
                        if (newCxArgs == null)
                        {
                            newCxArgs = new Expr[cx.Args.Length];
                            Array.Copy(cx.Args, newCxArgs, i);
                        }
                    }
                    newCxArgs?[i] = na;
                }
                if (ReferenceEquals(newCallee, cx.Callee) && newCxArgs == null)
                    return e;
                return new CallExpr(newCallee, newCxArgs ?? cx.Args, cx.Span) { Span = e.Span };

            case MemberAccessExpr ma:
                var newMaObj = SubExpr(ma.Object, ctx);
                if (ReferenceEquals(newMaObj, ma.Object)) return e;
                return new MemberAccessExpr(newMaObj, ma.Member, ma.Span) { Span = e.Span };

            case IndexExpr ix:
                var newIxObj = SubExpr(ix.Object, ctx);
                var newIxIdx = SubExpr(ix.Index, ctx);
                if (ReferenceEquals(newIxObj, ix.Object) && ReferenceEquals(newIxIdx, ix.Index))
                    return e;
                return new IndexExpr(newIxObj, newIxIdx, ix.Span) { Span = e.Span };

            case BinExpr be:
                var newBeLeft = SubExpr(be.Left, ctx);
                var newBeRight = SubExpr(be.Right, ctx);
                if (ReferenceEquals(newBeLeft, be.Left) && ReferenceEquals(newBeRight, be.Right))
                    return e;
                return new BinExpr(be.Op, newBeLeft, newBeRight, be.Span) { Span = e.Span };

            case UnaryExpr un:
                var newUnOp = SubExpr(un.Operand, ctx);
                if (ReferenceEquals(newUnOp, un.Operand)) return e;
                return new UnaryExpr(un.Op, newUnOp, un.Span) { Span = e.Span };

            case PostfixExpr pf:
                var newPfOp = SubExpr(pf.Operand, ctx);
                if (ReferenceEquals(newPfOp, pf.Operand)) return e;
                return new PostfixExpr(pf.Op, newPfOp, pf.Span) { Span = e.Span };

            case AddrOfExpr ao:
                var newAoTarget = SubExpr(ao.Target, ctx);
                if (ReferenceEquals(newAoTarget, ao.Target)) return e;
                return new AddrOfExpr(newAoTarget, ao.Span) { Span = e.Span };

            case DerefExpr dr:
                var newDrPtr = SubExpr(dr.Ptr, ctx);
                if (ReferenceEquals(newDrPtr, dr.Ptr)) return e;
                return new DerefExpr(newDrPtr, dr.Span) { Span = e.Span };

            case RefArgExpr ra:
                var newRaTarget = SubExpr(ra.Target, ctx);
                if (ReferenceEquals(newRaTarget, ra.Target)) return e;
                return new RefArgExpr(newRaTarget, ra.Span) { Span = e.Span };

            case InterpStrExpr ip:
                Expr[]? newIpParts = null;
                for (int i = 0; i < ip.Parts.Length; i++)
                {
                    var a = ip.Parts[i];
                    var na = SubExpr(a, ctx);
                    if (!ReferenceEquals(a, na))
                    {
                        if (newIpParts == null)
                        {
                            newIpParts = new Expr[ip.Parts.Length];
                            Array.Copy(ip.Parts, newIpParts, i);
                        }
                    }
                    newIpParts?[i] = na;
                }
                if (newIpParts == null) return e;
                return new InterpStrExpr(newIpParts, ip.Span) { Span = e.Span };

            case SizeofExpr so:
                var newSoType = ctx.SubType(so.TypeName);
                if (ReferenceEquals(newSoType, so.TypeName)) return e;
                return new SizeofExpr(newSoType!, so.Span) { Span = e.Span };

            case DefaultExpr de:
                var newDeType = ctx.SubType(de.TypeName);
                if (ReferenceEquals(newDeType, de.TypeName)) return e;
                return new DefaultExpr(newDeType!, de.Span) { Span = e.Span };

            default:
                return e;   // literals, IdentExpr, NullExpr
        }
    }

    #region Generic function helpers

    /// <summary>
    /// Tries to bind a type parameter inferred from one argument position.
    /// Returns false only on a conflicting re-bind; a concrete (non-parameter) type returns true.
    /// A generic-class parameter (e.g. Map[K, V]) unifies against the argument's registered
    /// instantiation record, so multi-parameter generics and underscore-containing class
    /// names infer correctly instead of being split at the first underscore.
    /// </summary>
    internal static bool UnifyParam(TypeSpec paramType, IrType argType,
        string[] gparams, Dictionary<string, TypeSpec> binds)
    {
        switch (paramType)
        {
            case NamedSpec { Args.Length: 0 } n when Array.IndexOf(gparams, n.Name) >= 0:
                return Bind(n.Name, SpecOf(argType), binds);

            case PtrSpec { Inner: NamedSpec { Args.Length: 0 } pn }
                when Array.IndexOf(gparams, pn.Name) >= 0 && argType is IrPtrType ptr:
                return Bind(pn.Name, SpecOf(ptr.Inner), binds);

            case NamedSpec gn when gn.Args.Length > 0 && argType is IrClassRef cr
                && Mangler.TryGetGenericInstance(cr.ClassName, out var instBase, out var instArgs)
                && instBase == gn.Name && instArgs.Count == gn.Args.Length:
            {
                for (int i = 0; i < gn.Args.Length; i++)
                    if (gn.Args[i] is { Args.Length: 0 } an && Array.IndexOf(gparams, an.Name) >= 0
                        && !Bind(an.Name, new NamedSpec(instArgs[i]), binds))
                        return false;
                return true;
            }

            default:
                return true;
        }
    }

    private static bool Bind(string param, TypeSpec spec, Dictionary<string, TypeSpec> binds)
    {
        if (binds.TryGetValue(param, out var prev)) return prev.ToSpecString() == spec.ToSpecString();
        binds[param] = spec;
        return true;
    }

    /// <summary>
    /// Returns the type spec for a resolved IR type, used as the binding value
    /// when inferring type arguments from call-site argument types.
    /// </summary>
    internal static TypeSpec SpecOf(IrType t)
    {
        return t switch
        {
            IrPrimType p => new NamedSpec(p.CName),
            IrClassRef c => new NamedSpec(c.ClassName),
            IrEnumType e => new NamedSpec(e.Name),
            IrUnionType u => new NamedSpec(u.Name),
            IrPtrType pt => new PtrSpec(SpecOf(pt.Inner), TextSpan.None),
            IrVoidType => new NamedSpec("void"),
            IrArrayType a => new ArraySpec(a.Size.ToString(), SpecOf(a.Elem), TextSpan.None),
            IrFuncPtrType f => new FuncSpec([.. f.Params.Select(SpecOf)], SpecOf(f.Ret), TextSpan.None),
            _ => new NamedSpec(t.MangledName)
        };
    }

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
