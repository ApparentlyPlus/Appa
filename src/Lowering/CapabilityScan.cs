namespace Appa;

/// <summary>
/// Capability inference pass that walks the call graph from all program entry points
/// and records which OS capabilities the program requires.
/// Results become -D macros so GatOS can drop subsystems the program never uses.
/// MEM: any reachable new expression or call to the heap allocator (_env_alloc).
/// INPUT: any reachable call to _env_read.
/// THREADS: the module declares at least one process or thread.
/// </summary>
internal sealed class CapabilityScan(IrModule m) : IrWalker
{
    public bool Mem;
    public bool Input;
    public bool Threads;

    private readonly Dictionary<string, IrFunction> _funcs = new(GetFuncsCapacity(m));
    private readonly Dictionary<string, IrOperator> _ops = new(GetOpsCapacity(m));
    private readonly HashSet<string> _seen = [];
    private readonly Queue<IrStmt> _work = new();

    /// <summary>
    /// Calculates the number of functions in the module to preallocate the dictionary.
    /// </summary>
    private static int GetFuncsCapacity(IrModule m)
    {
        int methodCount = 0;
        for (int i = 0; i < m.Classes.Count; i++)
            methodCount += m.Classes[i].Methods.Count;
        return methodCount + m.FreeFunctions.Count;
    }

    /// <summary>
    /// Calculates the number of operators in the module to preallocate the dictionary.
    /// </summary>
    private static int GetOpsCapacity(IrModule m)
    {
        int opCount = 0;
        for (int i = 0; i < m.Classes.Count; i++)
            opCount += m.Classes[i].Operators.Count;
        return opCount;
    }

    /// <summary>
    /// Runs the capability scan from all entry points and returns this instance with flags populated.
    /// </summary>
    public CapabilityScan Run()
    {
        foreach (var c in m.Classes)
        {
            foreach (var mm in c.Methods) _funcs[mm.CName] = mm;
            foreach (var o in c.Operators) _ops[o.CName] = o;
        }
        foreach (var f in m.FreeFunctions) _funcs[f.CName] = f;

        Threads = m.Processes.Count > 0;

        foreach (var f in m.FreeFunctions) if (f.IsEntry) Enter(f.CName, f.Body);
        foreach (var p in m.Processes)
            foreach (var t in p.Threads)
                if (t.EntryFunc is { } e) Enter(e.CName, e.Body);

        while (_work.Count > 0) WalkStmt(_work.Dequeue());
        return this;
    }

    /// <summary>
    /// Enqueues a function body for walking if it has not been visited yet.
    /// </summary>
    private void Enter(string cname, IrStmt? body)
    {
        if (body != null && _seen.Add(cname)) _work.Enqueue(body);
    }

    /// <summary>
    /// Records capabilities implied by calling a given C name and follows into known function bodies.
    /// </summary>
    private void Call(string cname)
    {
        if (cname == "_env_read") Input = true;
        if (cname == "_env_alloc") Mem = true;
        if (_funcs.TryGetValue(cname, out var f)) Enter(cname, f.Body);
        else if (_ops.TryGetValue(cname, out var o)) Enter(cname, o.Body);
    }

    /// <summary>
    /// Handles for-in statements, which carry implicit len and get calls not visible in the expression tree.
    /// </summary>
    protected override void WalkStmt(IrStmt s)
    {
        if (s is IrForIn fi) { Call(fi.LenCName); Call(fi.GetCName); }
        base.WalkStmt(s);
    }

    /// <summary>
    /// Handles expressions that have capability-specific meaning, then defers to the base structural walk.
    /// </summary>
    protected override void WalkExpr(IrExpr e)
    {
        switch (e)
        {
            case IrNew: Mem = true; break;
            case IrNewInit ni: Mem = true; Call(ni.AddCName); break;
            case IrStaticCall sc: Call(sc.CName); break;
            case IrInstanceCall ic: Call(ic.CName); break;
            case IrThrowsCall tc: Call(tc.CName); break;
            case IrThrowsInstanceCall ti: Call(ti.CName); break;
            // A function used as a value is conservatively treated as reachable even
            // when this walk cannot determine whether or when it is actually called.
            case IrFuncRef fr: Call(fr.CName); break;
        }
        base.WalkExpr(e);
    }
}
