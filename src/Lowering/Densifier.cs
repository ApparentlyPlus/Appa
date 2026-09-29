namespace Appa;

internal sealed class Densifier(IrModule m)
{
    private int _seq;

    // Names that already mean something in the emitted C and so cannot be handed out as a token.
    // An '@extern' may be spelled '_g5', and nothing else stops the sequence from reaching it.
    private HashSet<string>? _taken;

    /// <summary>
    /// Returns the next dense token in base-36 sequence, prefixed with _g, skipping any the program
    /// already spells for itself.
    /// </summary>
    private string Next()
    {
        string t;
        do
        {
            t = "__g" + Base36(_seq++);
        } while (_taken != null && _taken.Contains(t));
        return t;
    }

    /// <summary>
    /// Converts a non-negative integer to a base-36 string using digits 0-9 and letters a-z.
    /// </summary>
    private static string Base36(int v)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (v == 0) return "0";

        string s = "";
        while (v > 0)
        {
            s = digits[v % 36] + s;
            v /= 36;
        }
        return s;
    }

    /// <summary>
    /// Runs the dense naming pass and returns the renamed module together with a sourcemap that
    /// maps each dense token back to its original readable name.
    /// </summary>
    public (IrModule Module, IReadOnlyDictionary<string, string> Sourcemap) Run()
    {
        // every name that has to survive as written
        _taken = [];
        foreach (var (_, cname) in m.Symbols.Externs()) _taken.Add(cname);
        foreach (var f in m.FreeFunctions)
        {
            if (f.IsEntry || IsKept(f)) _taken.Add(f.CName);
        }
        foreach (var c in m.Classes)
        {
            if (c.Keep) _taken.Add(c.CName);
        }
        foreach (var e in m.Enums) _taken.Add(e.CName);
        foreach (var u in m.Unions) _taken.Add(u.CName);
        foreach (var n in m.NativeTypes) _taken.Add(n.CName);
        foreach (var p in m.Processes)
        {
            if (p.StateInit is { } si) _taken.Add(si.CName);
            foreach (var v in p.State) _taken.Add(v.CName);
        }

        var fn = new Dictionary<string, string>();          // old C name -> dense token
        var classTok = new Dictionary<string, string>();
        var src = new Dictionary<string, string>();         // dense token -> readable, for the sourcemap

        void MapFn(string old, string readable)
        {
            if (fn.ContainsKey(old)) return;
            string d = Next();
            fn[old] = d;
            src[d] = readable;
        }

        // Internal free functions and all methods/operators get dense names.
        // Entries and @keep free functions keep their readable names.
        foreach (var f in m.FreeFunctions)
        {
            if (!f.IsEntry && !IsKept(f))
                MapFn(f.CName, Mangler.DisplayName(f.Name));
        }
        foreach (var c in m.Classes)
        {
            string owner = Mangler.DisplayName(c.Name);
            foreach (var mm in c.Methods) MapFn(mm.CName, $"{owner}.{mm.Name}");
            foreach (var o in c.Operators) MapFn(o.CName, $"{owner}.operator{o.Op}");
        }
        
        // @keep classes keep their readable CName so native text that references
        // them by the readable gata_<Name> form continues to resolve correctly.
        foreach (var c in m.Classes)
        {
            string tok = c.Keep ? c.CName : Next();
            classTok[c.Name] = tok;
            src[tok] = Mangler.DisplayName(c.Name);
        }

        var renamed = new CallRenamer(fn).Run(m);

        var freeFunctions = new List<IrFunction>();
        foreach (var f in renamed.FreeFunctions)
            freeFunctions.Add(Rename(f, fn));

        var classes = new List<IrClass>();
        foreach (var c in renamed.Classes)
        {
            var methods = new List<IrFunction>();
            foreach (var mm in c.Methods)
                methods.Add(Rename(mm, fn));

            var operators = new List<IrOperator>();
            foreach (var o in c.Operators)
                operators.Add(fn.TryGetValue(o.CName, out var d) ? o with { CName = d } : o);

            classes.Add(c with
            {
                CName = classTok[c.Name],
                Methods = methods,
                Operators = operators
            });
        }

        var module = renamed with
        {
            FreeFunctions = freeFunctions,
            Classes = classes
        };

        // intrinsics are looked up by role later, so point them at the new names too
        var intrinsics = module.Symbols.Intrinsics;
        foreach (var role in intrinsics.Keys.ToList())
        {
            if (fn.TryGetValue(intrinsics[role], out var d)) intrinsics[role] = d;
        }

        Mangler.SetDense(classTok);
        return (module, src);
    }

    private static bool IsKept(IrFunction f) => f.Annotations.Any(a => a is KeepAnnotation);

    /// <summary>
    /// Returns a renamed function if the CName has a dense mapping, otherwise returns the original.
    /// </summary>
    private static IrFunction Rename(IrFunction f, Dictionary<string, string> fn) =>
        fn.TryGetValue(f.CName, out var d) ? f with { CName = d } : f;

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
