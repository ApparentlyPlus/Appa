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

    /// <summary>
    /// True when any declaration at all lives outside the root scope.
    /// </summary>
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
    /// The qualified name a scope declares directly, or null. No outward walk: a written qualifier
    /// names one exact scope.
    /// </summary>
    public string? TryDeclared(ScopeId scope, string written) =>
        _byScope.TryGetValue(scope, out var names) && names.TryGetValue(written, out var q) ? q : null;

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
/// Resolves scoped declaration names to globally unique ones, ahead of every other pass. Re-keying
/// each registry by (scope, name) instead founders on Dce and Densifier, which run on IR that has
/// already discarded scope and where a name is only a linkage key.
/// </summary>
internal sealed class ScopeBinder(DiagnosticBag diag)
{
    // Process names per realm, and the repeats. A process is not a symbol, so nothing downstream
    // deduplicates it, and both halves of a repeat mangle into the same C function names.
    private readonly HashSet<(ScopeId Realm, string Name)> _processes = [];
    private readonly HashSet<ProcessDecl> _duplicates = [];

    // Every scoped declaration, for the shadowing pass. Collected rather than judged on the spot,
    // because what a name shadows is only known once every scope has been filled.
    private readonly List<(string File, ScopeId Scope, string Name, TopLevel Item)> _declared = [];

    /// <summary>
    /// Interns every realm and process scope, records what each declares, then rewrites those
    /// declarations and every type position naming them to the qualified spelling. Two sweeps,
    /// because a declaration may reference a sibling declared later in the same block.
    /// </summary>
    public ScopeBindResult Bind(List<(string path, Program prog)> programs,
                                Dictionary<string, HashSet<string>>? visible = null)
    {
        var tree = new ScopeTree();
        var index = new ScopeIndex(tree);
        var atRoot = _atRoot = RootDeclarations(programs);

        // A realm is one project global namespace, so a process cannot be told what it shadows
        // until the whole realm is known - including the half declared after it, or in another file.
        var processes = new List<(string File, ScopeId Realm, ProcessDecl Decl)>();
        foreach (var (file, prog) in programs)
            foreach (var item in prog.Items)
                if (item is ContextDecl { Kind: not Realm.None } realm)
                {
                    var scope = tree.Intern(ScopeId.Root, NameOf(realm.Kind), realm.Kind);
                    foreach (var inner in realm.Items)
                    {
                        DeclareItem(tree, index, scope, inner, file);
                        if (inner is ProcessDecl pd) processes.Add((file, scope, pd));
                    }
                }

        // each process's own declarations, under a scope of its own
        foreach (var (file, scope, pd) in processes)
        {
            // Two processes of one name intern to one scope, so their declarations would merge and
            // be reported as duplicates of each other
            if (!_processes.Add((scope, pd.Name)))
            {
                diag.Error(Codes.DuplicateName, file, pd.Span,
                    $"process '{pd.Name}' is already declared in '{Owner(tree, scope)}'");
                _duplicates.Add(pd);
                continue;
            }

            // A process name segments the scope path, so 'Kernel.P' would name both the process and
            // a class P declared beside it
            Claim(new Named(pd.Name, scope, NameKind.Process, false, file, pd.Span));

            // A process nests under its realm and declares no realm of its own, so ScopeTree.RealmOf walks past it
            var proc = tree.Intern(scope, pd.Name, Realm.None);
            foreach (var pi in pd.Items) DeclareItem(tree, index, proc, pi, file);
        }

        // shadowing and name hygiene, once every scope knows what it holds
        CheckShadowing(tree, programs, atRoot, visible);
        CheckOneMeaningPerName(tree);

        // Skipped when nothing is scoped, so such a program emits identical C
        bool anyQualifier = programs.Any(p => p.prog.HasScopedRefs);
        if (!index.HasScopedDeclarations && _duplicates.Count == 0 && !anyQualifier)
            return new ScopeBindResult(tree, index);

        // rewrite
        for (int i = 0; i < programs.Count; i++)
        {
            var (file, prog) = programs[i];
            var items = prog.Items;
            TopLevel[]? rewritten = null;
            var uses = prog.GenericUses;
            GenericUse[]? newUses = null;

            // Root level code may write a qualifier too, like '::Name' for a name it shadows nowhere,
            // or a realm it is not inside, which has to be reported rather than left standing
            if (prog.HasScopedRefs)
            {
                var rootSub = SubstitutionFor(tree, index, ScopeId.Root, file);
                for (int j = 0; j < items.Length; j++)
                {
                    if (items[j] is ContextDecl) continue;
                    var moved = RewriteItem(items[j], tree, index, ScopeId.Root, rootSub, file);
                    if (ReferenceEquals(moved, items[j])) continue;
                    rewritten ??= (TopLevel[])items.Clone();
                    rewritten[j] = moved;
                }
                for (int u = 0; u < uses.Length; u++)
                {
                    if (uses[u].Scope == null) continue;
                    if (items.Any(it => it is ContextDecl c && Contains(c.Span, uses[u].Span))) continue;
                    var moved = RewriteUse(uses[u], index, ScopeId.Root, tree, file);
                    if (ReferenceEquals(moved, uses[u])) continue;
                    newUses ??= (GenericUse[])uses.Clone();
                    newUses[u] = moved;
                }
            }

            for (int j = 0; j < items.Length; j++)
            {
                if (items[j] is not ContextDecl { Kind: not Realm.None } realm) continue;
                var scope = tree.Intern(ScopeId.Root, NameOf(realm.Kind), realm.Kind);
                var sub = SubstitutionFor(tree, index, scope, file);

                var inner = new TopLevel[realm.Items.Length];
                for (int k = 0; k < inner.Length; k++)
                    inner[k] = RewriteItem(realm.Items[k], tree, index, scope, sub, file);

                rewritten ??= (TopLevel[])items.Clone();
                rewritten[j] = realm with { Items = inner };

                for (int u = 0; u < uses.Length; u++)
                {
                    if (!Contains(realm.Span, uses[u].Span)) continue;

                    var useScope = scope;
                    foreach (var it in realm.Items)
                        if (it is ProcessDecl p && Contains(p.Span, uses[u].Span))
                        {
                            useScope = tree.Intern(scope, p.Name, Realm.None);
                            break;
                        }

                    var moved = RewriteUse(uses[u], index, useScope, tree, file);
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
        // A generic template is declared under its base name, so 'Box' inside the scope resolves
        // to 'Box@kernel' and its stamps follow. The base is carried on the declaration, which is
        // what makes qualifying it a composition rather than a guess at the mangled form.

        string? name = item switch
        {
            ClassDecl cd => cd.BaseName,
            UnionDecl ud => ud.BaseName,
            EnumDecl ed => ed.Name,
            NativeTypeDecl nd => nd.Name,
            FuncDecl { IsEntry: false } fd => fd.Name,
            _ => null,
        };
        if (name == null)
        {
            RejectStrayShadows(item, file, "it belongs on a class, module, enum, union, native type " +
                                           "or free function");
            return;
        }
        string qualified = tree.Qualify(scope, name);
        index.Declare(scope, name, qualified);
        Mangler.RegisterScopedKind(qualified, Describe(KindOf(item)));
        _declared.Add((file, scope, name, item));
        Claim(new Named(name, scope, KindOf(item), IsPrivate(item), file, item.Span));
    }

    /// <summary>
    /// What a name means in the scope that declares it. A scope holds one meaning per name - a type
    /// and a function of one name would each be reachable at root, but a scoped declaration takes
    /// over the whole name, so the two spellings could not both survive being shadowed.
    /// </summary>
    private enum NameKind { Type, Generic, Func, Process }

    private readonly record struct Named(string Name, ScopeId Scope, NameKind Kind, bool Private,
                                         string File, TextSpan Span);

    // Qualifiers already rejected, per file. One written 'kernel.Cfg' is reached from the let's type
    // and again from the 'new', and each is the same fact about the same path.
    private readonly HashSet<(string File, string Path, string Name)> _badQualifier = [];

    // Every top level name in the build, so '::Name' can say when nothing declares it.
    private Dictionary<string, List<RootDecl>> _atRoot = [];

    // Every declaration that claims a name, in every scope including root, keyed the way both the
    // one-meaning check and the outward walk ask for it.
    private readonly Dictionary<(ScopeId Scope, string Name), List<Named>> _named = [];

    /// <summary>
    /// Records a declaration under the name and scope it claims.
    /// </summary>
    private void Claim(Named d)
    {
        if (!_named.TryGetValue((d.Scope, d.Name), out var list)) _named[(d.Scope, d.Name)] = list = [];
        list.Add(d);
    }

    /// <summary>
    /// The kind of name a declaration claims. A generic template is its own kind: 'Box' and 'Box[T]'
    /// are two answers to what 'Box' means in a type position, and neither wins.
    /// </summary>
    private static NameKind KindOf(TopLevel item) => item switch
    {
        ClassDecl { GenericParams.Length: > 0 } => NameKind.Generic,
        UnionDecl { GenericParams.Length: > 0 } => NameKind.Generic,
        FuncDecl => NameKind.Func,
        ExternFuncDecl => NameKind.Func,
        _ => NameKind.Type,
    };

    /// <summary>
    /// True for a declaration only its own file can see, which therefore only collides there.
    /// </summary>
    private static bool IsPrivate(TopLevel item) => item is FuncDecl f && (f.Modifiers & Modifiers.Private) != 0;

    /// <summary>
    /// The word a diagnostic uses for a kind of name.
    /// </summary>
    private static string Describe(NameKind k) => k switch
    {
        NameKind.Generic => "a generic type",
        NameKind.Func => "a function",
        NameKind.Process => "a process",
        _ => "a type",
    };

    /// <summary>
    /// Reports a name given two different meanings in one scope. Two functions are overloads and two
    /// types a plain duplicate, both owned elsewhere; every other pairing is nobody's, and leaves a
    /// name whose meaning depends on the position it is read in.
    /// </summary>
    private void CheckOneMeaningPerName(ScopeTree tree)
    {
        foreach (var ((scope, name), decls) in _named)
            for (int i = 1; i < decls.Count; i++)
            {
                var prev = decls[0];
                var d = decls[i];
                if (prev.Kind == d.Kind) continue;

                // A private function is file local, so it only takes a name away inside its own file
                if ((prev.Private || d.Private) && !PathsEqual(prev.File, d.File)) continue;

                string where = scope.IsRoot ? "" : $" in '{Owner(tree, scope)}'";
                diag.Error(Codes.DuplicateName, d.File, d.Span,
                    $"'{name}' is already declared{where} as {Describe(prev.Kind)}",
                    [$"one name means one thing in a scope; rename this {Describe(d.Kind)[2..]}"]);
                break;
            }
    }

    /// <summary>
    /// Every top level declaration in the build, mapped to the file that declares it
    /// </summary>
    private Dictionary<string, List<RootDecl>> RootDeclarations(List<(string path, Program prog)> programs)
    {
        var byName = new Dictionary<string, List<RootDecl>>();
        foreach (var (file, prog) in programs)
            foreach (var item in prog.Items)
            {
                // An @extern names a C symbol under exactly its own spelling, so it cannot be
                // qualified - but a scoped declaration of that name still takes the name over
                string? n = item switch
                {
                    ClassDecl cd => cd.BaseName,
                    UnionDecl ud => ud.BaseName,
                    EnumDecl ed => ed.Name,
                    NativeTypeDecl nd => nd.Name,
                    FuncDecl { IsEntry: false } fd => fd.Name,
                    ExternFuncDecl ef => ef.Name,
                    _ => null,
                };
                if (n == null) continue;
                bool priv = IsPrivate(item);
                if (!byName.TryGetValue(n, out var list)) byName[n] = list = [];
                list.Add(new RootDecl(file, priv));
                Claim(new Named(n, ScopeId.Root, KindOf(item), priv, file, item.Span));
            }
        return byName;
    }

    /// <summary>
    /// One top level declaration of a name: the file it is written in, and whether it is file local.
    /// </summary>
    private readonly record struct RootDecl(string File, bool Private);

    /// <summary>
    /// Reports every scoped declaration whose intent about shadowing does not match what it does.
    /// Runs once the whole tree is declared, since an inner declaration may shadow one written later
    /// in an enclosing block, or in another file that opens the same realm.
    /// </summary>
    private void CheckShadowing(ScopeTree tree, List<(string path, Program prog)> programs,
                                Dictionary<string, List<RootDecl>> atRoot,
                                Dictionary<string, HashSet<string>>? visible)
    {
        // Root has nothing outside it, so a declaration at file scope can never be displacing
        // anything. Checked here rather than in _declared, which only holds scoped declarations.
        foreach (var (file, prog) in programs)
            foreach (var item in prog.Items)
                RejectStrayShadows(item, file, "move the declaration inside the realm or process it " +
                                               "belongs to, or remove the annotation");

        foreach (var (file, scope, name, item) in _declared)
        {
            bool marked = Annotations(item).Any(a => a is ShadowsAnnotation);
            string? outer = OuterDeclaring(tree, scope, name, file, atRoot, visible);

            if (outer != null && !marked)
                diag.Error(Codes.UnmarkedShadow, file, item.Span,
                    $"'{name}' shadows the '{name}' declared in {outer}",
                    ["write '@shadows' before it if displacing that name is deliberate",
                     "otherwise rename this one; the outer declaration is not reachable from here"]);
            else if (outer == null && marked)
                diag.Error(Codes.UnmarkedShadow, file, item.Span,
                    $"'{name}' is marked '@shadows' but nothing outside '{Owner(tree, scope)}' declares it",
                    ["remove '@shadows'"]);
        }
    }

    /// <summary>
    /// Where an enclosing scope declares this name, rendered for a diagnostic, or null. Walks out to
    /// root, then falls back to the file's imports - the two ways a name can already mean something.
    /// </summary>
    private string? OuterDeclaring(ScopeTree tree, ScopeId scope, string name,
                                   string file, Dictionary<string, List<RootDecl>> atRoot,
                                   Dictionary<string, HashSet<string>>? visible)
    {
        // Outward one scope at a time rather than through ScopeIndex.Resolve, because a 'private'
        // declaration in another file is not a name this file could read: it has to be walked past
        // rather than reported as the thing being displaced.
        for (var s = tree.Parent(scope); !s.IsRoot; s = tree.Parent(s))
            foreach (var d in _named.GetValueOrDefault((s, name), []))
                if (!d.Private || PathsEqual(d.File, file))
                    return $"'{Owner(tree, s)}'";


        // A name not declared in any enclosing scope may still be imported from another file, so check
        // the file's imports
        if (!atRoot.TryGetValue(name, out var decls)) return null;
        HashSet<string>? reachable = null;
        visible?.TryGetValue(file, out reachable);

        string? imported = null;
        foreach (var d in decls)
        {
            if (PathsEqual(d.File, file)) return "the top level of this file";
            if (d.Private || reachable == null || !reachable.Contains(d.File)) continue;
            imported ??= $"'{Path.GetFileNameWithoutExtension(d.File)}'";
        }
        return imported;
    }

    /// <summary>
    /// Reports '@shadows' written where it can never mean anything: at the top level of a file,
    /// which has no enclosing scope, or on a form that is not a name in any scope.
    /// </summary>
    private void RejectStrayShadows(TopLevel item, string file, string advice)
    {
        if (Annotations(item).OfType<ShadowsAnnotation>().FirstOrDefault() is not { } stray) return;
        diag.Error(Codes.UnmarkedShadow, file, stray.Span, "'@shadows' displaces nothing here", [advice]);
    }

    /// <summary>
    /// The annotations a declaration carries, or none for the forms that take none. Covers the
    /// unnamed forms too, so a mark on one is rejected rather than silently ignored.
    /// </summary>
    private static Annotation[] Annotations(TopLevel item) => item switch
    {
        ClassDecl cd => cd.Annotations,
        UnionDecl ud => ud.Annotations ?? [],
        EnumDecl ed => ed.Annotations ?? [],
        NativeTypeDecl nd => nd.Annotations ?? [],
        FuncDecl fd => fd.Annotations,
        ExternFuncDecl ef => ef.Annotations ?? [],
        NativeBlock nb => nb.Annotations ?? [],
        _ => [],
    };

    /// <summary>
    /// The readable name of a scope, for a diagnostic that names where something already exists.
    /// </summary>
    private static string Owner(ScopeTree tree, ScopeId scope) => tree.Display(scope, "").TrimEnd('.');

    /// <summary>
    /// Compares two source paths the way the import graph keys them.
    /// </summary>
    private static bool PathsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a type name written under an explicit scope qualifier.
    /// </summary>
    private NamedSpec ResolveScopedType(NamedSpec spec, ScopeTree tree, ScopeIndex index, ScopeId from, string file)
    {
        var path = spec.Scope!;
        var bare = spec with { Scope = null };
        if (ScopeFor(path, tree, from, file, spec.Span) is not { } scope) return Poisoned(bare);
        return NameIn(scope, spec.Name, [.. path], tree, index, file, spec.Span) is { } q
            ? bare with { Name = q }
            : Poisoned(bare);
    }

    /// <summary>
    /// Resolves a name written under an explicit scope qualifier in expression position, where the
    /// segments after it may be more scopes, then the name, then member accesses. The longest run
    /// that names scopes wins, so 'kernel.Algo.Min' reads as the realm's Algo and its member.
    /// </summary>
    private Expr ResolveScopedExpr(ScopedNameExpr sn, ScopeTree tree, ScopeIndex index, ScopeId from, string file)
    {
        if (sn.Generic is { } g)
        {
            var resolved = ResolveScopedType(g, tree, index, from, file);
            if (resolved.Name == NamedSpec.Poison) return Fallback(sn);
            Expr gen = new GenericTypeRefExpr(resolved.Name, resolved.Args, null, sn.Span);
            foreach (var m in sn.Path) gen = new MemberAccessExpr(gen, m, sn.Span);
            return gen;
        }

        var path = new List<string>(sn.Scope);
        var scope = ScopeId.Root;
        if (sn.Scope.Length > 0)
        {
            if (RealmScope(tree, sn.Scope[0]) is not { } realm) { ReportNoScope(path, file, sn.Span); return Fallback(sn); }
            scope = realm;
        }

        // Every segment but the last may still be a scope; the last can only be the name
        int i = 0;
        while (i < sn.Path.Length - 1 && tree.Child(scope, sn.Path[i]) is { } child)
        {
            scope = child;
            path.Add(sn.Path[i]);
            i++;
        }

        if (!Enclosing(scope, path, tree, from, file, sn.Span)) return Fallback(sn);
        if (NameIn(scope, sn.Path[i], path, tree, index, file, sn.Span) is not { } q) return Fallback(sn);

        Expr e = new IdentExpr(q, sn.Span);
        for (int m = i + 1; m < sn.Path.Length; m++) e = new MemberAccessExpr(e, sn.Path[m], sn.Span);
        return e;
    }

    /// <summary>
    /// What a rejected qualifier leaves behind. Poison rather than the bare name, so the one error
    /// already reported is not joined by a second about whatever the bare name happens to mean.
    /// </summary>
    private static NamedSpec Poisoned(NamedSpec spec) => spec with { Name = NamedSpec.Poison, Args = [] };

    /// <summary>
    /// Fallback is a poison expression, so the one error already reported is not joined by a second about
    /// whatever the bare name happens to mean.
    /// </summary>
    private static PoisonExpr Fallback(ScopedNameExpr sn) => new(sn.Span);

    /// <summary>
    /// The scope a written path names, or null once the reason it does not has been reported.
    /// </summary>
    private ScopeId? ScopeFor(string[] path, ScopeTree tree, ScopeId from, string file, TextSpan span)
    {
        var scope = ScopeId.Root;
        for (int i = 0; i < path.Length; i++)
        {
            var child = i == 0 ? RealmScope(tree, path[0]) : tree.Child(scope, path[i]);
            if (child is not { } found) { ReportNoScope([.. path[..(i + 1)]], file, span); return null; }
            scope = found;
        }
        return Enclosing(scope, [.. path], tree, from, file, span) ? scope : null;
    }

    /// <summary>
    /// The scope of a realm named in a qualifier. Interned, since both realms are part of the
    /// language rather than of any one program.
    /// </summary>
    private static ScopeId? RealmScope(ScopeTree tree, string name) => name switch
    {
        "kernel" => tree.Intern(ScopeId.Root, "kernel", Realm.Kernel),
        "userspace" => tree.Intern(ScopeId.Root, "userspace", Realm.User),
        _ => null,
    };

    /// <summary>
    /// Checks that a written qualifier names a scope this code is inside. Naming a sibling would
    /// make the qualifier a way to see into another process rather than a way to disambiguate.
    /// </summary>
    private bool Enclosing(ScopeId scope, List<string> path, ScopeTree tree, ScopeId from, string file, TextSpan span)
    {
        if (tree.Encloses(scope, from)) return true;
        if (!_badQualifier.Add((file, Spell(path), ""))) return false;
        diag.Error(Codes.ScopeNotEnclosing, file, span,
            $"'{Spell(path)}' does not enclose this code",
            ["a scope qualifier reaches outward only; name an enclosing realm or process, or '::' for the top level"]);
        return false;
    }

    /// <summary>
    /// The qualified name a scope declares, or null once the reason it declares none has been
    /// reported. Root names are never qualified, so there the written name is the answer.
    /// </summary>
    private string? NameIn(ScopeId scope, string name, List<string> path, ScopeTree tree, ScopeIndex index,
                           string file, TextSpan span)
    {
        if (scope.IsRoot)
        {
            if (_atRoot.ContainsKey(name)) return name;
        }
        else if (index.TryDeclared(scope, name) is { } q) return q;

        if (!_badQualifier.Add((file, Spell(path), name))) return null;
        diag.Error(Codes.UnknownInScope, file, span,
            $"{Where(path)} declares no '{name}'",
            [scope.IsRoot
                ? "the top level of the build declares it nowhere; check the spelling, or drop the '::'"
                : $"drop the qualifier to use whatever '{name}' is in scope here"]);
        return null;
    }

    /// <summary>
    /// Reports a qualifier naming a scope that does not exist in this build.
    /// </summary>
    private void ReportNoScope(List<string> path, string file, TextSpan span)
    {
        if (!_badQualifier.Add((file, Spell(path), ""))) return;
        diag.Error(Codes.ScopeNotEnclosing, file, span, $"there is no scope '{Spell(path)}'",
                   ["the only realms are 'kernel' and 'userspace'; a process is named inside one"]);
    }

    /// <summary>
    /// A written scope path, spelled the way it is typed. The root scope is '::'.
    /// </summary>
    private static string Spell(List<string> path) => path.Count == 0 ? "::" : string.Join('.', path);

    /// <summary>
    /// A scope path as a diagnostic names it.
    /// </summary>
    private static string Where(List<string> path) =>
        path.Count == 0 ? "the top level" : $"'{Spell(path)}'";

    /// <summary>
    /// Builds the name-to-qualified-type map visible from a scope. Reuses the Monomorphizer's
    /// substitution walker rather than adding a second one: binding 'Config' to 'Config@kernel' is
    /// the same operation as binding a type parameter, and that walker knows every type position.
    /// </summary>
    private Monomorphizer.SubstitutionContext SubstitutionFor(ScopeTree tree, ScopeIndex index, ScopeId scope, string file)
    {
        var specs = new Dictionary<string, TypeSpec>();
        var names = new Dictionary<string, string>();
        var cTypes = new Dictionary<string, string>();

        for (var s = scope; ; s = tree.Parent(s))
        {
            foreach (var (written, qualified) in index.DeclaredIn(s))
            {
                if (written == qualified || specs.ContainsKey(written)) continue;
                specs[written] = new NamedSpec(qualified);
                names[written] = qualified;
                cTypes[written] = Mangler.Class(qualified) + "*";
                Mangler.RegisterScopedName(qualified, tree.Display(s, written));
            }
            if (s.IsRoot) break;
        }

        return new Monomorphizer.SubstitutionContext(specs, cTypes)
        {
            RewriteTypeNames = true,
            NameMap = names,
            ScopedType = spec => ResolveScopedType(spec, tree, index, scope, file),
            ScopedExpr = sn => ResolveScopedExpr(sn, tree, index, scope, file),
        };
    }

    /// <summary>
    /// Rewrites one declaration: its own name, and every type it mentions.
    /// </summary>
    private TopLevel RewriteItem(TopLevel item, ScopeTree tree, ScopeIndex index, ScopeId scope,
                                 Monomorphizer.SubstitutionContext sub, string file)
    {
        string Q(string name) => index.Resolve(scope, name) ?? name;

        return item switch
        {
            ClassDecl cd => cd with
            {
                BaseName = Q(cd.BaseName),
                Name = Requalify(cd.Name, cd.BaseName, Q(cd.BaseName), cd.GenericParams),
                Members = Monomorphizer.SubMembers(cd.Members, sub),
            },
            UnionDecl ud => ud with
            {
                BaseName = Q(ud.BaseName),
                Name = Requalify(ud.Name, ud.BaseName, Q(ud.BaseName), ud.GenericParams),
                Variants = Monomorphizer.SubVariants(ud.Variants, sub),
            },
            EnumDecl ed => ed with { Name = Q(ed.Name) },
            NativeTypeDecl nd => nd with { Name = Q(nd.Name) },
            ProcessDecl pd => RewriteProcess(pd, tree, index, scope, file),
            FuncDecl fd => fd with
            {
                Name = Q(fd.Name),
                ReturnType = sub.SubType(fd.ReturnType),
                Params = Monomorphizer.SubParams(fd.Params, sub),
                Body = Monomorphizer.SubBody(fd.Body, fd.Params, sub),
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
    /// Requalifies a generic instantiation: its base, if the template itself is scoped, and each of
    /// its type arguments.
    /// </summary>
    private GenericUse RewriteUse(GenericUse use, ScopeIndex index, ScopeId scope, ScopeTree tree, string file)
    {
        string baseName = use.Scope != null
            ? ResolveScopedType(new NamedSpec(use.Base, use.Span) { Scope = use.Scope }, tree, index, scope, file).Name
            : index.Resolve(scope, use.Base) ?? use.Base;

        NamedSpec[]? specs = null;
        if (use.ArgSpecs != null)
            for (int i = 0; i < use.ArgSpecs.Length; i++)
            {
                var q = RewriteSpec(use.ArgSpecs[i], index, scope, tree, file);
                if (ReferenceEquals(q, use.ArgSpecs[i])) continue;
                specs ??= (NamedSpec[])use.ArgSpecs.Clone();
                specs[i] = q;
            }

        string[]? args = null;
        for (int i = 0; i < use.Args.Length; i++)
        {
            string q = specs != null && i < specs.Length ? specs[i].Mangled
                     : index.Resolve(scope, use.Args[i]) ?? use.Args[i];
            if (q == use.Args[i]) continue;
            args ??= (string[])use.Args.Clone();
            args[i] = q;
        }

        if (args == null && specs == null && baseName == use.Base) return use;
        return use with { Base = baseName, Args = args ?? use.Args, ArgSpecs = specs ?? use.ArgSpecs };
    }

    /// <summary>
    /// Requalifies a type argument, recursing through its own arguments. Structural throughout: a
    /// nested instantiation has no flat spelling any scope declares.
    /// </summary>
    private NamedSpec RewriteSpec(NamedSpec s, ScopeIndex index, ScopeId scope, ScopeTree tree, string file)
    {
        NamedSpec[]? args = null;
        for (int i = 0; i < s.Args.Length; i++)
        {
            var q = RewriteSpec(s.Args[i], index, scope, tree, file);
            if (ReferenceEquals(q, s.Args[i])) continue;
            args ??= (NamedSpec[])s.Args.Clone();
            args[i] = q;
        }

        // A qualified argument was already told which scope it means, so it is resolved rather than
        // looked up from here: 'Box[::Cargo]' inside a realm declaring Cargo means the root one.
        if (s.Scope != null)
        {
            var resolved = ResolveScopedType(s, tree, index, scope, file);
            return resolved with { Args = args ?? resolved.Args };
        }
        string name = index.Resolve(scope, s.Name) ?? s.Name;
        return args == null && name == s.Name ? s : s with { Name = name, Args = args ?? s.Args };
    }

    /// <summary>
    /// The declaration's internal name after its base is qualified. A non-generic declaration is
    /// just its base; a generic one recomposes through the same function every other pass uses to
    /// spell an instantiation, so the template and its stamps agree by construction.
    /// </summary>
    private static string Requalify(string name, string baseName, string qualBase, string[] generics)
    {
        if (generics.Length == 0) return qualBase;
        return name == baseName ? qualBase : Mangler.GenericInstance(qualBase, generics);
    }

    /// <summary>
    /// Rewrites a process: its own declarations and its threads, both under the process scope.
    /// </summary>
    private TopLevel RewriteProcess(ProcessDecl pd, ScopeTree tree, ScopeIndex index,
                                    ScopeId realmScope, string file)
    {
        // A repeat was already reported and declares nothing, so it is emptied rather than left to
        // stamp a second copy of every C symbol its twin already owns.
        if (_duplicates.Contains(pd)) return pd with { Items = [], Threads = [] };

        var proc = tree.Intern(realmScope, pd.Name, Realm.None);
        var sub = SubstitutionFor(tree, index, proc, file);

        var items = new TopLevel[pd.Items.Length];
        for (int i = 0; i < items.Length; i++) items[i] = RewriteItem(pd.Items[i], tree, index, proc, sub, file);

        return pd with { Items = items, Threads = RewriteThreads(pd.Threads, sub) };
    }

    /// <summary>
    /// Rewrites each thread's entry body, which is ordinary code running inside its process.
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
                    Body = SubEntryBlock(t.Entry, sub),
                },
            };
        }
        return result;
    }

    /// <summary>
    /// Rewrites a thread entry's body with its parameters bound, so a parameter named like a scoped
    /// type keeps meaning the parameter.
    /// </summary>
    private static Block SubEntryBlock(EntryFuncDecl entry, Monomorphizer.SubstitutionContext sub)
    {
        var body = Monomorphizer.SubBody(new BlockBody(entry.Body), entry.Params, sub);
        return ((BlockBody)body).Block;
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
