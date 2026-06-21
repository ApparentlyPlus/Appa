namespace Appa;

/// <summary>
/// Post-DCE dense naming pass that assigns short sequential identifiers (_g0, _g1, ...) to
/// every reachable internal class and function, rewrites all call sites and definitions to use them,
/// updates Mangler so the emitter only ever sees the short names, and produces a sourcemap from
/// each dense token back to its original readable name.
/// Exports, @keep symbols, and native type names keep their readable names.
/// </summary>
internal sealed class Densifier(IrModule m)
{
    private int _seq;

    /// <summary>
    /// Returns the next dense token in base-36 sequence, prefixed with _g.
    /// </summary>
    private string Next() => "_g" + Base36(_seq++);

    /// <summary>
    /// Converts a non-negative integer to a base-36 string using digits 0-9 and letters a-z.
    /// </summary>
    private static string Base36(int v)
    {
        const string D = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (v == 0) return "0";
        var s = "";
        for (; v > 0; v /= 36) s = D[v % 36] + s;
        return s;
    }

    /// <summary>
    /// Runs the dense naming pass and returns the renamed module together with a sourcemap
    /// that maps each dense token back to its original readable name.
    /// </summary>
    public (IrModule Module, IReadOnlyDictionary<string, string> Sourcemap) Run()
    {
        var fn = new Dictionary<string, string>();
        var classTok = new Dictionary<string, string>();
        var src = new Dictionary<string, string>();

        void MapFn(string old)
        {
            if (fn.ContainsKey(old)) return;
            string d = Next(); fn[old] = d; src[d] = old;
        }

        // Internal free functions and all methods/operators get dense names.
        // Entries and @keep free functions keep their readable names.
        foreach (var f in m.FreeFunctions)
            if (!f.IsEntry && !f.Annotations.Any(a => a is KeepAnnotation)) MapFn(f.CName);
        foreach (var c in m.Classes)
        {
            foreach (var mm in c.Methods) MapFn(mm.CName);
            foreach (var o in c.Operators) MapFn(o.CName);
        }
        // @keep classes keep their readable CName so native text that references
        // them by the readable gata_<Name> form continues to resolve correctly.
        foreach (var c in m.Classes)
        {
            if (c.Keep) { classTok[c.Name] = c.CName; src[c.CName] = c.CName; continue; }
            string d = Next(); classTok[c.Name] = d; src[d] = c.CName;
        }

        var renamed = new CallRenamer(fn).Run(m);
        var module = renamed with
        {
            FreeFunctions = renamed.FreeFunctions.Select(f => Rename(f, fn)).ToList(),
            Classes = renamed.Classes.Select(c => c with
            {
                CName = classTok[c.Name],
                Methods = c.Methods.Select(mm => Rename(mm, fn)).ToList(),
                Operators = c.Operators.Select(o => fn.TryGetValue(o.CName, out var d) ? o with { CName = d } : o).ToList(),
            }).ToList(),
        };

        foreach (var role in module.Symbols.Intrinsics.Keys.ToList())
            if (fn.TryGetValue(module.Symbols.Intrinsics[role], out var d))
                module.Symbols.Intrinsics[role] = d;

        Mangler.SetDense(classTok);
        return (module, src);
    }

    /// <summary>
    /// Returns a renamed function if the CName has a dense mapping, otherwise returns the original.
    /// </summary>
    private static IrFunction Rename(IrFunction f, Dictionary<string, string> fn)
    {
        return fn.TryGetValue(f.CName, out var d) ? f with { CName = d } : f;
    }

    /// <summary>
    /// Rewrites every call site, for-in reference, and func-ref through the old-to-dense name map.
    /// Unmapped names (exports, externs, libc) are left unchanged.
    /// </summary>
    private sealed class CallRenamer(Dictionary<string, string> fn) : IrRewriter
    {
        private string Map(string c) => fn.GetValueOrDefault(c, c);

        /// <summary>
        /// Rewrites all call and func-ref expressions to use dense names.
        /// </summary>
        protected override IrExpr RewriteExpr(IrExpr e)
        {
            return base.RewriteExpr(e) switch
            {
                IrStaticCall sc => sc with { CName = Map(sc.CName) },
                IrInstanceCall ic => ic with { CName = Map(ic.CName) },
                IrThrowsCall tc => tc with { CName = Map(tc.CName) },
                IrThrowsInstanceCall ti => ti with { CName = Map(ti.CName) },
                IrNewInit ni => ni with { AddCName = Map(ni.AddCName) },
                IrFuncRef fr => fr with { CName = Map(fr.CName) },
                var x => x
            };
        }

        /// <summary>
        /// Rewrites for-in len and get references to use dense names.
        /// </summary>
        protected override IrStmt RewriteStmt(IrStmt s)
        {
            return base.RewriteStmt(s) switch
            {
                IrForIn fi => fi with { LenCName = Map(fi.LenCName), GetCName = Map(fi.GetCName) },
                var x => x
            };
        }
    }
}
