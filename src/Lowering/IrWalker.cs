namespace Appa;

using System.Runtime.InteropServices;

internal abstract class IrWalker
{
    /// <summary>
    /// Dispatches a statement node to its children. Override to intercept specific statement kinds;
    /// call base to recurse into children.
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
                if (f.Step != null) WalkStmt(f.Step);
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
            case IrAssignValue av:
                WalkExpr(av.Value);
                break;
            
            // IrNativeStmt, IrGoto, IrLabel, IrBreak, IrContinue, IrThrow, IrDebug, IrPanic:
            // no children. Debug builds reject anything else.
            default: NodeCoverage.AssertInertIrStmt(s, "IrWalker.WalkStmt"); break;
        }
    }

    /// <summary>
    /// Dispatches an expression node to its children. Override to intercept specific expression
    /// kinds; call base to recurse into children.
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
            case IrCatchCall cc:
                WalkExpr(cc.Call);
                WalkStmt(cc.Handler);
                break;
            case IrStructLit sl:
                foreach (var f in CollectionsMarshal.AsSpan(sl.Fields)) WalkExpr(f.Value);
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
            default: NodeCoverage.AssertInertIrExpr(e, "IrWalker.WalkExpr"); break;
        }
    }
}
