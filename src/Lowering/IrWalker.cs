namespace Appa;

using System.Runtime.InteropServices;

// Base for read-only IR analyses (Dce, CapabilityScan): the side-effecting twin of
// IrRewriter. Default WalkStmt/WalkExpr recurse into every child unconditionally;
// a pass overrides one or both to react to the node kinds it cares about, then
// calls base.WalkStmt/base.WalkExpr to keep recursing into that node's children -
// the same "override what matters, inherit the rest" shape IrRewriter uses.
//
// This exists because Dce and CapabilityScan used to hand-write nearly identical
// recursive case lists, which meant every new IrStmt/IrExpr node type had to be
// added to both by hand - miss one and the pass silently fails to look inside it.
// Sharing one base means a future node type only needs handling here once.


/// <summary>
/// Abstract base for read-only IR traversal passes.
/// Override WalkStmt or WalkExpr to react to specific node kinds; call base to continue recursing.
/// </summary>
abstract class IrWalker
{
    /// <summary>
    /// Dispatches a statement node to its children.
    /// Override to intercept specific statement kinds; call base to recurse into children.
    /// </summary>
    protected virtual void WalkStmt(IrStmt s)
    {
        switch (s)
        {
            case IrBlock b: 
                foreach (var x in CollectionsMarshal.AsSpan(b.Stmts)) WalkStmt(x);
                break;
            case IrUnsafeBlock u: 
                WalkStmt(u.Body); 
                break;
            case IrDeclVar d: 
                if (d.Init != null) WalkExpr(d.Init); 
                break;
            case IrAssign a:
                WalkExpr(a.Target);
                WalkExpr(a.Value);
                break;
            case IrExprStmt e:
                WalkExpr(e.Expr); 
                break;
            case IrReturn r: 
                if (r.Value != null) WalkExpr(r.Value);
                break;
            case IrIf i: 
                WalkExpr(i.Cond);
                WalkStmt(i.Then);
                if (i.Else != null) WalkStmt(i.Else);
                break;
            case IrWhile w: 
                WalkExpr(w.Cond);
                WalkStmt(w.Body);
                break;
            case IrFor f: 
                if (f.Init != null) WalkStmt(f.Init);
                if (f.Cond != null) WalkExpr(f.Cond); 
                if (f.Step != null) WalkExpr(f.Step); 
                WalkStmt(f.Body); 
                break;
            case IrForIn fi: 
                WalkExpr(fi.Collection); 
                WalkStmt(fi.Body);
                break;
            case IrTryCatch t: 
                WalkStmt(t.Try);
                WalkStmt(t.Catch);
                break;
            case IrSwitch sw:
                WalkExpr(sw.Scrutinee);
                foreach (var c in CollectionsMarshal.AsSpan(sw.Cases)) { 
                    foreach (var l in CollectionsMarshal.AsSpan(c.Labels)) WalkExpr(l); 
                    WalkStmt(c.Body); 
                }
                if (sw.Default != null) WalkStmt(sw.Default);
                break;
            case IrMatch ms:
                WalkExpr(ms.Scrutinee);
                foreach (var c in CollectionsMarshal.AsSpan(ms.Cases)) WalkStmt(c.Body);
                if (ms.Default != null) WalkStmt(ms.Default);
                break;
            case IrDefer d2:
                WalkStmt(d2.Action); 
                break;
            // IrNativeStmt, IrRaw, IrBreak, IrContinue, IrThrow, IrDebug, IrPanic: no children.
        }
    }

    /// <summary>
    /// Dispatches an expression node to its children.
    /// Override to intercept specific expression kinds; call base to recurse into children.
    /// </summary>
    protected virtual void WalkExpr(IrExpr e)
    {
        switch (e)
        {
            case IrFieldLoad fl: 
                WalkExpr(fl.Obj); 
                break;
            case IrIndex ix:
                WalkExpr(ix.Obj);
                WalkExpr(ix.Idx);
                break;
            case IrStaticCall sc:
                foreach (var a in CollectionsMarshal.AsSpan(sc.Args)) WalkExpr(a);
                break;
            case IrInstanceCall ic:
                WalkExpr(ic.Recv); 
                foreach (var a in CollectionsMarshal.AsSpan(ic.Args)) WalkExpr(a); 
                break;
            case IrThrowsCall tc:
                foreach (var a in CollectionsMarshal.AsSpan(tc.Args)) WalkExpr(a);
                break;
            case IrThrowsInstanceCall ti: 
                WalkExpr(ti.Recv); 
                foreach (var a in CollectionsMarshal.AsSpan(ti.Args)) WalkExpr(a); 
                break;
            case IrNew n:
                foreach (var a in CollectionsMarshal.AsSpan(n.Args)) WalkExpr(a); 
                break;
            case IrNewInit ni:
                foreach (var a in CollectionsMarshal.AsSpan(ni.Args)) WalkExpr(a); 
                foreach (var x in CollectionsMarshal.AsSpan(ni.Inits)) WalkExpr(x); 
                break;
            case IrCast c:
                WalkExpr(c.Value);
                break;
            case IrBinOp b: 
                WalkExpr(b.Left); 
                WalkExpr(b.Right); 
                break;
            case IrTernary t: 
                WalkExpr(t.Cond);
                WalkExpr(t.Then);
                WalkExpr(t.Else); 
                break;
            case IrUnaryOp u: 
                WalkExpr(u.Operand); 
                break;
            case IrPostfix p: 
                WalkExpr(p.Operand); 
                break;
            case IrArrayLit al:
                foreach (var x in CollectionsMarshal.AsSpan(al.Elems)) WalkExpr(x);
                break;
            case IrInterp ip:
                foreach (var x in CollectionsMarshal.AsSpan(ip.Parts)) WalkExpr(x);
                break;
            case IrAddrOf a2:
                WalkExpr(a2.Target);
                break;
            case IrDeref d3:
                WalkExpr(d3.Ptr);
                break;
            case IrIndirectCall ic2:
                WalkExpr(ic2.Target);
                foreach (var a in CollectionsMarshal.AsSpan(ic2.Args)) WalkExpr(a);
                break;
            case IrUnionConstruct uc: 
                foreach (var a in CollectionsMarshal.AsSpan(uc.Args)) WalkExpr(a);
                break;
            case IrUnionField uf:
                WalkExpr(uf.Union);
                break;
            // Literals, IrVar, IrSelfExpr, IrFuncRef, IrSizeof, IrDefault: no children.
        }
    }
}
