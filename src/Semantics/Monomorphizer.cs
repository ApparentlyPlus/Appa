namespace Appa;

/// <summary>
/// A generic type instantiation the resolver found it needed but the Monomorphizer had no way to
/// see: one written over a generic function's or method's own type parameter, which only becomes
/// concrete when that function is stamped, in a later pass.
/// </summary>
/// <remarks>
/// Args are the mangled spellings of the type arguments - the same form <see cref="GenericUse"/>
/// carries - so seeding one reproduces exactly the request an ordinary syntactic use would have made.
/// </remarks>
internal readonly record struct GenericSeed(string Base, string[] Args, TextSpan Span, string File)
{
    /// <summary>The mangled instance name, which is what makes a seed comparable to another.</summary>
    public string Key => Mangler.GenericInstance(Base, Args);

    /// <summary>
    /// The module scope in force where the instantiation was discovered. Carried rather than
    /// recovered from File, because the discovery happens while resolving a stamped generic body,
    /// whose file is the template's - the type argument came from somewhere that file need never
    /// import. The stamped instance is resolved under this.
    /// </summary>
    public string[] Scope { get; init; } = [];
}

internal sealed class Monomorphizer(DiagnosticBag diag)
{
    // A generic template, either a class or a union. Both are stamped through the same
    // worklist so one can reach for the other - a union variant holding a List[T], a class
    // field holding a Maybe[T] - and so the two share one namespace for duplicate detection.
    private sealed record Template(TopLevel Decl, string[] Params, string BaseName);

    internal sealed class SubstitutionContext(Dictionary<string, TypeSpec> g, Dictionary<string, string>? c)
    {
        public readonly Dictionary<string, TypeSpec> SpecMap = g;
        public readonly Dictionary<string, string> CMap = c ?? [];

        /// <summary>
        /// Rewrites the base name of a generic reference: 'Box[int]' inside a realm declaring Box
        /// means 'Box@kernel[int]'. SpecMap binds whole types and cannot say this. Empty for
        /// monomorphization, where a template's base is never scoped.
        /// </summary>
        public Dictionary<string, string> NameMap { get; init; } = [];

        /// <summary>
        /// Also rewrite bare identifiers naming a substituted type, not just type positions. Off
        /// for monomorphization, where an identifier spelled like a type parameter is a variable;
        /// on for scope binding, where 'Tagged.Ident(...)' has to follow 'let Tagged x'.
        /// </summary>
        public bool RewriteTypeNames { get; init; }

        /// <summary>
        /// Resolves an explicitly qualified type or name against the scope tree. Set only by scope
        /// binding; a resolution here is terminal, since the name it produces is already the one the
        /// author asked for and must not then be re-mapped by SpecMap.
        /// </summary>
        public Func<NamedSpec, NamedSpec>? ScopedType { get; init; }

        public Func<ScopedNameExpr, Expr>? ScopedExpr { get; init; }

        // Names a local binding owns at the point being rewritten. Only RewriteTypeNames needs it:
        // rewriting bare identifiers is otherwise blind, so 'let int Cfg' would go on reading as
        // the scoped type - a variable losing to a type name, which never happens at file scope.
        private readonly List<string> _bound = [];

        /// <summary>
        /// The current binding depth, to be handed back to Release when the scope closes.
        /// </summary>
        public int Mark() => _bound.Count;

        /// <summary>
        /// Drops every binding made since the mark.
        /// </summary>
        public void Release(int mark) => _bound.RemoveRange(mark, _bound.Count - mark);

        /// <summary>
        /// Records that a name now belongs to a local, a parameter or a pattern binding.
        /// </summary>
        public void Bind(string name) => _bound.Add(name);

        public void Bind(Param[] ps) { foreach (var p in ps) _bound.Add(p.Name); }

        public void Bind(string[] names) { foreach (var n in names) _bound.Add(n); }

        /// <summary>
        /// True when a local binding, not a declaration in some scope, owns this name here.
        /// </summary>
        public bool IsBound(string name) => _bound.Contains(name);

        /// <summary>
        /// Substitutes type parameters in raw native C text, replacing whole words that match a
        /// type parameter with its concrete C type. Native bodies are the one place where
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
        /// Structurally substitutes type parameters in a type spec tree. Returns the same reference
        /// when nothing changed so callers can cheaply detect no-ops.
        /// </summary>
        public TypeSpec? SubType(TypeSpec? t)
        {
            switch (t)
            {
                case null:
                    return null;
                case NamedSpec { Scope: not null } q when ScopedType != null:
                {
                    var resolved = ScopedType(q);
                    NamedSpec[]? qArgs = null;
                    for (int i = 0; i < resolved.Args.Length; i++)
                    {
                        var na = SubArg(resolved.Args[i]);
                        if (!ReferenceEquals(na, resolved.Args[i]) && qArgs == null)
                        {
                            qArgs = new NamedSpec[resolved.Args.Length];
                            Array.Copy(resolved.Args, qArgs, i);
                        }
                        qArgs?[i] = na;
                    }
                    return qArgs == null ? resolved : resolved with { Args = qArgs };
                }
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
                        newArgs?[i] = na;
                    }
                    string newName = NameMap.GetValueOrDefault(n.Name, n.Name);
                    if (newArgs == null && newName == n.Name) return t;
                    return n with { Name = newName, Args = newArgs ?? n.Args };
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
                        newPs?[i] = np;
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
        /// binding to a non named spec (like a pointer bound by generic-function inference) folds
        /// to its sanitized mangled fragment to stay a valid slot.
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
    /// Every declaration in a program, descending into realm and process bodies. The single walk
    /// shared by template collection, owner lookup and the splice, so a declaration form one of
    /// them can see is never one another silently cannot.
    /// </summary>
    private static IEnumerable<TopLevel> EachDecl(IEnumerable<TopLevel> items)
    {
        foreach (var item in items)
        {
            yield return item;
            switch (item)
            {
                case ContextDecl cd:
                    foreach (var inner in EachDecl(cd.Items)) yield return inner;
                    break;
                case ProcessDecl pd:
                    foreach (var inner in EachDecl(pd.Items)) yield return inner;
                    break;
            }
        }
    }

    /// <summary>
    /// Stamps a concrete class per distinct instantiation breadth-first, rewriting each program's
    /// Items to replace templates with instances. A use deferred because one template reaches
    /// another through its own parameters replays once its owner is stamped.
    /// </summary>
    public Dictionary<string, string> Process(List<(string path, Program prog)> programs,
                                              IReadOnlyList<GenericSeed>? seeds = null)
    {
        var templates = new Dictionary<string, Template>();
        var tmplNames = new HashSet<string>();
        foreach (var (path, prog) in programs)
            foreach (var item in EachDecl(prog.Items))
            {
                var (declName, genericParams) = item switch
                {
                    ClassDecl cd when cd.GenericParams.Length > 0 => (cd.BaseName, cd.GenericParams),
                    UnionDecl ud when ud.GenericParams.Length > 0 => (ud.BaseName, ud.GenericParams),
                    _ => ((string?)null, (string[]?)null),
                };
                if (declName == null) continue;

                string baseName = declName;

                // 'C[T, T]' names one parameter twice, so the second argument is unreachable and
                // the mangled name collides with the single-parameter form. Silently accepted
                // before, for classes and unions alike.
                var seenParams = new HashSet<string>(genericParams!.Length);
                foreach (var gp in genericParams!)
                    if (!seenParams.Add(gp))
                        diag.Error(Codes.DuplicateName, path, item.Span,
                            $"generic type '{Mangler.DisplayName(baseName)}' declares the type parameter '{gp}' twice");

                if (templates.ContainsKey(baseName))
                    diag.Error(Codes.DuplicateName, path, item.Span,
                        $"generic type '{Mangler.DisplayName(baseName)}' is already declared");
                templates[baseName] = new Template(item, genericParams!, baseName);
                Mangler.RegisterGenericTemplate(baseName, genericParams!.Length);
                tmplNames.Add(Mangler.GenericInstance(baseName, genericParams!));
            }

        if (templates.Count == 0) return [];

        var directUses = new List<(GenericUse Use, string File)>();
        var deferredByOwner = new Dictionary<string, List<(GenericUse Use, string File)>>();
        foreach (var (path, prog) in programs)
        {
            var declsInFile = new HashSet<TopLevel>(EachDecl(prog.Items));
            var ownersInFile = templates.Values.Where(t => declsInFile.Contains(t.Decl)).ToList();
            var funcOwners = EachDecl(prog.Items).OfType<FuncDecl>()
                                                 .Where(f => f.GenericParams.Length > 0)
                                                 .ToList();
            foreach (var use in prog.GenericUses)
            {
                if (funcOwners.Any(fd =>
                        use.Span.Start >= fd.Span.Start && use.Span.End <= fd.Span.End &&
                        MentionsParam(use, fd.GenericParams)))
                    continue;
                var owner = ownersInFile.FirstOrDefault(t =>
                    t.BaseName != use.Base &&
                    use.Span.Start >= t.Decl.Span.Start && use.Span.End <= t.Decl.Span.End &&
                    MentionsParam(use, t.Params));
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
        var scopeRequester = new Dictionary<string, string>();

        bool AddRequest(string b, string[] a, TextSpan sp, string file, string requester)
        {
            if (!templates.ContainsKey(b)) return false;
            string mangled = Mangler.GenericInstance(b, a);
            if (tmplNames.Contains(mangled)) return false;
            if (!requests.TryAdd(mangled, (b, a, sp, file))) return false;
            scopeRequester[mangled] = requester;
            return true;
        }
        foreach (var (use, file) in directUses) AddRequest(use.Base, use.Args, use.Span, file, file);
        
        if (seeds != null)
            foreach (var s in seeds) AddRequest(s.Base, s.Args, s.Span, s.File, s.File);

        var instancesByBase = new Dictionary<string, List<TopLevel>>();
        var requestedFrom = new Dictionary<string, string>();
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
                Mangler.MarkGenericFailed(mangled);
                continue;
            }
            if (Array.Exists(args, a => a.Trim() == "void"))
            {
                diag.Error(Codes.UndefinedType, file, span,
                    $"'void' is not a valid type argument to '{baseName}'");
                Mangler.RegisterGenericInstance(mangled, baseName, [..args]);
                Mangler.MarkGenericFailed(mangled);
                continue;
            }
            var (concrete, binds) = Instantiate(tmpl, args, mangled);
            Mangler.RegisterGenericInstance(mangled, baseName, [..args]);
            string requester = scopeRequester.GetValueOrDefault(mangled, file);
            requestedFrom[mangled] = requester;
            if (!instancesByBase.TryGetValue(baseName, out var list))
                instancesByBase[baseName] = list = [];
            list.Add(concrete);

            if (deferredByOwner.TryGetValue(baseName, out var deferred))
                foreach (var (du, dfile) in deferred)
                {
                    var concreteArgs = SubstituteArgs(du, binds);
                    if (AddRequest(du.Base, concreteArgs, du.Span, dfile, requester))
                        pending.Enqueue(Mangler.GenericInstance(du.Base, concreteArgs));
                }
        }

        for (int i = 0; i < programs.Count; i++)
        {
            var (path, prog) = programs[i];
            bool changed = false;
            var hoisted = new List<TopLevel>();

            TopLevel[] Strip(TopLevel[] items)
            {
                var kept = new List<TopLevel>(items.Length);
                foreach (var item in items)
                {
                    string? tmplBase = item switch
                    {
                        ClassDecl cd when cd.GenericParams.Length > 0 => cd.BaseName,
                        UnionDecl ud when ud.GenericParams.Length > 0 => ud.BaseName,
                        _ => null,
                    };
                    if (tmplBase != null)
                    {
                        changed = true;
                        if (instancesByBase.TryGetValue(tmplBase, out var instances))
                            hoisted.AddRange(instances);
                        continue;
                    }
                    switch (item)
                    {
                        case ContextDecl cd:
                        {
                            var inner = Strip(cd.Items);
                            kept.Add(ReferenceEquals(inner, cd.Items) ? cd : cd with { Items = inner });
                            break;
                        }
                        case ProcessDecl pd:
                        {
                            var inner = Strip(pd.Items);
                            kept.Add(ReferenceEquals(inner, pd.Items) ? pd : pd with { Items = inner });
                            break;
                        }
                        default:
                            kept.Add(item);
                            break;
                    }
                }
                return kept.Count == items.Length && !changed ? items : [.. kept];
            }

            var rewritten = Strip(prog.Items);
            if (changed) programs[i] = (path, prog with { Items = [.. hoisted, .. rewritten] });
        }

        return requestedFrom;
    }

    /// <summary>
    /// True if any type argument mentions one of the given parameters, at any depth. Testing
    /// whether an argument *is* one caught 'List[T]' in 'Foo[T]' but not 'List[Node[T]]' in
    /// 'Node[T]'; requiring *every* argument to be one missed 'Pair[T, int]'.
    /// </summary>
    private static bool MentionsParam(GenericUse use, string[] parameters)
    {
        if (parameters.Length == 0) return false;
        if (use.ArgSpecs is not { } specs || specs.Length != use.Args.Length)
            return use.Args.Any(a => Array.IndexOf(parameters, a) >= 0);

        foreach (var spec in specs)
            if (Mentions(spec)) return true;
        return false;

        bool Mentions(NamedSpec s)
        {
            if (s.Args.Length == 0) return Array.IndexOf(parameters, s.Name) >= 0;
            foreach (var a in s.Args)
                if (Mentions(a)) return true;
            return false;
        }
    }

    /// <summary>
    /// Rewrites a deferred use's type arguments against its owner's bindings, so 'List[T]' in
    /// 'Foo[T]' becomes 'List[int]' when Foo[int] is stamped. Structural where the parse kept the
    /// shape, since a whole-string lookup only catches a bare parameter.
    /// </summary>
    private static string[] SubstituteArgs(GenericUse du, Dictionary<string, string> binds)
    {
        if (du.ArgSpecs is not { } specs || specs.Length != du.Args.Length)
            return [.. du.Args.Select(a => binds.GetValueOrDefault(a, a))];

        var specMap = new Dictionary<string, TypeSpec>(binds.Count);
        foreach (var (param, concrete) in binds) specMap[param] = new NamedSpec(concrete);
        var ctx = new SubstitutionContext(specMap, null);

        var result = new string[specs.Length];
        for (int i = 0; i < specs.Length; i++)
            result[i] = ctx.SubType(specs[i]) is NamedSpec ns ? ns.Mangled : du.Args[i];
        return result;
    }

    /// <summary>
    /// Clones a generic class template with concrete type arguments, substituting type parameters
    /// throughout signatures, native fields, and statement bodies.
    /// </summary>
    private (TopLevel Concrete, Dictionary<string, string> Binds) Instantiate(
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

        // A union has no members, only variants, and a variant's fields are plain params - so
        // stamping one is just substituting every field's type spec.
        if (tmpl.Decl is UnionDecl utd)
        {
            var variants = new UnionVariant[utd.Variants.Length];
            for (int i = 0; i < variants.Length; i++)
            {
                var v = utd.Variants[i];
                var fields = new Param[v.Fields.Length];
                for (int j = 0; j < fields.Length; j++)
                {
                    var f = v.Fields[j];
                    fields[j] = f with { Type = ctx.SubType(f.Type)! };
                }
                variants[i] = v with { Fields = fields };
            }
            return (new UnionDecl(mangled, [], variants, utd.Span, utd.Annotations), gataMap);
        }

        var classTmpl = (ClassDecl)tmpl.Decl;
        var members = new ClassMember[classTmpl.Members.Length];
        bool changed = false;
        for (int i = 0; i < members.Length; i++)
        {
            var m = classTmpl.Members[i];
            var sm = SubMember(m, ctx);
            members[i] = sm;
            if (!ReferenceEquals(m, sm)) changed = true;
        }

        var concrete = changed
            ? new ClassDecl(mangled, [], classTmpl.Annotations, members, classTmpl.Span)
            : classTmpl with { Name = mangled };
        return (concrete, gataMap);
    }

    /// <summary>
    /// Substitutes every type mentioned in a class's members, returning the same array when nothing
    /// changed. Shared with the ScopeBinder, which rewrites scoped type names through exactly the
    /// same walker rather than duplicating knowledge of where type positions live.
    /// </summary>
    internal static ClassMember[] SubMembers(ClassMember[] members, SubstitutionContext ctx)
    {
        ClassMember[]? result = null;
        for (int i = 0; i < members.Length; i++)
        {
            var sm = SubMember(members[i], ctx);
            if (!ReferenceEquals(sm, members[i]) && result == null)
            {
                result = new ClassMember[members.Length];
                Array.Copy(members, result, i);
            }
            result?[i] = sm;
        }
        return result ?? members;
    }

    /// <summary>
    /// Substitutes every type mentioned in a union's variant payloads.
    /// </summary>
    internal static UnionVariant[] SubVariants(UnionVariant[] variants, SubstitutionContext ctx)
    {
        var result = new UnionVariant[variants.Length];
        for (int i = 0; i < variants.Length; i++)
        {
            var v = variants[i];
            result[i] = v with { Fields = SubParams(v.Fields, ctx) };
        }
        return result;
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
    /// Substitutes type parameters in a field declaration, including its type and initializer
    /// expression.
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
    /// Substitutes type parameters in a method declaration, including its return type, parameters,
    /// and body.
    /// </summary>
    private static MethodDecl SubMethodDecl(MethodDecl md, SubstitutionContext ctx)
    {
        var newRet = ctx.SubType(md.ReturnType);
        var newParams = SubParams(md.Params, ctx);
        var newBody = SubBody(md.Body, md.Params, ctx);
        if (ReferenceEquals(newRet, md.ReturnType) && ReferenceEquals(newParams, md.Params) && ReferenceEquals(newBody, md.Body))
            return md;
        return new MethodDecl(md.Modifiers, md.Annotations, newRet, md.Name, md.GenericParams, newParams, md.IsEntry, md.Throws, newBody, md.Span);
    }

    /// <summary>
    /// Substitutes type parameters in an operator declaration, including its return type,
    /// parameters, and body.
    /// </summary>
    private static OperatorDecl SubOperatorDecl(OperatorDecl od, SubstitutionContext ctx)
    {
        var newParams = SubParams(od.Params, ctx);
        var newRet = ctx.SubType(od.ReturnType);
        var newBody = SubBody(od.Body, od.Params, ctx);
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
    internal static MethodBody SubBody(MethodBody b, Param[] ps, SubstitutionContext ctx)
    {
        int mark = ctx.Mark();
        ctx.Bind(ps);
        var result = SubBody(b, ctx);
        ctx.Release(mark);
        return result;
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
    /// Substitutes type parameters in a native method body's code string.
    /// </summary>
    private static NativeBody SubNative(NativeBody nb, SubstitutionContext ctx)
    {
        var newC = ctx.SubWords(nb.C);
        return ReferenceEquals(newC, nb.C) ? nb : new NativeBody(newC);
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
    /// Returns the C-type spelling for a Gata type argument, used when substituting type parameters
    /// inside native struct fields and native bodies.
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
    /// Substitutes type parameters in a block of statements, returning a new block if any
    /// substitutions occurred.
    /// </summary>
    internal static Block SubBlock(Block b, SubstitutionContext ctx)
    {
        int mark = ctx.Mark();
        Stmt[]? newStmts = null;
        for (int i = 0; i < b.Stmts.Length; i++)
        {
            var s = b.Stmts[i];
            var ns = SubStmt(s, ctx);
            if (s is LetStmt let) ctx.Bind(let.Name);
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
        ctx.Release(mark);
        if (newStmts == null) return b;
        return new Block(newStmts, b.Span);
    }

    /// <summary>
    /// Substitutes type parameters in a single statement, recursively processing any nested
    /// statements or expressions.
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
                int forMark = ctx.Mark();
                var newFInit = fs.Init is null ? null : SubStmt(fs.Init, ctx);
                if (fs.Init is LetStmt fLet) ctx.Bind(fLet.Name);
                var newFCond = fs.Cond is null ? null : SubExpr(fs.Cond, ctx);
                var newFStep = fs.Step is null ? null : SubStmt(fs.Step, ctx);
                var newFBody = SubBlock(fs.Body, ctx);
                ctx.Release(forMark);
                if (ReferenceEquals(newFInit, fs.Init) && ReferenceEquals(newFCond, fs.Cond) &&
                    ReferenceEquals(newFStep, fs.Step) && ReferenceEquals(newFBody, fs.Body))
                    return s;
                return new ForStmt(newFInit, newFCond, newFStep, newFBody, fs.Span) { Span = s.Span };

            case ForInStmt fi:
                var newFiColl = SubExpr(fi.Collection, ctx);
                int inMark = ctx.Mark();
                ctx.Bind(fi.Var);
                var newFiBody = SubBlock(fi.Body, ctx);
                ctx.Release(inMark);
                if (ReferenceEquals(newFiColl, fi.Collection) && ReferenceEquals(newFiBody, fi.Body))
                    return s;
                return new ForInStmt(fi.Var, newFiColl, newFiBody, fi.Span) { Span = s.Span };

            case ReturnStmt rs:
                var newRv = rs.Value is null ? null : SubExpr(rs.Value, ctx);
                if (ReferenceEquals(newRv, rs.Value)) return s;
                return new ReturnStmt(newRv, rs.Span) { Span = s.Span };

            case AssignValueStmt av:
                var newAvValue = SubExpr(av.Value, ctx);
                if (ReferenceEquals(newAvValue, av.Value)) return s;
                return new AssignValueStmt(newAvValue, av.Span) { Span = s.Span };

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
                    int caseMark = ctx.Mark();
                    ctx.Bind(c.Bindings);
                    var newMsBody = SubBlock(c.Body, ctx);
                    ctx.Release(caseMark);
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
                // NativeStmt, BreakStmt, ContinueStmt, ThrowStmt, DebugStmt, PanicStmt - nothing
                // to substitute.
                NodeCoverage.AssertInertAstStmt(s);
                return s;
        }
    }

    /// <summary>
    /// Substitutes type parameters in an expression, recursively processing any sub-expressions and
    /// types.
    /// </summary>
    internal static Expr SubExpr(Expr e, SubstitutionContext ctx)
    {
        switch (e)
        {
            case ScopedNameExpr sn when ctx.ScopedExpr != null:
                return ctx.ScopedExpr(sn);

            case IdentExpr id when ctx.RewriteTypeNames && !ctx.IsBound(id.Name):
                return ctx.SpecMap.TryGetValue(id.Name, out var bound)
                    && bound is NamedSpec { Args.Length: 0 } ns
                    ? new IdentExpr(ns.Name, id.Span) { Span = e.Span }
                    : e;

            case CastExpr ce:
                var newType = ctx.SubType(ce.TargetType);
                var newVal = SubExpr(ce.Value, ctx);
                if (ReferenceEquals(newType, ce.TargetType) && ReferenceEquals(newVal, ce.Value))
                    return e;
                return new CastExpr(newType!, newVal, ce.Span) { Span = e.Span };

            case GenericTypeRefExpr gt:
            {
                var subArgs = new NamedSpec[gt.Args.Length];
                bool argsChanged = false;
                for (int i = 0; i < gt.Args.Length; i++)
                {
                    subArgs[i] = ctx.SubType(gt.Args[i]) as NamedSpec ?? gt.Args[i];
                    argsChanged |= !ReferenceEquals(subArgs[i], gt.Args[i]);
                }
                var subIndex = gt.IndexForm == null ? null : SubExpr(gt.IndexForm, ctx);
                string subName = ctx.RewriteTypeNames && !ctx.IsBound(gt.Name)
                    ? ctx.NameMap.GetValueOrDefault(gt.Name, gt.Name) : gt.Name;
                if (!argsChanged && ReferenceEquals(subIndex, gt.IndexForm) && subName == gt.Name) return e;
                return new GenericTypeRefExpr(subName, subArgs, subIndex, gt.Span) { Span = e.Span };
            }

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


            case CatchCallExpr cc:
                var newCcCall = SubExpr(cc.Call, ctx);
                var newCcHandler = SubBlock(cc.Handler, ctx);
                if (ReferenceEquals(newCcCall, cc.Call) && ReferenceEquals(newCcHandler, cc.Handler)) return e;
                return new CatchCallExpr(newCcCall, newCcHandler, cc.Span) { Span = e.Span };

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
                // Literals, IdentExpr, NullExpr - nothing to substitute.
                NodeCoverage.AssertInertAstExpr(e);
                return e;
        }
    }

    #region Generic function helpers

    /// <summary>
    /// Tries to bind a type parameter inferred from one argument position.
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

            // A generic instantiation in parameter position, matched against the stamped
            // instance passed in. Both a class and a union arrive here - 'Count[T](Node[T] n)'
            // called with a Node[int] carries an IrUnionType, not an IrClassRef.
            case NamedSpec gn when gn.Args.Length > 0 && NameOfInstance(argType) is { } instName
                && Mangler.TryGetGenericInstance(instName, out var instBase, out var instArgs)
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

    /// <summary>
    /// Returns the declared name of a type that could be a stamped generic instance - a class
    /// reference or a union - or null for anything that could not be one.
    /// </summary>
    private static string? NameOfInstance(IrType t)
    {
        return t switch
        {
            IrClassRef cr => cr.ClassName,
            IrUnionType ut => ut.Name,
            _ => null,
        };
    }

    private static bool Bind(string param, TypeSpec spec, Dictionary<string, TypeSpec> binds)
    {
        if (binds.TryGetValue(param, out var prev)) return prev.ToSpecString() == spec.ToSpecString();
        binds[param] = spec;
        return true;
    }

    /// <summary>
    /// Returns the type spec for a resolved IR type, used as the binding value when inferring type
    /// arguments from call-site argument types.
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
