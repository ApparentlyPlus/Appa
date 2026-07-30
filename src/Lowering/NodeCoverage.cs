namespace Appa;

using System.Diagnostics;

internal static class NodeCoverage
{
    /// <summary>
    /// Asserts that an AST statement reaching Monomorphizer.SubStmt's default arm is one with
    /// nothing to substitute.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertInertAstStmt(Stmt s)
    {
        if (s is NativeStmt or BreakStmt or ContinueStmt or ThrowStmt or DebugStmt or PanicStmt) return;
        throw new UnreachableException(Message("Monomorphizer.SubStmt", s.GetType().Name,
            "its type arguments will not be substituted when a generic is stamped"));
    }

    /// <summary>
    /// Asserts that an AST expression reaching Monomorphizer.SubExpr's default arm is one with
    /// nothing to substitute.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertInertAstExpr(Expr e)
    {
        if (e is IntLitExpr or FloatLitExpr or StrLitExpr or CharLitExpr or BoolLitExpr
                or NullExpr or IdentExpr) return;
        throw new UnreachableException(Message("Monomorphizer.SubExpr", e.GetType().Name,
            "its type arguments will not be substituted when a generic is stamped"));
    }

    /// <summary>
    /// Asserts that an IR expression reaching a traversal's default arm has no child expressions.
    /// Shared by IrRewriter.MapExpr and IrWalker.WalkExpr, which must agree on this set.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertInertIrExpr(IrExpr e, string where)
    {
        if (e is IrLitInt or IrLitFloat or IrLitString or IrLitChar or IrLitBool or IrLitNull
                or IrVar or IrSelfExpr or IrFuncRef or IrEnumConst or IrSizeof or IrDefault) return;
        throw new UnreachableException(Message(where, e.GetType().Name,
            "its child expressions will be skipped by every pass built on this traversal"));
    }

    /// <summary>
    /// Asserts that an IR statement reaching a traversal's default arm has no child nodes. Shared
    /// by IrRewriter.MapStmt and IrWalker.WalkStmt.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertInertIrStmt(IrStmt s, string where)
    {
        if (s is IrNativeStmt or IrGoto or IrLabel or IrBreak or IrContinue or IrThrow
                or IrDebug or IrPanic) return;
        throw new UnreachableException(Message(where, s.GetType().Name,
            "its child nodes will be skipped by every pass built on this traversal"));
    }
    
    /// <summary>
    /// Asserts that a statement reaching the default arm of a control-flow analysis - the hand-rolled
    /// switches in DefinitelyReturns and HasLoopBreak - carries nothing those analyses would need to
    /// look inside.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertNoNestedFlow(IrStmt s, string where)
    {
        if (s is IrGoto or IrLabel or IrBreak or IrContinue or IrThrow or IrDebug or IrPanic
                or IrReturn or IrAssignValue) return;
        throw new UnreachableException(Message(where, s.GetType().Name,
            "the control flow inside it is invisible to this analysis"));
    }

    private static string Message(string where, string node, string consequence)
    {
        return $"[{where}] no case for {node}, and it is not in the inert set. " +
               $"Left unhandled, {consequence}. Add a case for it, or - if it really has no " +
               $"children - add it to the inert set in NodeCoverage.";
    }
}
