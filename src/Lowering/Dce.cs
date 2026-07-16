namespace Appa;

/// <summary>
/// Reachability dead-code elimination pass that walks the call and type graph from all entry
/// points and drops any class or free function that nothing reachable references.
/// Runs after Desugar and before Ownership. Runtime roles the emitter inserts are seeded as roots.
/// Classes and free functions marked @keep are exempt regardless of reachability.
/// </summary>
internal sealed class Dce(IrModule m) : IrWalker
{
    private readonly record struct UnitKey(string Name, bool IsFunction);

    private readonly Dictionary<string, UnitKey> _unitOf = new(GetUnitOfCapacity(m));
    private readonly Dictionary<string, IrClass> _classes = new(m.Classes.Count);
    private readonly Dictionary<string, IrFunction> _funcs = new(m.FreeFunctions.Count);
    private readonly HashSet<UnitKey> _live = [];
    private readonly Queue<UnitKey> _work = new();

    // Composite types actually referenced by reachable code, keyed by mangled name.
    // ArrayTypes/FuncPtrTypes collected during resolution include entries from since-
    // dropped functions. Only the live ones get their C typedef emitted.
    private readonly HashSet<string> _liveComposites = [];

    private static int GetUnitOfCapacity(IrModule m)
    {
        int count = m.FreeFunctions.Count;
        for (int i = 0; i < m.Classes.Count; i++)
            count += m.Classes[i].Methods.Count + m.Classes[i].Operators.Count;
        return count;
    }

    private void Root(UnitKey unit) { if (_live.Add(unit)) _work.Enqueue(unit); }
    private void Ref(string cname) { if (_unitOf.TryGetValue(cname, out var u)) Root(u); }

    /// <summary>
    /// Runs the DCE pass and returns a new IrModule containing only reachable classes and free functions.
    /// </summary>
    public IrModule Run()
    {
        foreach (var c in m.Classes)
        {
            _classes[c.Name] = c;
            var classKey = new UnitKey(c.Name, false);
            foreach (var mm in c.Methods) _unitOf[mm.CName] = classKey;
            foreach (var o in c.Operators) _unitOf[o.CName] = classKey;
        }
        foreach (var f in m.FreeFunctions)
        {
            _unitOf[f.CName] = new UnitKey(f.CName, true);
            _funcs[f.CName] = f;
        }

        foreach (var f in m.FreeFunctions) if (f.IsEntry) Root(new UnitKey(f.CName, true));
        foreach (var p in m.Processes)
            foreach (var t in p.Threads)
                if (t.EntryFunc is { } e) MarkFunc(e);
        foreach (var role in new[] { Roles.Alloc, Roles.Retain, Roles.Release, Roles.ObjInit })
            if (m.Symbols.IntrinsicOrNull(role) is { } cn) Ref(cn);

        // @keep is the explicit escape hatch for symbols reachable only through native text.
        foreach (var c in m.Classes) if (c.Keep) Root(new UnitKey(c.Name, false));
        foreach (var f in m.FreeFunctions) if (f.Annotations.Any(a => a is KeepAnnotation)) Root(new UnitKey(f.CName, true));

        while (_work.Count > 0) ScanUnit(_work.Dequeue());

        return m with
        {
            Classes = [.. m.Classes.Where(c => _live.Contains(new UnitKey(c.Name, false)))],
            FreeFunctions = [.. m.FreeFunctions.Where(f => f.IsEntry || _live.Contains(new UnitKey(f.CName, true)))],
            ArrayTypes = [.. m.ArrayTypes.Where(a => _liveComposites.Contains(a.MangledName))],
            FuncPtrTypes = [.. m.FuncPtrTypes.Where(f => _liveComposites.Contains(f.MangledName))],
        };
    }

    /// <summary>
    /// Dispatches a live unit token to MarkClass or MarkFunc based on its prefix.
    /// </summary>
    private void ScanUnit(UnitKey unit)
    {
        if (unit.IsFunction) { if (_funcs.TryGetValue(unit.Name, out var f)) MarkFunc(f); return; }
        if (_classes.TryGetValue(unit.Name, out var c)) MarkClass(c);
    }

    /// <summary>
    /// Marks all field types, field initializer expressions, methods, and operators of a class as reachable.
    /// </summary>
    private void MarkClass(IrClass c)
    {
        foreach (var f in c.Fields) MarkType(f.Type);
        foreach (var e in c.FieldInits.Values) WalkExpr(e);
        foreach (var mm in c.Methods) MarkFunc(mm);
        foreach (var o in c.Operators) MarkOperator(o);
    }

    /// <summary>
    /// Marks a function's return type, parameter types, and body statements as reachable.
    /// </summary>
    private void MarkFunc(IrFunction f)
    {
        MarkType(f.ReturnType);
        foreach (var p in f.Params) MarkType(p.Type);
        if (f.Body != null) WalkStmt(f.Body);
    }

    /// <summary>
    /// Marks an operator's return type, parameter types, and body statements as reachable.
    /// </summary>
    private void MarkOperator(IrOperator o)
    {
        MarkType(o.ReturnType);
        foreach (var p in o.Params) MarkType(p.Type);
        if (o.Body != null) WalkStmt(o.Body);
    }

    /// <summary>
    /// Roots all units that a type reference transitively depends on.
    /// </summary>
    private void MarkType(IrType t)
    {
        switch (t)
        {
            case IrClassRef cr: Root(new UnitKey(cr.ClassName, false)); break;
            case IrPtrType p: MarkType(p.Inner); break;
            case IrArrayType a: _liveComposites.Add(a.MangledName); MarkType(a.Elem); break;
            case IrResultType r: MarkType(r.Inner); break;
            case IrFuncPtrType f:
                _liveComposites.Add(f.MangledName);
                MarkType(f.Ret);
                foreach (var p2 in f.Params) MarkType(p2);
                break;
        }
    }

    /// <summary>
    /// Handles statements with DCE-specific meaning, then defers to the base structural walk.
    /// </summary>
    protected override void WalkStmt(IrStmt s)
    {
        switch (s)
        {
            case IrDeclVar d: MarkType(d.Type); break;
            case IrForIn fi: Ref(fi.LenCName); Ref(fi.GetCName); MarkType(fi.ElemType); break;
        }
        base.WalkStmt(s);
    }

    /// <summary>
    /// Handles expressions with DCE-specific meaning, then defers to the base structural walk.
    /// </summary>
    protected override void WalkExpr(IrExpr e)
    {
        switch (e)
        {
            case IrStaticCall sc: Ref(sc.CName); break;
            case IrInstanceCall ic: Ref(ic.CName); break;
            case IrThrowsCall tc: Ref(tc.CName); break;
            case IrThrowsInstanceCall ti: Ref(ti.CName); break;
            case IrNew n: Root(new UnitKey(n.ClassName, false)); break;
            case IrNewInit ni: Root(new UnitKey(ni.ClassName, false)); Ref(ni.AddCName); break;
            case IrCast c: MarkType(c.To); break;
            case IrArrayLit al: MarkType(al.ArrType); break;
            case IrSizeof so: MarkType(so.Of); break;
            case IrDefault df: MarkType(df.Of); break;
            // A function used only as a value must still be kept so DCE does not drop
            // a symbol that a callback registration hands out as a pointer.
            case IrFuncRef fr: Ref(fr.CName); break;
        }
        base.WalkExpr(e);
    }
}
