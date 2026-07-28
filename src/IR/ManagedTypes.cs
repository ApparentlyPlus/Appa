namespace Appa;

internal sealed class ManagedTypes
{
    private readonly HashSet<string> _classes;
    private readonly HashSet<string> _unions;

    public ManagedTypes(IrModule m)
    {
        _classes = new HashSet<string>(m.Classes.Count);
        foreach (var c in m.Classes)
        {
            if (!c.IsModule) _classes.Add(c.Name);
        }

        _unions = [];
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var u in m.Unions)
            {
                if (_unions.Contains(u.Name)) continue;
                if (HoldsManaged(u)) changed |= _unions.Add(u.Name);
            }
        }
    }

    /// <summary>
    /// Returns true if any variant of the union stores a value that is managed under the
    /// classification built so far.
    /// </summary>
    private bool HoldsManaged(IrUnion u)
    {
        foreach (var v in u.Variants)
            foreach (var f in v.Fields)
                if (IsManaged(f.Type)) return true;
        return false;
    }

    /// <summary>
    /// True if values of this type carry reference counts the compiler maintains. Fixed arrays are
    /// excluded even with a managed element type - an array is raw storage the author counts by
    /// hand, and managing it here would be a feature, not a consistency fix.
    /// </summary>
    public bool IsManaged(IrType t)
    {
        return t switch
        {
            IrClassRef cr => _classes.Contains(cr.ClassName),
            IrUnionType ut => _unions.Contains(ut.Name),
            _ => false
        };
    }

    /// <summary>
    /// Returns true if the named union stores managed values and therefore needs a generated
    /// retain/release pair.
    /// </summary>
    public bool IsManagedUnion(string name) => _unions.Contains(name);
}
