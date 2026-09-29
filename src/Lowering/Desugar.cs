namespace Appa;

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
    /// Lowers a match to a scrutinee temp followed by a tag-equality if/else-if chain with payload
    /// bindings. Mirrors the shape of LowerSwitch exactly, using the union's __tag field as the
    /// discriminant.
    /// </summary>
    private IrBlock LowerMatch(IrMatch ms)
    {
        var stmts = new List<IrStmt>();
        string v = $"__mt{_seq++}";
        var vr = new IrVar(v, ms.Scrutinee.Type);
        stmts.Add(new IrDeclVar(v, ms.Scrutinee.Type, ms.Scrutinee));

        bool closeLastArm = ms.Default == null && IsExhaustive(ms);

        IrStmt? chain = ms.Default;
        for (int i = ms.Cases.Count - 1; i >= 0; i--)
        {
            var c = ms.Cases[i];

            // the arm's bindings become locals read straight out of the payload
            var bodyStmts = new List<IrStmt>();
            foreach (var b in c.Binds)
            {
                var field = new IrUnionField(vr, c.VariantIndex, b.FieldName, b.Type);
                bodyStmts.Add(new IrDeclVar(b.BindName, b.Type, field));
            }
            bodyStmts.AddRange(c.Body.Stmts);

            // an exhaustive match doesn't need to test the last tag, it's the only one left
            if (closeLastArm && i == ms.Cases.Count - 1)
            {
                chain = new IrBlock(bodyStmts);
                continue;
            }

            var tag = new IrFieldLoad(vr, "__tag", IrType.Int);
            var cond = new IrBinOp(BinOp.Eq, tag, new IrLitInt(c.VariantIndex), IrType.Bool);
            chain = new IrIf(cond, new IrBlock(bodyStmts), AsBlock(chain));
        }

        if (chain != null) stmts.Add(chain);
        return new IrBlock(stmts);
    }

    /// <summary>
    /// True if the match names every variant of its union exactly once. Always true for a
    /// defaultless match in a clean build, but re-derived because this pass also runs over IR from
    /// a source that failed to resolve, where the cases can cover nothing.
    /// </summary>
    private bool IsExhaustive(IrMatch ms)
    {
        var variants = sym.UnionDef(ms.UnionT.Name);
        if (variants == null || ms.Cases.Count != variants.Count) return false;

        var seen = new HashSet<int>();
        foreach (var c in ms.Cases)
        {
            int vi = c.VariantIndex;
            if (vi < 0 || vi >= variants.Count || !seen.Add(vi)) return false;
        }
        return true;
    }

    /// <summary>
    /// Lowers a switch to a single-eval scrutinee temp followed by an if/else-if equality chain. No
    /// fallthrough. Break and continue inside a case reach the enclosing loop.
    /// </summary>
    private IrBlock LowerSwitch(IrSwitch sw)
    {
        var stmts = new List<IrStmt>();
        string v = $"__sw{_seq++}";
        var vr = new IrVar(v, sw.Scrutinee.Type);
        stmts.Add(new IrDeclVar(v, sw.Scrutinee.Type, sw.Scrutinee));

        IrStmt? chain = sw.Default;
        for (int i = sw.Cases.Count - 1; i >= 0; i--)
        {
            var c = sw.Cases[i];

            // case 1, 2, 3: becomes v == 1 || v == 2 || v == 3
            IrExpr cond = new IrBinOp(BinOp.Eq, vr, c.Labels[0], IrType.Bool);
            for (int j = 1; j < c.Labels.Count; j++)
            {
                var eq = new IrBinOp(BinOp.Eq, vr, c.Labels[j], IrType.Bool);
                cond = new IrBinOp(BinOp.Or, cond, eq, IrType.Bool);
            }

            chain = new IrIf(cond, c.Body, AsBlock(chain));
        }

        if (chain != null) stmts.Add(chain);
        return new IrBlock(stmts);
    }

    private static IrBlock? AsBlock(IrStmt? s)
    {
        if (s == null) return null;
        return s as IrBlock ?? new IrBlock([s]);
    }

    /// <summary>
    /// Lowers an interpolated string: one part passes through, two fold into a '+', three or more
    /// build through one StringBuilder rather than a chain copying a String per fold. The builder
    /// comes from @builtin(StringBuilder), with '+' as the fallback.
    /// </summary>
    private IrExpr LowerInterp(IrInterp ip)
    {
        if (ip.Parts.Count == 0) return new IrLitString("\"\"") { Span = ip.Span };

        if (ip.Parts.Count >= 3 && sym.Builtins.TryGetValue(BuiltinTypes.StringBuilder, out var sbClass)
            && sym.LookupMethod(sbClass, "Put") is { } put
            && sym.LookupMethod(sbClass, "ToString") is { } toStr)
        {
            // new StringBuilder().Put(a).Put(b).Put(c).ToString()
            IrExpr sb = new IrNew(sbClass, []) { Span = ip.Span };
            foreach (var part in ip.Parts)
                sb = new IrInstanceCall(sb, put.CName, IrTypes.ClassRef(sbClass), [part]) { Span = ip.Span };
            return new IrInstanceCall(sb, toStr.CName, IrType.String, []) { Span = ip.Span };
        }

        var acc = ip.Parts[0];
        for (int i = 1; i < ip.Parts.Count; i++)
            acc = new IrStaticCall(Concat(ip.Span), IrType.String, [acc, ip.Parts[i]]) { Span = ip.Span };
        return acc;
    }

    /// <summary>
    /// Returns the CName of String's '+' operator, or emits a diagnostic and returns a fallback
    /// name.
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
