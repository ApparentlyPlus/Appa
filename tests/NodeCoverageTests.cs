namespace Appa.Tests;

using Appa;

/// <summary>
/// The guards on the four hand-written node dispatchers. These exist because a missing case
/// in Monomorphizer's AST substituter or in IrRewriter/IrWalker is silent: the node is returned
/// untouched and a whole subtree is quietly never visited. Two real bugs in this compiler had
/// exactly that shape, so the inert set is asserted rather than assumed.
///
/// The guards are [Conditional("DEBUG")] on purpose -- a shipped compiler meeting an unexpected
/// node should emit slightly wrong code rather than abort mid-build -- so the calls vanish
/// entirely in Release and there is nothing left to assert. The rejection tests skip there
/// instead of failing, and say why.
/// </summary>
public class NodeCoverageTests
{
    private const bool GuardsActive =
#if DEBUG
        true;
#else
        false;
#endif

    private const string NotInRelease =
        "NodeCoverage's guards are [Conditional(\"DEBUG\")] and compile out of a Release build";

    [Fact]
    public void InertNodesArePermitted()
    {
        if (!GuardsActive) { Assert.Skip(NotInRelease); return; }

        NodeCoverage.AssertInertIrExpr(new IrLitInt(1), "test");
        NodeCoverage.AssertInertIrExpr(new IrVar("x", IrType.Int), "test");
        NodeCoverage.AssertInertIrStmt(new IrBreak(), "test");
        NodeCoverage.AssertInertIrStmt(new IrGoto("L"), "test");
        NodeCoverage.AssertInertAstExpr(new IdentExpr("x", TextSpan.None));
        NodeCoverage.AssertInertAstStmt(new BreakStmt(TextSpan.None));
    }

    [Fact]
    public void NodesWithChildrenAreRejected()
    {
        if (!GuardsActive) { Assert.Skip(NotInRelease); return; }

        var lit = new IrLitInt(1);
        Assert.Throws<System.Diagnostics.UnreachableException>(() =>
            NodeCoverage.AssertInertIrExpr(new IrBinOp(BinOp.Add, lit, lit, IrType.Int), "test"));
        Assert.Throws<System.Diagnostics.UnreachableException>(() =>
            NodeCoverage.AssertInertIrStmt(new IrExprStmt(lit), "test"));
        Assert.Throws<System.Diagnostics.UnreachableException>(() =>
            NodeCoverage.AssertInertAstStmt(new ReturnStmt(null, TextSpan.None)));
    }

    [Fact]
    public void RejectionExplainsTheConsequence()
    {
        if (!GuardsActive) { Assert.Skip(NotInRelease); return; }

        var ex = Assert.Throws<System.Diagnostics.UnreachableException>(() =>
            NodeCoverage.AssertInertIrExpr(new IrInterp([]), "IrWalker.WalkExpr"));
        Assert.Contains("IrWalker.WalkExpr", ex.Message);
        Assert.Contains("IrInterp", ex.Message);
        Assert.Contains("skipped by every pass", ex.Message);
    }
}
