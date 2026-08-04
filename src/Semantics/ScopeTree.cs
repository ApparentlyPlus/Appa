namespace Appa;

internal readonly record struct ScopeId(int Value)
{
    public static readonly ScopeId Root = new(0);
    public bool IsRoot => Value == 0;
}

/// <summary>
/// A declaration's identity: the scope it was written in and the name it was written as.
/// </summary>
internal readonly record struct QualifiedName(ScopeId Scope, string Name);

internal sealed class ScopeTree
{
    /// <summary>
    /// Suffix is precomputed at intern time rather than rebuilt per lookup, since qualification
    /// happens once per declaration and per type reference. Token is its C-safe rendering, which
    /// costs the same to precompute and saves a hash per emitted name.
    /// </summary>
    private readonly record struct Node(ScopeId Parent, string Segment, Realm Realm, string Suffix, string Token);

    // The root scope is always present, and has no parent, no segment, no realm, and no suffix.
    private readonly List<Node> _nodes = [new(default, "", Realm.None, "", "")];
    private readonly Dictionary<(int Parent, string Segment), ScopeId> _index = [];

    // What each qualified name was composed from, and which scopes declare a given bare name. 
    private readonly Dictionary<string, QualifiedName> _qualified = [];
    private readonly Dictionary<string, List<ScopeId>> _byBare = [];

    // What each scope-qualified name was declared as: "a type", "a function", "a generic type".
    private readonly Dictionary<string, string> _kind = [];

    /// <summary>
    /// Returns the scope for a segment under a parent, creating it on first use. Interning means
    /// every 'realm userspace { }' block in the project, in whatever file, lands in one scope.
    /// </summary>
    public ScopeId Intern(ScopeId parent, string segment, Realm realm)
    {
        if (_index.TryGetValue((parent.Value, segment), out var existing)) return existing;
        string suffix = parent.IsRoot ? $"@{segment}" : $"{Suffix(parent)}${segment}";
        var id = new ScopeId(_nodes.Count);
        _nodes.Add(new Node(parent, segment, realm, suffix, "_s" + Mangler.Hash(suffix)));
        _index[(parent.Value, segment)] = id;
        return id;
    }

    public ScopeId Parent(ScopeId s) => _nodes[s.Value].Parent;

    public string Segment(ScopeId s) => _nodes[s.Value].Segment;

    /// <summary>
    /// The mangling suffix for a scope: "" at root, "@kernel" for a realm, "@kernel$P" for a
    /// process inside one.
    /// </summary>
    public string Suffix(ScopeId s) => _nodes[s.Value].Suffix;

    /// <summary>
    /// The realm a scope belongs to, walking outward. A process inherits it rather than declaring
    /// one, which is what keeps the name axis and the visibility axis independent.
    /// </summary>
    public Realm RealmOf(ScopeId s)
    {
        while (!s.IsRoot)
        {
            var n = _nodes[s.Value];
            if (n.Realm != Realm.None) return n.Realm;
            s = n.Parent;
        }
        return Realm.None;
    }

    /// <summary>
    /// The globally unique name a declaration written as <paramref name="name"/> in this scope gets.
    /// Root-scope names are returned unchanged, so a program using no realm-scoped declarations
    /// produces byte-identical output to one compiled before scopes existed.
    /// </summary>
    public string Qualify(ScopeId s, string name)
    {
        if (s.IsRoot) return name;
        string q = name + Suffix(s);
        _qualified[q] = new QualifiedName(s, name);
        if (!_byBare.TryGetValue(name, out var scopes)) _byBare[name] = scopes = [];
        if (!scopes.Contains(s)) scopes.Add(s);
        return q;
    }

    /// <summary>
    /// The scope and written name a qualified spelling was composed from, for the passes that meet
    /// it flat. False for an ordinary root-scope name, which is its own written form.
    /// </summary>
    public bool TryUnqualify(string qualified, out QualifiedName qn) => _qualified.TryGetValue(qualified, out qn);

    /// <summary>
    /// The C-safe token standing in for a scope's suffix, precomputed at intern time.
    /// </summary>
    public string Token(ScopeId s) => _nodes[s.Value].Token;

    /// <summary>
    /// Records what kind of declaration a scope-qualified name refers to.
    /// </summary>
    public void SetKind(string qualified, string kind) => _kind[qualified] = kind;

    /// <summary>
    /// What a scope-qualified name was declared as, or null when nothing scoped declares it.
    /// </summary>
    public string? KindOf(string qualified) => _kind.GetValueOrDefault(qualified);

    /// <summary>
    /// The readable paths of every scope declaring this bare name, ordinally sorted. Empty when
    /// nothing scoped declares it, which is the ordinary case. Sorted because these reach the user,
    /// and declaration order is not something a diagnostic should expose.
    /// </summary>
    public List<string> Candidates(string bare)
    {
        if (!_byBare.TryGetValue(bare, out var scopes)) return [];
        var paths = new List<string>(scopes.Count);
        foreach (var s in scopes) paths.Add(Display(s, bare));
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>
    /// The readable, fully-qualified form for diagnostics: "kernel.P1.Config". Never the raw
    /// suffixed name, which must not reach a user.
    /// </summary>
    public string Display(ScopeId s, string name)
    {
        if (s.IsRoot) return name;
        var parts = new List<string>();
        for (var cur = s; !cur.IsRoot; cur = Parent(cur))
        {
            parts.Add(_nodes[cur.Value].Segment);
        }
        parts.Reverse();
        return string.Join('.', parts) + "." + name;
    }

    /// <summary>
    /// The child scope for a segment, or null when this scope has no such child. Lookup only: a
    /// written qualifier must not bring a scope into existence.
    /// </summary>
    public ScopeId? Child(ScopeId parent, string segment) =>
        _index.TryGetValue((parent.Value, segment), out var id) ? id : null;

    /// <summary>
    /// True when <paramref name="outer"/> is <paramref name="inner"/> or encloses it. The whole
    /// visibility rule for a written qualifier: outward is a disambiguator, inward would be a new
    /// way to see into a sibling.
    /// </summary>
    public bool Encloses(ScopeId outer, ScopeId inner)
    {
        for (var s = inner; ; s = Parent(s))
        {
            if (s == outer) return true;
            if (s.IsRoot) return false;
        }
    }
}
