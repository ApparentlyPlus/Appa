namespace Appa.Tests;

using Appa;

/// <summary>
/// The guards on the four hand-written node dispatchers. These exist because a missing case
/// in Monomorphizer's AST substituter or in IrRewriter/IrWalker is silent: the node is returned
/// untouched and a whole subtree is quietly never visited. Two real bugs in this compiler had
/// exactly that shape, so the inert set is asserted rather than assumed.
/// </summary>
public class NodeCoverageTests
{
    /// <summary>
    /// A node with no children is legitimately allowed through the default arm.
    /// </summary>
    [Fact]
    public void InertNodesArePermitted()
    {
        NodeCoverage.AssertInertIrExpr(new IrLitInt(1), "test");
        NodeCoverage.AssertInertIrExpr(new IrVar("x", IrType.Int), "test");
        NodeCoverage.AssertInertIrStmt(new IrBreak(), "test");
        NodeCoverage.AssertInertIrStmt(new IrGoto("L"), "test");
        NodeCoverage.AssertInertAstExpr(new IdentExpr("x", TextSpan.None));
        NodeCoverage.AssertInertAstStmt(new BreakStmt(TextSpan.None));
    }

    /// <summary>
    /// A node that owns children must not reach a default arm - that is the bug being guarded
    /// against, and in a debug build it fails loudly instead of silently skipping the subtree.
    /// </summary>
    [Fact]
    public void NodesWithChildrenAreRejected()
    {
        var lit = new IrLitInt(1);
        Assert.Throws<System.Diagnostics.UnreachableException>(() =>
            NodeCoverage.AssertInertIrExpr(new IrBinOp(BinOp.Add, lit, lit, IrType.Int), "test"));
        Assert.Throws<System.Diagnostics.UnreachableException>(() =>
            NodeCoverage.AssertInertIrStmt(new IrExprStmt(lit), "test"));
        Assert.Throws<System.Diagnostics.UnreachableException>(() =>
            NodeCoverage.AssertInertAstStmt(new ReturnStmt(null, TextSpan.None)));
    }

    /// <summary>
    /// The message has to name the dispatcher, the node, and the consequence - a bare
    /// "unhandled node" would leave the next person to rediscover why it matters.
    /// </summary>
    [Fact]
    public void RejectionExplainsTheConsequence()
    {
        var ex = Assert.Throws<System.Diagnostics.UnreachableException>(() =>
            NodeCoverage.AssertInertIrExpr(new IrInterp([]), "IrWalker.WalkExpr"));
        Assert.Contains("IrWalker.WalkExpr", ex.Message);
        Assert.Contains("IrInterp", ex.Message);
        Assert.Contains("skipped by every pass", ex.Message);
    }
}
