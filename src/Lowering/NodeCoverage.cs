namespace Appa;

using System.Diagnostics;

// Guards for the compiler's four hand written node dispatchers: Monomorphizer's AST
// substituter (SubStmt/SubExpr) and IrRewriter/IrWalker's IR traversals.
//
// Each of those switches ends in a catch all that returns the node untouched, which is correct
// for the node kinds that genuinely have no children to visit - a literal, a `break`, an
// identifier. The hazard is that it is equally silent for a node kind nobody taught it about:
// adding a new AST or IR node and forgetting one dispatcher produces no error anywhere, just a
// subtree that is never substituted, never renamed, never marked live. That failure mode has
// already cost this compiler twice - a `catch` handler whose type parameters were left
// unsubstituted inside a generic, and a struct literal whose field expressions were invisible
// to both traversals.
//
// So the inert set is written down explicitly, once per dispatcher, and a debug build throws
// on anything outside it. Release keeps the lenient pass-through: a shipped compiler meeting an
// unexpected node should emit slightly wrong code, not abort mid-build. The [Conditional]
// attribute removes the call entirely outside DEBUG, so this costs nothing in a release image.
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
    /// Asserts that an IR statement reaching a traversal's default arm has no child nodes.
    /// Shared by IrRewriter.MapStmt and IrWalker.WalkStmt.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertInertIrStmt(IrStmt s, string where)
    {
        if (s is IrNativeStmt or IrGoto or IrLabel or IrBreak or IrContinue or IrThrow
                or IrDebug or IrPanic) return;
        throw new UnreachableException(Message(where, s.GetType().Name,
            "its child nodes will be skipped by every pass built on this traversal"));
    }
    
    private static string Message(string where, string node, string consequence)
    {
        return $"[{where}] no case for {node}, and it is not in the inert set. " +
               $"Left unhandled, {consequence}. Add a case for it, or - if it really has no " +
               $"children - add it to the inert set in NodeCoverage.";
    }
}
