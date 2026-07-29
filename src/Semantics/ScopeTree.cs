namespace Appa;

internal readonly record struct ScopeId(int Value)
{
    public static readonly ScopeId Root = new(0);
    public bool IsRoot => Value == 0;
}

internal sealed class ScopeTree
{
    /// <summary>
    /// Suffix is precomputed at intern time rather than rebuilt per lookup, since qualification
    /// happens once per declaration and per type reference.
    /// </summary>
    private readonly record struct Node(ScopeId Parent, string Segment, Realm Realm, string Suffix);
    private readonly List<Node> _nodes = [new(default, "", Realm.None, "")];
    private readonly Dictionary<(int Parent, string Segment), ScopeId> _index = [];

    /// <summary>
    /// Returns the scope for a segment under a parent, creating it on first use. Interning means
    /// every 'realm userspace { }' block in the project, in whatever file, lands in one scope.
    /// </summary>
    public ScopeId Intern(ScopeId parent, string segment, Realm realm)
    {
        if (_index.TryGetValue((parent.Value, segment), out var existing)) return existing;
        string suffix = parent.IsRoot ? $"@{segment}" : $"{Suffix(parent)}${segment}";
        var id = new ScopeId(_nodes.Count);
        _nodes.Add(new Node(parent, segment, realm, suffix));
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
    public string Qualify(ScopeId s, string name) => s.IsRoot ? name : name + Suffix(s);

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
