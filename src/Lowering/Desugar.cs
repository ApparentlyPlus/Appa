namespace Appa;

/// <summary>
/// Desugaring pass that rewrites high-level convenience nodes into ordinary calls
/// so the emitter never special-cases them.
/// Handles string interpolation and switch statements; match is added next.
/// </summary>
internal sealed class Desugar(SymbolTable sym, DiagnosticBag diag) : IrRewriter
{
    private int _seq;

    /// <summary>
    /// Rewrites children first, then lowers any interpolated string expression.
    /// </summary>
    protected override IrExpr RewriteExpr(IrExpr e)
    {
        e = base.RewriteExpr(e);
        return e is IrInterp ip ? LowerInterp(ip) : e;
    }

    /// <summary>
    /// Rewrites children first, then lowers switch statements to if/else-if chains.
    /// </summary>
    protected override IrStmt RewriteStmt(IrStmt s)
    {
        s = base.RewriteStmt(s);
        return s is IrSwitch sw ? LowerSwitch(sw) : s;
    }

    /// <summary>
    /// Lowers a switch to a single-eval scrutinee temp followed by an if/else-if equality chain.
    /// No fallthrough; break and continue inside a case reach the enclosing loop.
    /// </summary>
    private IrStmt LowerSwitch(IrSwitch sw)
    {
        var stmts = new List<IrStmt>();
        string v = $"_sw{_seq++}";
        var vr = new IrVar(v, sw.Scrutinee.Type);
        stmts.Add(new IrDeclVar(v, sw.Scrutinee.Type, sw.Scrutinee));

        IrStmt? chain = sw.Default;
        for (int i = sw.Cases.Count - 1; i >= 0; i--)
        {
            var c = sw.Cases[i];
            IrExpr cond = c.Labels
                .Select(l => (IrExpr)new IrBinOp("==", vr, l, IrType.Bool))
                .Aggregate((a, b) => new IrBinOp("||", a, b, IrType.Bool));
            IrBlock? elseBlk = chain switch { null => null, IrBlock b => b, var x => new IrBlock([x]) };
            chain = new IrIf(cond, c.Body, elseBlk);
        }
        if (chain != null) stmts.Add(chain);
        return new IrBlock(stmts);
    }

    /// <summary>
    /// Folds the parts of an interpolated string into a left-associative concat chain.
    /// Each part is already String-typed so every fold is String's '+' operator.
    /// </summary>
    private IrExpr LowerInterp(IrInterp ip)
    {
        if (ip.Parts.Count == 0) return new IrLitString("\"\"") { Span = ip.Span };
        var acc = ip.Parts[0];
        for (int i = 1; i < ip.Parts.Count; i++)
            acc = new IrStaticCall(Concat(ip.Span), IrType.String, [acc, ip.Parts[i]]) { Span = ip.Span };
        return acc;
    }

    /// <summary>
    /// Returns the CName of String's '+' operator, or emits a diagnostic and returns a fallback name.
    /// </summary>
    private string Concat(TextSpan span)
    {
        var op = sym.LookupOperator("String", "+");
        if (op != null) return op.CName;
        diag.Error(Codes.MissingIntrinsic, "<runtime>", span, "String defines no '+' operator for concatenation");
        return "gata_MISSING_String_concat";
    }
}
