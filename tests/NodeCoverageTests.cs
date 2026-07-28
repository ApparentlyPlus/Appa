namespace Appa.Tests;

using Appa;

/// <summary>
/// The guards on the four hand-written node dispatchers, where a missing case is silent and two
/// real bugs had that shape. They are [Conditional("DEBUG")] on purpose, so the calls vanish in
/// Release and the rejection tests skip there rather than fail.
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
