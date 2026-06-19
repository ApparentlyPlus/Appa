namespace Appa;

/// <summary>
/// Desugaring pass that rewrites high-level convenience nodes into ordinary calls
/// so the emitter never special-cases them.
/// Currently handles string interpolation: $"a{x}b" folds to ("a" + x) + "b" via String's '+' operator.
/// </summary>
internal sealed class Desugar(SymbolTable sym, DiagnosticBag diag) : IrRewriter
{
    /// <summary>
    /// Rewrites children first, then lowers any interpolated string expression.
    /// </summary>
    protected override IrExpr RewriteExpr(IrExpr e)
    {
        e = base.RewriteExpr(e);
        return e is IrInterp ip ? LowerInterp(ip) : e;
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
