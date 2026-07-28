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
    private readonly Dictionary<(ScopeId Scope, string Name), string> _decls = [];
    private readonly Dictionary<string, ScopeId> _declScopeOf = [];

    /// <summary>True when any declaration at all lives outside the root scope.</summary>
    public bool HasScopedDeclarations => _declScopeOf.Count > 0;

    /// <summary>
    /// Records that <paramref name="scope"/> declares <paramref name="name"/>, which is globally
    /// known as <paramref name="qualified"/>.
    /// </summary>
    public void Declare(ScopeId scope, string name, string qualified)
    {
        _decls[(scope, name)] = qualified;
        if (!scope.IsRoot) _declScopeOf[qualified] = scope;
    }

    /// <summary>
    /// Resolves a written name as seen from <paramref name="from"/>, walking outward. Returns null
    /// when no enclosing scope declares it, which means the name is an ordinary root-scope name and
    /// every existing lookup path applies unchanged.
    /// </summary>
    public string? Resolve(ScopeId from, string written)
    {
        for (var s = from; ; s = tree.Parent(s))
        {
            if (_decls.TryGetValue((s, written), out var qualified)) return qualified;
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
    /// Interns every realm and process scope and records what each declares.
    /// </summary>
    public ScopeBindResult Bind(List<(string path, Program prog)> programs)
    {
        _ = diag; // reserved: scope-level diagnostics arrive with the rewrite sweep
        var tree = new ScopeTree();
        var index = new ScopeIndex(tree);

        foreach (var (_, prog) in programs)
            foreach (var item in prog.Items)
                if (item is ContextDecl realm && realm.Kind != Realm.None)
                {
                    // Realm scopes are project-global: every 'realm userspace { }' block, in any
                    // file, interns to the same scope, so the realm is one namespace rather than
                    // one per file.
                    var scope = tree.Intern(ScopeId.Root, NameOf(realm.Kind), realm.Kind);
                    foreach (var inner in realm.Items) DeclareItem(tree, index, scope, inner);
                }

        return new ScopeBindResult(tree, index);
    }

    /// <summary>
    /// Records a single declaration's name in its scope. Anything that is not a named declaration -
    /// a native block, an import - contributes no name and is skipped.
    /// </summary>
    private static void DeclareItem(ScopeTree tree, ScopeIndex index, ScopeId scope, TopLevel item)
    {
        string? name = item switch
        {
            ClassDecl cd => cd.Name,
            UnionDecl ud => ud.Name,
            EnumDecl ed => ed.Name,
            NativeTypeDecl nd => nd.Name,
            FuncDecl fd => fd.Name,
            _ => null,
        };
        // An extern function names a C symbol that already exists under exactly that spelling, so
        // qualifying it would rename something the compiler does not own.
        if (name != null) index.Declare(scope, name, tree.Qualify(scope, name));
    }

    /// <summary>
    /// The scope segment naming a realm. Matches the source keyword, so a qualified name and a
    /// diagnostic read the same way the user wrote it.
    /// </summary>
    public static string NameOf(Realm r) => r switch
    {
        Realm.Kernel => "kernel",
        Realm.User => "userspace",
        _ => "",
    };
}
