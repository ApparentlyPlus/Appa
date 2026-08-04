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

        var holders = new Dictionary<string, List<string>>();
        var work = new Queue<string>();

        foreach (var u in m.Unions)
        {
            bool managed = false;
            foreach (var v in u.Variants)
                foreach (var f in v.Fields)
                {
                    if (f.Type is IrClassRef cr && _classes.Contains(cr.ClassName)) managed = true;
                    else if (f.Type is IrUnionType ut)
                    {
                        if (!holders.TryGetValue(ut.Name, out var up)) holders[ut.Name] = up = [];
                        up.Add(u.Name);
                    }
                }
            if (managed && _unions.Add(u.Name)) work.Enqueue(u.Name);
        }

        while (work.Count > 0)
            if (holders.TryGetValue(work.Dequeue(), out var up))
                foreach (var h in up)
                    if (_unions.Add(h)) work.Enqueue(h);
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
