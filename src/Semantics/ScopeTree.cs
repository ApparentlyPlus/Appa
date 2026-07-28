namespace Appa;

/// <summary>
/// A declaration scope, interned into a <see cref="ScopeTree"/>. Deliberately a 4-byte struct: it
/// is a dictionary key on every name lookup, so it must hash and compare without touching a string.
/// </summary>
internal readonly record struct ScopeId(int Value)
{
    /// <summary>The file/import scope every program starts in. Names here are unqualified.</summary>
    public static readonly ScopeId Root = new(0);

    public bool IsRoot => Value == 0;
}

/// <summary>
/// The tree of declaration scopes in a program: root (imports) -> realm -> process. Three levels,
/// each corresponding to something real. A realm is a translation unit; a process is an address
/// space, which is exactly the boundary across which threads share memory.
///
/// This is the *name* axis, and it is deliberately separate from the *visibility* axis carried by
/// <see cref="Realm"/>. A process contributes a segment to a name's scope path but inherits its
/// realm's visibility, so <see cref="RealmOf"/> walks to the nearest enclosing realm rather than
/// reading the node itself.
/// </summary>
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

        // '@' and '$' cannot appear in a Gata identifier, so a qualified name can never collide
        // with one a user wrote by hand.
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
        for (var cur = s; !cur.IsRoot; cur = Parent(cur)) parts.Add(Segment(cur));
        parts.Reverse();
        return string.Join('.', parts) + "." + name;
    }
}
