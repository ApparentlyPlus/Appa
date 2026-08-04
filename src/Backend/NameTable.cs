namespace Appa;

/// <summary>
/// Every name one compilation invents, in one object with that compilation's lifetime. Replaces a
/// row of independent static registries that each had to be cleared by hand, and whose leak between
/// builds an entire test file existed to disprove.
/// </summary>
internal sealed class NameTable
{
    // The scope tree of the build: where a qualified name's structure lives.
    public ScopeTree? Scopes { get; set; }

    // Dense naming. Populated by the Densifier after reachability.
    public Dictionary<string, string> Dense { get; private set; } = [];

    // What each IR type spells itself as under the current naming.
    public Dictionary<IrType, string> CTypes { get; private set; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Adopts the dense name map the Densifier produced, dropping the spellings it supersedes.
    /// </summary>
    public void SetDense(Dictionary<string, string> map)
    {
        Dense = map;
        CTypes = new Dictionary<IrType, string>(ReferenceEqualityComparer.Instance);
    }

    // Instantiations the Monomorphizer stamped, which is the set that actually exists.
    public Dictionary<string, GenericKey> Stamped { get; } = [];

    // Stamped instances bucketed by base name, each list kept ordinally sorted.
    public Dictionary<string, List<string>> StampedByBase { get; } = [];

    // Base names of every generic template seen, whether or not anything instantiated them.
    public HashSet<string> Templates { get; } = [];

    // Instantiations the Monomorphizer rejected and therefore never stamped.
    public HashSet<string> Failed { get; } = [];

    // Every instance name GenericInstance ever composed, stamped or not: what a flat spelling means,
    // as opposed to what the build stamped.
    public Dictionary<string, GenericKey> Composed { get; } = [];

    /// <summary>
    /// Drops what one front-end round decided, for the round replacing it. A round starts from the
    /// unstamped programs again; what a name means does not change between them.
    /// </summary>
    public void BeginRound()
    {
        Scopes = null;
        SetDense([]);
        Stamped.Clear();
        StampedByBase.Clear();
        Templates.Clear();
        Failed.Clear();
    }

    /// <summary>
    /// Records a stamped instance under its base name, keeping the bucket ordinally sorted.
    /// </summary>
    public void AddStamped(string mangled, GenericKey key)
    {
        if (!Stamped.TryAdd(mangled, key)) return;
        if (!StampedByBase.TryGetValue(key.Base, out var found)) StampedByBase[key.Base] = found = [];
        int at = found.BinarySearch(mangled, StringComparer.Ordinal);
        found.Insert(at < 0 ? ~at : at, mangled);
    }
}
