namespace Appa;

/// <summary>
/// Desugaring pass that rewrites high-level convenience nodes into ordinary calls
/// so the emitter never special-cases them.
/// Lowers string interpolation, switch, and match; after this pass the emitter sees only if/else chains.
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
    /// Rewrites children first, then lowers switch and match statements to if/else-if chains.
    /// </summary>
    protected override IrStmt RewriteStmt(IrStmt s)
    {
        s = base.RewriteStmt(s);
        return s switch
        {
            IrSwitch sw => LowerSwitch(sw),
            IrMatch ms => LowerMatch(ms),
            _ => s
        };
    }

    /// <summary>
    /// Lowers a match to a scrutinee temp followed by a tag-equality if/else-if chain with payload bindings.
    /// Mirrors the shape of LowerSwitch exactly, using the union's __tag field as the discriminant.
    /// </summary>
    private IrBlock LowerMatch(IrMatch ms)
    {
        var stmts = new List<IrStmt>();
        string v = $"_mt{_seq++}";
        var vr = new IrVar(v, ms.Scrutinee.Type);
        stmts.Add(new IrDeclVar(v, ms.Scrutinee.Type, ms.Scrutinee));

        IrStmt? chain = ms.Default;
        for (int i = ms.Cases.Count - 1; i >= 0; i--)
        {
            var c = ms.Cases[i];
            IrExpr cond = new IrBinOp(BinOp.Eq,
                new IrFieldLoad(vr, "__tag", IrType.Int), new IrLitInt(c.VariantIndex), IrType.Bool);
            var bodyStmts = new List<IrStmt>();
            foreach (var b in c.Binds)
                bodyStmts.Add(new IrDeclVar(b.BindName, b.Type, new IrUnionField(vr, c.VariantIndex, b.FieldName, b.Type)));
            bodyStmts.AddRange(c.Body.Stmts);
            IrBlock? elseBlk = chain switch { null => null, IrBlock b2 => b2, var x => new IrBlock([x]) };
            chain = new IrIf(cond, new IrBlock(bodyStmts), elseBlk);
        }
        if (chain != null) stmts.Add(chain);
        return new IrBlock(stmts);
    }

    /// <summary>
    /// Lowers a switch to a single-eval scrutinee temp followed by an if/else-if equality chain.
    /// No fallthrough; break and continue inside a case reach the enclosing loop.
    /// </summary>
    private IrBlock LowerSwitch(IrSwitch sw)
    {
        var stmts = new List<IrStmt>();
        string v = $"_sw{_seq++}";
        var vr = new IrVar(v, sw.Scrutinee.Type);
        stmts.Add(new IrDeclVar(v, sw.Scrutinee.Type, sw.Scrutinee));

        IrStmt? chain = sw.Default;
        for (int i = sw.Cases.Count - 1; i >= 0; i--)
        {
            var c = sw.Cases[i];
            IrExpr cond = new IrBinOp(BinOp.Eq, vr, c.Labels[0], IrType.Bool);
            for (int j = 1; j < c.Labels.Count; j++)
            {
                cond = new IrBinOp(BinOp.Or, cond, new IrBinOp(BinOp.Eq, vr, c.Labels[j], IrType.Bool), IrType.Bool);
            }
            IrBlock? elseBlk = chain switch { null => null, IrBlock b => b, var x => new IrBlock([x]) };
            chain = new IrIf(cond, c.Body, elseBlk);
        }
        if (chain != null) stmts.Add(chain);
        return new IrBlock(stmts);
    }

    /// <summary>
    /// Lowers an interpolated string. One part passes through and two parts fold into a single
    /// '+' call, but three or more parts build through one StringBuilder instead of a concat
    /// chain - a chain allocates (and copies into) a fresh String per fold, O(n^2) in the total
    /// length, where the builder is one growable buffer plus one final String. The builder class
    /// is resolved through @builtin(StringBuilder), never by name; if the active stdlib doesn't
    /// bind one (or lacks Put/ToString), the concat chain remains as the fallback.
    /// </summary>
    private IrExpr LowerInterp(IrInterp ip)
    {
        if (ip.Parts.Count == 0) return new IrLitString("\"\"") { Span = ip.Span };
        if (ip.Parts.Count >= 3 && sym.Builtins.TryGetValue(BuiltinTypes.StringBuilder, out var sbClass)
            && sym.LookupMethod(sbClass, "Put") is { } put
            && sym.LookupMethod(sbClass, "ToString") is { } toStr)
        {
            IrExpr sb = new IrNew(sbClass, []) { Span = ip.Span };
            foreach (var part in ip.Parts)
                sb = new IrInstanceCall(sb, put.CName, new IrClassRef(sbClass), [part]) { Span = ip.Span };
            return new IrInstanceCall(sb, toStr.CName, IrType.String, []) { Span = ip.Span };
        }
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
        string stringClass = sym.Builtins.GetValueOrDefault(BuiltinTypes.String, BuiltinTypes.String);
        var op = sym.LookupOperator(stringClass, "+");
        if (op != null) return op.CName;
        diag.Error(Codes.MissingIntrinsic, "<runtime>", span, "String defines no '+' operator for concatenation");
        return "gata_MISSING_String_concat";
    }
}
