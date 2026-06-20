namespace Appa;

/// <summary>
/// Reachability dead-code elimination pass that walks the call and type graph from all entry
/// points and drops any class or free function that nothing reachable references.
/// Runs after Desugar and before Ownership. Runtime roles the emitter inserts are seeded as roots.
/// Classes and free functions marked @keep are exempt regardless of reachability.
/// </summary>
internal sealed class Dce(IrModule m) : IrWalker
{
    // Unit identity: a class by its Gata name, a free function tagged "fn:"+cname.
    private static string Fn(string cname) => "fn:" + cname;

    private readonly Dictionary<string, string> _unitOf = new();
    private readonly Dictionary<string, IrClass> _classes = new();
    private readonly Dictionary<string, IrFunction> _funcs = new();
    private readonly HashSet<string> _live = [];
    private readonly Queue<string> _work = new();

    private void Root(string unit) { if (_live.Add(unit)) _work.Enqueue(unit); }
    private void Ref(string cname) { if (_unitOf.TryGetValue(cname, out var u)) Root(u); }

    /// <summary>
    /// Runs the DCE pass and returns a new IrModule containing only reachable classes and free functions.
    /// </summary>
    public IrModule Run()
    {
        foreach (var c in m.Classes)
        {
            _classes[c.Name] = c;
            foreach (var mm in c.Methods) _unitOf[mm.CName] = c.Name;
            foreach (var o in c.Operators) _unitOf[o.CName] = c.Name;
        }
        foreach (var f in m.FreeFunctions) { _unitOf[f.CName] = Fn(f.CName); _funcs[f.CName] = f; }

        foreach (var f in m.FreeFunctions) if (f.IsEntry) Root(Fn(f.CName));
        foreach (var p in m.Processes)
            foreach (var t in p.Threads)
                if (t.EntryFunc is { } e) MarkFunc(e);
        foreach (var role in new[] { Roles.Alloc, Roles.Retain, Roles.Release, Roles.ObjInit })
            if (m.Symbols.IntrinsicOrNull(role) is { } cn) Ref(cn);

        // @keep is the explicit escape hatch for symbols reachable only through native text.
        foreach (var c in m.Classes) if (c.Keep) Root(c.Name);
        foreach (var f in m.FreeFunctions) if (f.Annotations.Any(a => a is KeepAnnotation)) Root(Fn(f.CName));

        while (_work.Count > 0) ScanUnit(_work.Dequeue());

        return m with
        {
            Classes = m.Classes.Where(c => _live.Contains(c.Name)).ToList(),
            FreeFunctions = m.FreeFunctions.Where(f => f.IsEntry || _live.Contains(Fn(f.CName))).ToList(),
        };
    }

    /// <summary>
    /// Dispatches a live unit token to MarkClass or MarkFunc based on its prefix.
    /// </summary>
    private void ScanUnit(string unit)
    {
        if (unit.StartsWith("fn:")) { if (_funcs.TryGetValue(unit[3..], out var f)) MarkFunc(f); return; }
        if (_classes.TryGetValue(unit, out var c)) MarkClass(c);
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
            case IrClassRef cr: Root(cr.ClassName); break;
            case IrPtrType p: MarkType(p.Inner); break;
            case IrArrayType a: MarkType(a.Elem); break;
            case IrResultType r: MarkType(r.Inner); break;
            case IrFuncPtrType f: MarkType(f.Ret); foreach (var p2 in f.Params) MarkType(p2); break;
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
            case IrNew n: Root(n.ClassName); break;
            case IrNewInit ni: Root(ni.ClassName); Ref(ni.AddCName); break;
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
