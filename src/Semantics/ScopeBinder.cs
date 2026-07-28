namespace Appa;

/// <summary>
/// The scope tree of a program plus the index of what each scope declares.
/// </summary>
internal sealed record ScopeBindResult(ScopeTree Tree, ScopeIndex Index);

/// <summary>
/// Maps a written name to the globally unique name it refers to, from a given scope. Lookup walks
/// outward - process, then realm, then root - and the innermost match wins, so an inner declaration
/// shadows an outer one silently.
/// </summary>
internal sealed class ScopeIndex(ScopeTree tree)
{
    private readonly Dictionary<ScopeId, Dictionary<string, string>> _byScope = [];
    private readonly Dictionary<string, ScopeId> _declScopeOf = [];

    /// <summary>True when any declaration at all lives outside the root scope.</summary>
    public bool HasScopedDeclarations => _declScopeOf.Count > 0;

    /// <summary>
    /// Records that <paramref name="scope"/> declares <paramref name="name"/>, which is globally
    /// known as <paramref name="qualified"/>.
    /// </summary>
    public void Declare(ScopeId scope, string name, string qualified)
    {
        if (!_byScope.TryGetValue(scope, out var names)) _byScope[scope] = names = [];
        names[name] = qualified;
        if (!scope.IsRoot) _declScopeOf[qualified] = scope;
    }

    /// <summary>
    /// Every (written, qualified) pair declared directly in a scope.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> DeclaredIn(ScopeId scope) =>
        _byScope.TryGetValue(scope, out var names) ? names : [];

    /// <summary>
    /// Resolves a written name as seen from <paramref name="from"/>, walking outward. Returns null
    /// when no enclosing scope declares it, which means the name is an ordinary root-scope name and
    /// every existing lookup path applies unchanged.
    /// </summary>
    public string? Resolve(ScopeId from, string written)
    {
        for (var s = from; ; s = tree.Parent(s))
        {
            if (_byScope.TryGetValue(s, out var names) && names.TryGetValue(written, out var q)) return q;
            if (s.IsRoot) return null;
        }
    }

    /// <summary>
    /// The scope a qualified name was declared in, or root if it is an ordinary global name. Used
    /// to reject a scoped name reached from outside its scope.
    /// </summary>
    public ScopeId ScopeOf(string qualified) =>
        _declScopeOf.TryGetValue(qualified, out var s) ? s : ScopeId.Root;
}

/// <summary>
/// Resolves scoped declaration names to globally unique ones, ahead of every other pass.
///
/// The alternative - re-keying each downstream registry by (scope, name) - founders on Dce and
/// Densifier, which run after the resolver on IR that has already discarded scope, and where names
/// are only linkage keys. Qualifying up front keeps every registry from the Monomorphizer onward
/// flat, and generalises the trick the compiler already uses for file-local private functions:
/// same written name, different declaring scope, disambiguated by a token in the mangled C name.
/// </summary>
internal sealed class ScopeBinder(DiagnosticBag diag)
{
    /// <summary>
    /// Interns every realm and process scope, records what each declares, then rewrites those
    /// declarations and every type position naming them to the qualified spelling. Two sweeps,
    /// because a declaration may reference a sibling declared later in the same block.
    /// </summary>
    public ScopeBindResult Bind(List<(string path, Program prog)> programs)
    {
        var tree = new ScopeTree();
        var index = new ScopeIndex(tree);

        // Sweep 1: declare.
        foreach (var (file, prog) in programs)
            foreach (var item in prog.Items)
                if (item is ContextDecl { Kind: not Realm.None } realm)
                {
                    // Realm scopes are project-global: every 'realm userspace { }' block, in any
                    // file, interns to the same scope, so a realm is one namespace rather than one
                    // per file.
                    var scope = tree.Intern(ScopeId.Root, NameOf(realm.Kind), realm.Kind);
                    foreach (var inner in realm.Items) DeclareItem(tree, index, scope, inner, file);
                }

        // Skipped entirely when nothing is scoped, so a program declaring nothing inside a realm
        // takes the exact path it took before scopes existed and emits identical C.
        if (!index.HasScopedDeclarations) return new ScopeBindResult(tree, index);

        // Sweep 2: rewrite.
        for (int i = 0; i < programs.Count; i++)
        {
            var (file, prog) = programs[i];
            var items = prog.Items;
            TopLevel[]? rewritten = null;
            var uses = prog.GenericUses;
            GenericUse[]? newUses = null;

            for (int j = 0; j < items.Length; j++)
            {
                if (items[j] is not ContextDecl { Kind: not Realm.None } realm) continue;
                var scope = tree.Intern(ScopeId.Root, NameOf(realm.Kind), realm.Kind);
                var sub = SubstitutionFor(tree, index, scope);
                if (sub == null) continue;

                var inner = new TopLevel[realm.Items.Length];
                for (int k = 0; k < inner.Length; k++)
                    inner[k] = RewriteItem(realm.Items[k], index, scope, sub);

                rewritten ??= (TopLevel[])items.Clone();
                rewritten[j] = realm with { Items = inner };

                // A generic instantiation the parser recorded is a flat list with no scope
                // attached, so the realm it belongs to is recovered from where it was written.
                // Without this, 'Box[Config]' inside a realm asks for a stamp over the outer
                // Config, or over nothing at all.
                for (int u = 0; u < uses.Length; u++)
                {
                    if (!Contains(realm.Span, uses[u].Span)) continue;
                    var moved = RewriteUse(uses[u], index, scope);
                    if (ReferenceEquals(moved, uses[u])) continue;
                    newUses ??= (GenericUse[])uses.Clone();
                    newUses[u] = moved;
                }
            }

            if (rewritten != null || newUses != null)
                programs[i] = (file, prog with
                {
                    Items = rewritten ?? items,
                    GenericUses = newUses ?? uses,
                });
        }

        return new ScopeBindResult(tree, index);
    }

    /// <summary>
    /// Records a single declaration's name in its scope, and rejects the forms that cannot be
    /// scoped yet. Anything unnamed - a native block, an import - contributes nothing.
    /// </summary>
    private void DeclareItem(ScopeTree tree, ScopeIndex index, ScopeId scope, TopLevel item, string file)
    {
        // A generic template inside a realm is rejected because the Monomorphizer collects and
        // splices templates over a program's top-level items only - it has never descended into a
        // realm block - so one declared here would be found by nothing and silently left as a
        // half-formed class. Rejecting it is the honest version of not supporting it yet.
        //
        // The name is no longer the obstacle: a declaration carries its BaseName, so qualifying a
        // template is composing 'List@kernel' with its parameters rather than hoping a suffix strip
        // lands in the right place.
        switch (item)
        {
            case ClassDecl { GenericParams.Length: > 0 } g:
                RejectGeneric(g.Name, g.GenericParams, g.Span, file, scope, tree, "class");
                return;
            case UnionDecl { GenericParams.Length: > 0 } g:
                RejectGeneric(g.Name, g.GenericParams, g.Span, file, scope, tree, "union");
                return;
            case FuncDecl { GenericParams.Length: > 0 } g:
                RejectGeneric(g.Name, g.GenericParams, g.Span, file, scope, tree, "function");
                return;
        }

        string? name = item switch
        {
            ClassDecl cd => cd.Name,
            UnionDecl ud => ud.Name,
            EnumDecl ed => ed.Name,
            NativeTypeDecl nd => nd.Name,
            // An entry func is named by the runtime, not by Gata code - Mangler.FreeFunc maps it to
            // a fixed C symbol - so qualifying it would rename something the compiler does not own.
            // An @extern names a C symbol that already exists under exactly that spelling.
            FuncDecl { IsEntry: false } fd => fd.Name,
            _ => null,
        };
        if (name != null) index.Declare(scope, name, tree.Qualify(scope, name));
    }

    /// <summary>
    /// Reports a generic template declared inside a realm or process.
    /// </summary>
    private void RejectGeneric(string mangledName, string[] generics, TextSpan span, string file,
                               ScopeId scope, ScopeTree tree, string what)
    {
        // The declared name is already "List_T"; recover what the user typed for the message.
        string suffix = "_" + string.Join("_", generics);
        string bare = mangledName.EndsWith(suffix, StringComparison.Ordinal)
            ? mangledName[..^suffix.Length]
            : mangledName;

        diag.Error(Codes.GenericInScope, file, span,
            $"generic {what} '{bare}' cannot be declared inside '{tree.Display(scope, "").TrimEnd('.')}'",
            ["declare it at the top level of the file; a generic may still be used inside a realm, " +
             "and may be instantiated over a type the realm declares"]);
    }

    /// <summary>
    /// Builds the name-to-qualified-type map visible from a scope, or null when it would be empty.
    /// Reuses the Monomorphizer's substitution machinery rather than adding a second AST walker:
    /// binding 'Config' to a NamedSpec of 'Config@kernel' is structurally the same operation as
    /// binding a type parameter to its argument, and that walker is already the one place that
    /// knows every type position in the tree.
    /// </summary>
    private static Monomorphizer.SubstitutionContext? SubstitutionFor(ScopeTree tree, ScopeIndex index, ScopeId scope)
    {
        var specs = new Dictionary<string, TypeSpec>();
        var cTypes = new Dictionary<string, string>();
        foreach (var (written, qualified) in index.DeclaredIn(scope))
        {
            if (written == qualified) continue;
            specs[written] = new NamedSpec(qualified);
            cTypes[written] = Mangler.Class(qualified) + "*";
            Mangler.RegisterScopedName(qualified, tree.Display(scope, written));
        }
        return specs.Count == 0
            ? null
            : new Monomorphizer.SubstitutionContext(specs, cTypes) { RewriteTypeNames = true };
    }

    /// <summary>
    /// Rewrites one declaration: its own name, and every type it mentions.
    /// </summary>
    private static TopLevel RewriteItem(TopLevel item, ScopeIndex index, ScopeId scope, Monomorphizer.SubstitutionContext sub)
    {
        string Q(string name) => index.Resolve(scope, name) ?? name;

        return item switch
        {
            ClassDecl { GenericParams.Length: 0 } cd =>
                cd with { Name = Q(cd.Name), BaseName = Q(cd.BaseName), Members = Monomorphizer.SubMembers(cd.Members, sub) },
            UnionDecl { GenericParams.Length: 0 } ud =>
                ud with { Name = Q(ud.Name), BaseName = Q(ud.BaseName), Variants = Monomorphizer.SubVariants(ud.Variants, sub) },
            EnumDecl ed => ed with { Name = Q(ed.Name) },
            NativeTypeDecl nd => nd with { Name = Q(nd.Name) },
            // A process is not itself a scope yet, but its threads run inside the realm and must
            // see everything the realm declares.
            ProcessDecl pd => pd with { Threads = RewriteThreads(pd.Threads, sub) },
            FuncDecl { GenericParams.Length: 0 } fd => fd with
            {
                Name = Q(fd.Name),
                ReturnType = sub.SubType(fd.ReturnType),
                Params = Monomorphizer.SubParams(fd.Params, sub),
                Body = Monomorphizer.SubBody(fd.Body, sub),
            },
            _ => item,
        };
    }

    /// <summary>
    /// True when the outer span encloses the inner one. Used to attribute a generic instantiation
    /// to the realm it was written in.
    /// </summary>
    private static bool Contains(TextSpan outer, TextSpan inner) =>
        !outer.IsNone && !inner.IsNone
        && inner.Start >= outer.Start && inner.Start < outer.Start + outer.Length;

    /// <summary>
    /// Requalifies a generic instantiation's type arguments. The base name is left alone: a generic
    /// template may only be declared at the top level, so it is never scoped.
    /// </summary>
    private static GenericUse RewriteUse(GenericUse use, ScopeIndex index, ScopeId scope)
    {
        string[]? args = null;
        for (int i = 0; i < use.Args.Length; i++)
        {
            var q = index.Resolve(scope, use.Args[i]);
            if (q == null || q == use.Args[i]) continue;
            args ??= (string[])use.Args.Clone();
            args[i] = q;
        }

        NamedSpec[]? specs = null;
        if (use.ArgSpecs != null)
            for (int i = 0; i < use.ArgSpecs.Length; i++)
            {
                var s = use.ArgSpecs[i];
                if (s.Args.Length != 0) continue;
                var q = index.Resolve(scope, s.Name);
                if (q == null || q == s.Name) continue;
                specs ??= (NamedSpec[])use.ArgSpecs.Clone();
                specs[i] = s with { Name = q };
            }

        if (args == null && specs == null) return use;
        return use with { Args = args ?? use.Args, ArgSpecs = specs ?? use.ArgSpecs };
    }

    /// <summary>
    /// Rewrites each thread's entry body, which is ordinary code running inside the realm.
    /// </summary>
    private static ThreadDecl[] RewriteThreads(ThreadDecl[] threads, Monomorphizer.SubstitutionContext sub)
    {
        var result = new ThreadDecl[threads.Length];
        for (int i = 0; i < threads.Length; i++)
        {
            var t = threads[i];
            result[i] = t with
            {
                Entry = t.Entry with
                {
                    ReturnType = sub.SubType(t.Entry.ReturnType),
                    Params = Monomorphizer.SubParams(t.Entry.Params, sub),
                    Body = Monomorphizer.SubBlock(t.Entry.Body, sub),
                },
            };
        }
        return result;
    }

    /// <summary>
    /// The scope segment naming a realm. Matches the source keyword, so a qualified name and a
    /// diagnostic read the way the user wrote it.
    /// </summary>
    public static string NameOf(Realm r) => r switch
    {
        Realm.Kernel => "kernel",
        Realm.User => "userspace",
        _ => "",
    };
}
