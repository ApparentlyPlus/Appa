namespace Appa;

using System.Runtime.InteropServices;

// Base for IR->IR lowering passes. By default it deep-copies the IR, recursing into
// every expression and statement; a pass overrides RewriteExpr / RewriteStmt to
// transform specific nodes (call base first to rewrite children). Spans and types
// ride along through record copies.
/// <summary>
/// Abstract base for IR-to-IR lowering passes.
/// Default implementation is an identity transform; subclasses override RewriteExpr or RewriteStmt
/// to transform specific node kinds.
/// </summary>
internal abstract class IrRewriter
{
    /// <summary>
    /// Runs the rewriter over an entire module, rewriting all classes, free functions, and processes.
    /// </summary>
    public IrModule Run(IrModule m)
    {
        var classes = MapClasses(m.Classes);
        var freeFunctions = MapFunctions(m.FreeFunctions);
        var processes = MapProcesses(m.Processes);

        return ReferenceEquals(classes, m.Classes) &&
               ReferenceEquals(freeFunctions, m.FreeFunctions) &&
               ReferenceEquals(processes, m.Processes)
            ? m
            : m with { Classes = classes, FreeFunctions = freeFunctions, Processes = processes };
    }

    /// <summary>
    /// Rewrites all fields, methods, operators, and field initializers of a class.
    /// </summary>
    private IrClass RewriteClass(IrClass c)
    {
        var fields = MapFields(c.Fields);
        var methods = MapFunctions(c.Methods);
        var operators = MapOperators(c.Operators);
        var fieldInits = MapFieldInits(c.FieldInits);

        return ReferenceEquals(fields, c.Fields) &&
               ReferenceEquals(methods, c.Methods) &&
               ReferenceEquals(operators, c.Operators) &&
               ReferenceEquals(fieldInits, c.FieldInits)
            ? c
            : c with { Fields = fields, Methods = methods, Operators = operators, FieldInits = fieldInits };
    }

    /// <summary>
    /// Rewrites a function body; returns the function unchanged if it is native.
    /// </summary>
    private IrFunction RewriteFunction(IrFunction f)
    {
        if (f.Body == null) return f;
        var body = RewriteBlock(f.Body);
        return ReferenceEquals(body, f.Body) ? f : f with { Body = body };
    }

    /// <summary>
    /// Rewrites an operator body; returns the operator unchanged if it is native.
    /// </summary>
    private IrOperator RewriteOperator(IrOperator o)
    {
        if (o.Body == null) return o;
        var body = RewriteBlock(o.Body);
        return ReferenceEquals(body, o.Body) ? o : o with { Body = body };
    }

    /// <summary>
    /// Rewrites the entry function of each thread within a process.
    /// </summary>
    private IrProcess RewriteProcess(IrProcess p)
    {
        var threads = MapThreads(p.Threads);
        return ReferenceEquals(threads, p.Threads) ? p : p with { Threads = threads };
    }

    /// <summary>
    /// Rewrites a block by delegating to RewriteStmt and casting the result.
    /// </summary>
    private IrBlock RewriteBlock(IrBlock b)
    {
        return (IrBlock)RewriteStmt(b);
    }

    // Override points
    /// <summary>
    /// Override to transform specific expression kinds. Default delegates to MapExpr.
    /// </summary>
    protected virtual IrExpr RewriteExpr(IrExpr e)
    {
        return MapExpr(e);
    }

    /// <summary>
    /// Override to transform specific statement kinds. Default delegates to MapStmt.
    /// </summary>
    protected virtual IrStmt RewriteStmt(IrStmt s)
    {
        return MapStmt(s);
    }

    // Default structural recursion
    /// <summary>
    /// Structurally recurses into an expression, rewriting all child expressions.
    /// </summary>
    protected IrExpr MapExpr(IrExpr e)
    {
        return e switch
        {
            IrFieldLoad fl => UpdateFieldLoad(fl),
            IrIndex ix => UpdateIndex(ix),
            IrStaticCall sc => UpdateStaticCall(sc),
            IrInstanceCall ic => UpdateInstanceCall(ic),
            IrThrowsCall tc => UpdateThrowsCall(tc),
            IrThrowsInstanceCall ti => UpdateThrowsInstanceCall(ti),
            IrBinOp b => UpdateBinOp(b),
            IrTernary t => UpdateTernary(t),
            IrUnaryOp u => UpdateUnaryOp(u),
            IrPostfix p => UpdatePostfix(p),
            IrCast c => UpdateCast(c),
            IrNew n => UpdateNew(n),
            IrNewInit ni => UpdateNewInit(ni),
            IrArrayLit al => UpdateArrayLit(al),
            IrInterp ip => UpdateInterp(ip),
            IrAddrOf a => UpdateAddrOf(a),
            IrDeref d => UpdateDeref(d),
            IrIndirectCall ic2 => UpdateIndirectCall(ic2),
            IrUnionConstruct uc => UpdateUnionConstruct(uc),
            IrUnionField uf => UpdateUnionField(uf),
            _ => e   // literals, IrVar, IrSelfExpr, IrFuncRef
        };
    }

    /// <summary>
    /// Rewrites the object child of an IrFieldLoad; returns the original if unchanged.
    /// </summary>
    private IrFieldLoad UpdateFieldLoad(IrFieldLoad fl)
    {
        var obj = RewriteExpr(fl.Obj);
        return ReferenceEquals(obj, fl.Obj) ? fl : fl with { Obj = obj };
    }

    /// <summary>
    /// Rewrites the object and index children of an IrIndex; returns the original if unchanged.
    /// </summary>
    private IrIndex UpdateIndex(IrIndex ix)
    {
        var obj = RewriteExpr(ix.Obj);
        var idx = RewriteExpr(ix.Idx);
        return ReferenceEquals(obj, ix.Obj) && ReferenceEquals(idx, ix.Idx) ? ix : ix with { Obj = obj, Idx = idx };
    }

    /// <summary>
    /// Rewrites the argument list of an IrStaticCall; returns the original if unchanged.
    /// </summary>
    private IrStaticCall UpdateStaticCall(IrStaticCall sc)
    {
        var args = Map(sc.Args);
        return ReferenceEquals(args, sc.Args) ? sc : sc with { Args = args };
    }

    /// <summary>
    /// Rewrites the receiver and arguments of an IrInstanceCall; returns the original if unchanged.
    /// </summary>
    private IrInstanceCall UpdateInstanceCall(IrInstanceCall ic)
    {
        var recv = RewriteExpr(ic.Recv);
        var args = Map(ic.Args);
        return ReferenceEquals(recv, ic.Recv) && ReferenceEquals(args, ic.Args) ? ic : ic with { Recv = recv, Args = args };
    }

    /// <summary>
    /// Rewrites the argument list of an IrThrowsCall; returns the original if unchanged.
    /// </summary>
    private IrThrowsCall UpdateThrowsCall(IrThrowsCall tc)
    {
        var args = Map(tc.Args);
        return ReferenceEquals(args, tc.Args) ? tc : tc with { Args = args };
    }

    /// <summary>
    /// Rewrites the receiver and arguments of an IrThrowsInstanceCall; returns the original if unchanged.
    /// </summary>
    private IrThrowsInstanceCall UpdateThrowsInstanceCall(IrThrowsInstanceCall ti)
    {
        var recv = RewriteExpr(ti.Recv);
        var args = Map(ti.Args);
        return ReferenceEquals(recv, ti.Recv) && ReferenceEquals(args, ti.Args) ? ti : ti with { Recv = recv, Args = args };
    }

    /// <summary>
    /// Rewrites both operands of an IrBinOp; returns the original if unchanged.
    /// </summary>
    private IrBinOp UpdateBinOp(IrBinOp b)
    {
        var left = RewriteExpr(b.Left);
        var right = RewriteExpr(b.Right);
        return ReferenceEquals(left, b.Left) && ReferenceEquals(right, b.Right) ? b : b with { Left = left, Right = right };
    }

    /// <summary>
    /// Rewrites all three branches of an IrTernary; returns the original if unchanged.
    /// </summary>
    private IrTernary UpdateTernary(IrTernary t)
    {
        var cond = RewriteExpr(t.Cond);
        var then = RewriteExpr(t.Then);
        var el = RewriteExpr(t.Else);
        return ReferenceEquals(cond, t.Cond) && ReferenceEquals(then, t.Then) && ReferenceEquals(el, t.Else) ? t : t with { Cond = cond, Then = then, Else = el };
    }

    /// <summary>
    /// Rewrites the operand of an IrUnaryOp; returns the original if unchanged.
    /// </summary>
    private IrUnaryOp UpdateUnaryOp(IrUnaryOp u)
    {
        var operand = RewriteExpr(u.Operand);
        return ReferenceEquals(operand, u.Operand) ? u : u with { Operand = operand };
    }

    /// <summary>
    /// Rewrites the operand of an IrPostfix; returns the original if unchanged.
    /// </summary>
    private IrPostfix UpdatePostfix(IrPostfix p)
    {
        var operand = RewriteExpr(p.Operand);
        return ReferenceEquals(operand, p.Operand) ? p : p with { Operand = operand };
    }

    /// <summary>
    /// Rewrites the value child of an IrCast; returns the original if unchanged.
    /// </summary>
    private IrCast UpdateCast(IrCast c)
    {
        var value = RewriteExpr(c.Value);
        return ReferenceEquals(value, c.Value) ? c : c with { Value = value };
    }

    /// <summary>
    /// Rewrites the argument list of an IrNew; returns the original if unchanged.
    /// </summary>
    private IrNew UpdateNew(IrNew n)
    {
        var args = Map(n.Args);
        return ReferenceEquals(args, n.Args) ? n : n with { Args = args };
    }

    /// <summary>
    /// Rewrites the constructor arguments and initializer elements of an IrNewInit; returns the original if unchanged.
    /// </summary>
    private IrNewInit UpdateNewInit(IrNewInit ni)
    {
        var args = Map(ni.Args);
        var inits = Map(ni.Inits);
        return ReferenceEquals(args, ni.Args) && ReferenceEquals(inits, ni.Inits) ? ni : ni with { Args = args, Inits = inits };
    }

    /// <summary>
    /// Rewrites the element list of an IrArrayLit; returns the original if unchanged.
    /// </summary>
    private IrArrayLit UpdateArrayLit(IrArrayLit al)
    {
        var elems = Map(al.Elems);
        return ReferenceEquals(elems, al.Elems) ? al : al with { Elems = elems };
    }

    /// <summary>
    /// Rewrites the parts list of an IrInterp; returns the original if unchanged.
    /// </summary>
    private IrInterp UpdateInterp(IrInterp ip)
    {
        var parts = Map(ip.Parts);
        return ReferenceEquals(parts, ip.Parts) ? ip : ip with { Parts = parts };
    }

    /// <summary>
    /// Rewrites the target of an IrAddrOf; returns the original if unchanged.
    /// </summary>
    private IrAddrOf UpdateAddrOf(IrAddrOf a)
    {
        var target = RewriteExpr(a.Target);
        return ReferenceEquals(target, a.Target) ? a : a with { Target = target };
    }

    /// <summary>
    /// Rewrites the pointer child of an IrDeref; returns the original if unchanged.
    /// </summary>
    private IrDeref UpdateDeref(IrDeref d)
    {
        var ptr = RewriteExpr(d.Ptr);
        return ReferenceEquals(ptr, d.Ptr) ? d : d with { Ptr = ptr };
    }

    /// <summary>
    /// Rewrites the target and arguments of an IrIndirectCall; returns the original if unchanged.
    /// </summary>
    private IrIndirectCall UpdateIndirectCall(IrIndirectCall ic)
    {
        var target = RewriteExpr(ic.Target);
        var args = Map(ic.Args);
        return ReferenceEquals(target, ic.Target) && ReferenceEquals(args, ic.Args) ? ic : ic with { Target = target, Args = args };
    }

    /// <summary>
    /// Rewrites the argument list of an IrUnionConstruct; returns the original if unchanged.
    /// </summary>
    private IrUnionConstruct UpdateUnionConstruct(IrUnionConstruct uc)
    {
        var args = Map(uc.Args);
        return ReferenceEquals(args, uc.Args) ? uc : uc with { Args = args };
    }

    /// <summary>
    /// Rewrites the union expression child of an IrUnionField; returns the original if unchanged.
    /// </summary>
    private IrUnionField UpdateUnionField(IrUnionField uf)
    {
        var union = RewriteExpr(uf.Union);
        return ReferenceEquals(union, uf.Union) ? uf : uf with { Union = union };
    }

    /// <summary>
    /// Structurally recurses into a statement, rewriting all child expressions and statements.
    /// </summary>
    protected IrStmt MapStmt(IrStmt s)
    {
        return s switch
        {
            IrBlock b => UpdateBlock(b),
            IrDeclVar d => UpdateDeclVar(d),
            IrAssign a => UpdateAssign(a),
            IrExprStmt es => UpdateExprStmt(es),
            IrReturn r => UpdateReturn(r),
            IrIf i => UpdateIf(i),
            IrWhile w => UpdateWhile(w),
            IrFor f => UpdateFor(f),
            IrForIn fi => UpdateForIn(fi),
            IrTryCatch t => UpdateTryCatch(t),
            IrSwitch sw => UpdateSwitch(sw),
            IrUnsafeBlock u => UpdateUnsafeBlock(u),
            IrMatch m => UpdateMatch(m),
            IrDefer d => UpdateDefer(d),
            _ => s   // IrNativeStmt, IrRaw, IrBreak, IrContinue, IrThrow, IrDebug, IrPanic
        };
    }

    /// <summary>
    /// Rewrites the statement list of an IrBlock; returns the original if unchanged.
    /// </summary>
    private IrBlock UpdateBlock(IrBlock b)
    {
        var stmts = MapStmts(b.Stmts);
        return ReferenceEquals(stmts, b.Stmts) ? b : b with { Stmts = stmts };
    }

    /// <summary>
    /// Rewrites the initializer of an IrDeclVar; returns the original if unchanged.
    /// </summary>
    private IrDeclVar UpdateDeclVar(IrDeclVar d)
    {
        var init = d.Init == null ? null : RewriteExpr(d.Init);
        return ReferenceEquals(init, d.Init) ? d : d with { Init = init };
    }

    /// <summary>
    /// Rewrites the target and value of an IrAssign; returns the original if unchanged.
    /// </summary>
    private IrAssign UpdateAssign(IrAssign a)
    {
        var target = RewriteExpr(a.Target);
        var val = RewriteExpr(a.Value);
        return ReferenceEquals(target, a.Target) && ReferenceEquals(val, a.Value) ? a : a with { Target = target, Value = val };
    }

    /// <summary>
    /// Rewrites the expression of an IrExprStmt; returns the original if unchanged.
    /// </summary>
    private IrExprStmt UpdateExprStmt(IrExprStmt es)
    {
        var expr = RewriteExpr(es.Expr);
        return ReferenceEquals(expr, es.Expr) ? es : es with { Expr = expr };
    }

    /// <summary>
    /// Rewrites the return value of an IrReturn; returns the original if unchanged.
    /// </summary>
    private IrReturn UpdateReturn(IrReturn r)
    {
        var val = r.Value == null ? null : RewriteExpr(r.Value);
        return ReferenceEquals(val, r.Value) ? r : r with { Value = val };
    }

    /// <summary>
    /// Rewrites the condition, then-branch, and else-branch of an IrIf; returns the original if unchanged.
    /// </summary>
    private IrIf UpdateIf(IrIf i)
    {
        var cond = RewriteExpr(i.Cond);
        var then = RewriteBlock(i.Then);
        var el = i.Else == null ? null : RewriteBlock(i.Else);
        return ReferenceEquals(cond, i.Cond) && ReferenceEquals(then, i.Then) && ReferenceEquals(el, i.Else) ? i : i with { Cond = cond, Then = then, Else = el };
    }

    /// <summary>
    /// Rewrites the condition and body of an IrWhile; returns the original if unchanged.
    /// </summary>
    private IrWhile UpdateWhile(IrWhile w)
    {
        var cond = RewriteExpr(w.Cond);
        var body = RewriteBlock(w.Body);
        return ReferenceEquals(cond, w.Cond) && ReferenceEquals(body, w.Body) ? w : w with { Cond = cond, Body = body };
    }

    /// <summary>
    /// Rewrites the init, condition, step, and body of an IrFor; returns the original if unchanged.
    /// </summary>
    private IrFor UpdateFor(IrFor f)
    {
        var init = f.Init == null ? null : RewriteStmt(f.Init);
        var cond = f.Cond == null ? null : RewriteExpr(f.Cond);
        var step = f.Step == null ? null : RewriteStmt(f.Step);
        var body = RewriteBlock(f.Body);
        return ReferenceEquals(init, f.Init) && ReferenceEquals(cond, f.Cond) && ReferenceEquals(step, f.Step) && ReferenceEquals(body, f.Body)
            ? f
            : f with { Init = init, Cond = cond, Step = step, Body = body };
    }

    /// <summary>
    /// Rewrites the collection and body of an IrForIn; returns the original if unchanged.
    /// </summary>
    private IrForIn UpdateForIn(IrForIn fi)
    {
        var coll = RewriteExpr(fi.Collection);
        var body = RewriteBlock(fi.Body);
        return ReferenceEquals(coll, fi.Collection) && ReferenceEquals(body, fi.Body) ? fi : fi with { Collection = coll, Body = body };
    }

    /// <summary>
    /// Rewrites the try and catch blocks of an IrTryCatch; returns the original if unchanged.
    /// </summary>
    private IrTryCatch UpdateTryCatch(IrTryCatch t)
    {
        var @try = RewriteBlock(t.Try);
        var @catch = RewriteBlock(t.Catch);
        return ReferenceEquals(@try, t.Try) && ReferenceEquals(@catch, t.Catch) ? t : t with { Try = @try, Catch = @catch };
    }

    /// <summary>
    /// Rewrites the scrutinee, cases, and default block of an IrSwitch; returns the original if unchanged.
    /// </summary>
    private IrSwitch UpdateSwitch(IrSwitch sw)
    {
        var scr = RewriteExpr(sw.Scrutinee);
        var cases = MapCases(sw.Cases);
        var def = sw.Default == null ? null : RewriteBlock(sw.Default);
        return ReferenceEquals(scr, sw.Scrutinee) && ReferenceEquals(cases, sw.Cases) && ReferenceEquals(def, sw.Default)
            ? sw
            : sw with { Scrutinee = scr, Cases = cases, Default = def };
    }

    /// <summary>
    /// Rewrites the body of an IrUnsafeBlock; returns the original if unchanged.
    /// </summary>
    private IrUnsafeBlock UpdateUnsafeBlock(IrUnsafeBlock u)
    {
        var body = RewriteBlock(u.Body);
        return ReferenceEquals(body, u.Body) ? u : u with { Body = body };
    }

    /// <summary>
    /// Rewrites the scrutinee, cases, and default block of an IrMatch; returns the original if unchanged.
    /// </summary>
    private IrMatch UpdateMatch(IrMatch m)
    {
        var scr = RewriteExpr(m.Scrutinee);
        var cases = MapMatchCases(m.Cases);
        var def = m.Default == null ? null : RewriteBlock(m.Default);
        return ReferenceEquals(scr, m.Scrutinee) && ReferenceEquals(cases, m.Cases) && ReferenceEquals(def, m.Default)
            ? m
            : m with { Scrutinee = scr, Cases = cases, Default = def };
    }

    /// <summary>
    /// Rewrites the action of an IrDefer; returns the original if unchanged.
    /// </summary>
    private IrDefer UpdateDefer(IrDefer d)
    {
        var action = RewriteStmt(d.Action);
        return ReferenceEquals(action, d.Action) ? d : d with { Action = action };
    }

    /// <summary>
    /// Maps a list of expressions, returning the original list if no element was rewritten.
    /// </summary>
    private List<IrExpr> Map(List<IrExpr> xs)
    {
        if (xs.Count == 0) return xs;

        List<IrExpr>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var rewritten = RewriteExpr(orig);
            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrExpr>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of statements, returning the original list if no element was rewritten.
    /// </summary>
    private List<IrStmt> MapStmts(List<IrStmt> xs)
    {
        if (xs.Count == 0) return xs;

        List<IrStmt>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var rewritten = RewriteStmt(orig);
            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrStmt>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of switch cases, returning the original list if no case was rewritten.
    /// </summary>
    private List<IrSwitchCase> MapCases(List<IrSwitchCase> xs)
    {
        if (xs.Count == 0) return xs;

        List<IrSwitchCase>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var labels = Map(orig.Labels);
            var body = RewriteBlock(orig.Body);
            var rewritten = ReferenceEquals(labels, orig.Labels) && ReferenceEquals(body, orig.Body) ? orig : orig with { Labels = labels, Body = body };

            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrSwitchCase>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of match cases, returning the original list if no case was rewritten.
    /// </summary>
    private List<IrMatchCase> MapMatchCases(List<IrMatchCase> xs)
    {
        if (xs.Count == 0) return xs;

        List<IrMatchCase>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var body = RewriteBlock(orig.Body);
            var rewritten = ReferenceEquals(body, orig.Body) ? orig : orig with { Body = body };

            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrMatchCase>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of classes, returning the original list if no class was rewritten.
    /// </summary>
    private List<IrClass> MapClasses(List<IrClass> xs)
    {
        if (xs.Count == 0) return xs;
        List<IrClass>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var rewritten = RewriteClass(orig);
            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrClass>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of functions, returning the original list if no function was rewritten.
    /// </summary>
    private List<IrFunction> MapFunctions(List<IrFunction> xs)
    {
        if (xs.Count == 0) return xs;
        List<IrFunction>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var rewritten = RewriteFunction(orig);
            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrFunction>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of operators, returning the original list if no operator was rewritten.
    /// </summary>
    private List<IrOperator> MapOperators(List<IrOperator> xs)
    {
        if (xs.Count == 0) return xs;
        List<IrOperator>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var rewritten = RewriteOperator(orig);
            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrOperator>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of processes, returning the original list if no process was rewritten.
    /// </summary>
    private List<IrProcess> MapProcesses(List<IrProcess> xs)
    {
        if (xs.Count == 0) return xs;
        List<IrProcess>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var rewritten = RewriteProcess(orig);
            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrProcess>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of threads, returning the original list if no thread's entry function was rewritten.
    /// </summary>
    private List<IrThread> MapThreads(List<IrThread> xs)
    {
        if (xs.Count == 0) return xs;
        List<IrThread>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var rewritten = orig;
            if (orig.EntryFunc != null)
            {
                var entry = RewriteFunction(orig.EntryFunc);
                if (!ReferenceEquals(entry, orig.EntryFunc))
                {
                    rewritten = orig with { EntryFunc = entry };
                }
            }

            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrThread>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a list of fields, returning the original list if no field initializer was rewritten.
    /// </summary>
    private List<IrField> MapFields(List<IrField> xs)
    {
        if (xs.Count == 0) return xs;
        List<IrField>? result = null;
        var span = CollectionsMarshal.AsSpan(xs);
        for (int i = 0; i < span.Length; i++)
        {
            var orig = span[i];
            var rewritten = orig;
            if (orig.Init != null)
            {
                var init = RewriteExpr(orig.Init);
                if (!ReferenceEquals(init, orig.Init))
                {
                    rewritten = orig with { Init = init };
                }
            }

            if (!ReferenceEquals(orig, rewritten))
            {
                if (result == null)
                {
                    result = new List<IrField>(xs.Count);
                    for (int j = 0; j < i; j++) result.Add(span[j]);
                }
                result.Add(rewritten);
            }
            else
            {
                result?.Add(orig);
            }
        }
        return result ?? xs;
    }

    /// <summary>
    /// Maps a dictionary of field initializers, returning the original if no value was rewritten.
    /// </summary>
    private Dictionary<string, IrExpr> MapFieldInits(Dictionary<string, IrExpr> dict)
    {
        if (dict.Count == 0) return dict;
        Dictionary<string, IrExpr>? result = null;
        foreach (var kv in dict)
        {
            var origVal = kv.Value;
            var rewrittenVal = RewriteExpr(origVal);
            if (!ReferenceEquals(origVal, rewrittenVal))
            {
                if (result == null)
                {
                    result = new Dictionary<string, IrExpr>(dict);
                }
                result[kv.Key] = rewrittenVal;
            }
        }
        return result ?? dict;
    }
}
