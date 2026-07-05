namespace Appa;

internal sealed class TypeResolver(
    SymbolTable sym,
    HashSet<string> hasInit,
    HashSet<string> nativeStructs,
    HashSet<string> opaqueFieldClasses,
    Dictionary<string, HashSet<string>> visible,
    bool releaseMode,
    DiagnosticBag diag)
{
    // Modules visible to the file currently being resolved (set per file).
    private HashSet<string> _scope = [];

    /// <summary>
    /// Returns true when a class name is declared in a module the current file imports.
    /// </summary>
    private bool ClassInScope(string name)
    {
        return sym.ClassModule(name) is { } m && _scope.Contains(m);
    }

    /// <summary>
    /// Returns true when a class name is declared in a module the current file imports.
    /// </summary>
    private bool ClassInScope(ReadOnlySpan<char> name)
    {
        return sym.ClassModule(name) is { } m && _scope.Contains(m);
    }

    /// <summary>
    /// Returns true when a free-function symbol is in scope for the current file.
    /// </summary>
    private bool FuncInScope(Symbol? f)
    {
        return f != null && _scope.Contains(f.Module);
    }

    // Every fixed-array (T, N) pair used; the emitter stamps one struct per pair.
    private readonly List<IrArrayType> _arrays = [];
    private int _tmpSeq;

    /// <summary>
    /// Allocates a unique temporary variable name with the given prefix.
    /// </summary>
    private string Tmp(string prefix)
    {
        return $"{prefix}{_tmpSeq++}";
    }

    /// <summary>
    /// Records a fixed-array type usage and returns the IrArrayType node.
    /// </summary>
    private IrArrayType Arr(IrType elem, int size)
    {
        var a = new IrArrayType(elem, size);
        _arrays.Add(a);
        return a;
    }

    // Every distinct function-pointer signature used; the emitter stamps one typedef per signature.
    private readonly List<IrFuncPtrType> _funcPtrTypes = [];
    private readonly Dictionary<FuncPtrKey, IrFuncPtrType> _funcPtrSeen = [];

    /// <summary>
    /// Returns or creates a function-pointer type for the given return type and parameter list.
    /// </summary>
    private IrFuncPtrType FnPtr(IrType ret, List<IrType> ps)
    {
        var key = new FuncPtrKey(ret, ps);
        if (_funcPtrSeen.TryGetValue(key, out var existing)) return existing;
        var f = new IrFuncPtrType(ret, ps);
        _funcPtrSeen[key] = f;
        _funcPtrTypes.Add(f);
        return f;
    }

    // Generic free-function templates; each distinct instantiation is stamped once.
    private readonly Dictionary<string, (FuncDecl Decl, string File, string Context)> _funcTemplates = [];
    private readonly Queue<(FuncDecl Decl, string File, string Context, Dictionary<string, string> Binds, string Mangled)> _genericQueue = new();
    private readonly HashSet<string> _genericSeen = [];
    private int _labelSeq;

    // Splits "func(T1,T2)->R" into its parameter type-strings and return type-string,
    // tracking paren/bracket depth so nested types don't split at the wrong comma.
    /// <summary>
    /// Splits an encoded function-type string into parameter types and return type.
    /// </summary>
    private static bool TrySplitFuncType(ReadOnlySpan<char> t, out List<Range> ps, out ReadOnlySpan<char> rs)
    {
        ps = [];
        rs = default;
        if (!t.StartsWith("func(")) return false;
        int depth = 0, pstart = 5, close = -1;
        for (int i = 5; i < t.Length; i++)
        {
            char c = t[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') { if (depth == 0) { close = i; break; } depth--; }
            else if (c == ',' && depth == 0) { ps.Add(new Range(pstart, i)); pstart = i + 1; }
        }
        if (close < 0) return false;
        int lastLength = close - pstart;
        if (lastLength > 0) ps.Add(new Range(pstart, close));
        int rstart = close + 1;
        if (t[rstart..].StartsWith("->"))
            rs = t[(rstart + 2)..];
        else
            rs = t[rstart..];
        return true;
    }

    /// <summary>
    /// Returns true when the class was declared as a native type with no Gata-visible fields.
    /// </summary>
    private bool IsOpaqueStruct(string cls)
    {
        return nativeStructs.Contains(cls);
    }

    /// <summary>
    /// Returns true when the class has either a native struct body or raw C field blocks.
    /// </summary>
    private bool HasOpaqueFields(string cls)
    {
        return nativeStructs.Contains(cls) || opaqueFieldClasses.Contains(cls);
    }

    /// <summary>
    /// Validates that the given Gata type name refers to a real, in-scope type.
    /// Reports a diagnostic on any unknown or out-of-scope name.
    /// </summary>
    private void CheckType(string? gataType, ResolveCtx ctx, TextSpan span, bool allowVoid = false)
    {
        CheckType(gataType.AsSpan(), ctx, span, allowVoid);
    }

    /// <summary>
    /// Validates that the given Gata type name refers to a real, in-scope type.
    /// Reports a diagnostic on any unknown or out-of-scope name.
    /// </summary>
    private void CheckType(ReadOnlySpan<char> gataType, ResolveCtx ctx, TextSpan span, bool allowVoid = false)
    {
        if (gataType.IsEmpty) return;
        if (gataType.StartsWith("func("))
        {
            if (TrySplitFuncType(gataType, out var ps, out var rs))
            {
                foreach (var p in ps) CheckType(gataType[p], ctx, span);
                CheckType(rs, ctx, span, allowVoid: true);
            }
            else
                diag.Error(Codes.UndefinedType, ctx.File, span, $"malformed function type '{gataType.ToString()}'");
            return;
        }
        if (gataType.StartsWith("["))
        {
            int close = gataType.IndexOf(']');
            bool ok = close > 0 && TryParseIntLit(gataType[1..close], out var n, out _, out _) && n > 0;
            if (!ok)
                diag.Error(Codes.UndefinedType, ctx.File, span, $"invalid fixed-array size in '{gataType.ToString()}'");
            else
                CheckType(gataType[(close + 1)..], ctx, span);
            return;
        }
        bool hadPtr = gataType.Contains('*');
        ReadOnlySpan<char> bSpan = gataType;
        while (bSpan.EndsWith("*")) bSpan = bSpan[..^1];
        bSpan = bSpan.Trim();
        if (bSpan.Length == 0) return;
        if (bSpan.Equals("void", StringComparison.Ordinal))
        {
            if (!allowVoid && !hadPtr)
                diag.Error(Codes.UndefinedType, ctx.File, span, "'void' is not a value type");
            return;
        }
        if (SymbolTable.Primitives.GetAlternateLookup<ReadOnlySpan<char>>().Contains(bSpan)) return;
        if (bSpan.Equals("String", StringComparison.Ordinal) || bSpan.Equals("Process", StringComparison.Ordinal) || bSpan.Equals("Thread", StringComparison.Ordinal)) return;
        if (sym.IsEnum(bSpan)) return;
        if (sym.IsUnion(bSpan)) return;
        if (ClassInScope(bSpan)) return;
        diag.Error(Codes.UndefinedType, ctx.File, span,
            sym.IsClass(bSpan)
                ? $"type '{Mangler.DisplayName(bSpan.ToString())}' is not in scope; import its module"
                : $"unknown type '{Mangler.DisplayName(bSpan.ToString())}'");
    }

    /// <summary>
    /// Validates that no two parameters in the list share the same name.
    /// </summary>
    private void CheckParams(Param[] ps, ResolveCtx ctx)
    {
        var seen = new HashSet<string>();
        foreach (var p in ps)
            if (!seen.Add(p.Name))
                diag.Error(Codes.DuplicateName, ctx.File, p.Span, $"duplicate parameter '{p.Name}'");
    }

    /// <summary>
    /// Reports a diagnostic when the argument count does not match the expected parameter count.
    /// </summary>
    private void CheckArgCount(MethodSig? sig, int argCount, string display, ResolveCtx ctx, TextSpan span)
    {
        if (sig != null && sig.Params.Count != argCount)
            diag.Error(Codes.WrongArgCount, ctx.File, span,
                $"'{display}' expects {sig.Params.Count} argument(s), got {argCount}");
    }

    #region Overload resolution
    /// <summary>
    /// Picks the best-matching overload from the candidates for the given argument list.
    /// Reports a diagnostic when no overload matches or multiple overloads tie.
    /// </summary>
    private Symbol? ChooseOverload(IReadOnlyList<Symbol> cands, Symbol? primary,
                           List<IrExpr> args, string display, ResolveCtx ctx, TextSpan span)
    {
        if (cands.Count <= 1)
        {
            if (primary != null) CheckArgCount(primary.Sig, args.Count, display, ctx, span);
            return primary;
        }
        Symbol? best = null; int bestCost = int.MaxValue; bool tie = false;
        foreach (var c in cands)
        {
            int? cost = MatchCost(c.Sig!, args);
            if (cost == null) continue;
            if (cost < bestCost) { bestCost = cost.Value; best = c; tie = false; }
            else if (cost == bestCost) tie = true;
        }
        if (best == null)
        {
            diag.Error(Codes.NoMatchingOverload, ctx.File, span,
                $"no overload of '{display}' matches ({DescribeArgs(args)})");
            return null;
        }
        if (tie)
            diag.Error(Codes.AmbiguousOverload, ctx.File, span,
                $"call to '{display}' is ambiguous for ({DescribeArgs(args)})");
        return best;
    }

    /// <summary>
    /// Computes the total conversion cost for matching the given argument list to the signature.
    /// Returns null when the argument count or any individual argument type is incompatible.
    /// </summary>
    private int? MatchCost(MethodSig sig, List<IrExpr> args)
    {
        if (sig.Params.Count != args.Count) return null;
        int total = 0;
        for (int i = 0; i < args.Count; i++)
        {
            int? c = ArgConvCost(args[i], ResolveType(sig.Params[i].Type));
            if (c == null) return null;
            total += c.Value;
        }
        return total;
    }

    // 0 = exact, 1 = widening numeric, 2 = narrowing numeric, null = incompatible.
    /// <summary>
    /// Returns the conversion cost from the argument's type to the target type,
    /// or null when the types are incompatible.
    /// </summary>
    private static int? ArgConvCost(IrExpr arg, IrType to)
    {
        var from = arg.Type;
        if (arg is IrLitNull) return to is IrClassRef or IrPtrType ? 0 : null;
        if (SameType(from, to)) return 0;
        if ((from.IsNumeric || from.IsFloat) && (to.IsNumeric || to.IsFloat))
            return NumRank(from) <= NumRank(to) ? 1 : 2;
        if (from.IsString && to.IsString) return 0;
        if (from is IrPtrType fp && to is IrPtrType tp
            && (SameType(fp.Inner, tp.Inner) || fp.Inner is IrVoidType || tp.Inner is IrVoidType))
            return 1;
        return null;
    }

    /// <summary>
    /// Returns the numeric promotion rank of the type, used to resolve binary operator widening.
    /// </summary>
    private static int NumRank(IrType t)
    {
        return t is IrPrimType p ? p.CName switch
        {
            "bool" => 1,
            "char" or "sbyte" or "byte" => 2,
            "short" or "ushort" => 3,
            "int" or "uint" => 4,
            "int64" or "uint64" or "usize" or "uintptr" => 5,
            "float" => 6,
            "double" => 7,
            _ => 4
        } : 4;
    }

    /// <summary>
    /// Formats the argument type list as a comma-separated string for use in diagnostic messages.
    /// </summary>
    private static string DescribeArgs(List<IrExpr> args)
    {
        var names = new string[args.Count];
        for (int i = 0; i < args.Count; i++) names[i] = Describe(args[i].Type);
        return string.Join(", ", names);
    }

    #endregion

    #region Type compatibility
    /// <summary>
    /// Returns true when both IR types are structurally identical.
    /// </summary>
    private static bool SameType(IrType a, IrType b)
    {
        return (a, b) switch
        {
            (IrVoidType, IrVoidType) => true,
            (IrPrimType x, IrPrimType y) => x.CName == y.CName,
            (IrClassRef x, IrClassRef y) => x.ClassName == y.ClassName,
            (IrEnumType x, IrEnumType y) => x.Name == y.Name,
            (IrPtrType x, IrPtrType y) => SameType(x.Inner, y.Inner),
            (IrArrayType x, IrArrayType y) => x.Size == y.Size && SameType(x.Elem, y.Elem),
            (IrResultType x, IrResultType y) => SameType(x.Inner, y.Inner),
            (IrFuncPtrType x, IrFuncPtrType y) => SameFuncPtrParams(x, y),
            (IrUnionType x, IrUnionType y) => x.Name == y.Name,
            _ => false
        };
    }

    /// <summary>
    /// Returns true when two function pointer types have the same return type and
    /// pairwise-identical parameter types, without allocating a LINQ enumerator.
    /// </summary>
    private static bool SameFuncPtrParams(IrFuncPtrType x, IrFuncPtrType y)
    {
        if (!SameType(x.Ret, y.Ret) || x.Params.Count != y.Params.Count) return false;
        for (int i = 0; i < x.Params.Count; i++)
            if (!SameType(x.Params[i], y.Params[i])) return false;
        return true;
    }

    /// <summary>
    /// Returns true when value's type is assignment-compatible with the target type,
    /// accounting for implicit numeric widening, null-to-reference, and pointer covariance.
    /// </summary>
    private static bool Assignable(IrExpr value, IrType to)
    {
        var from = value.Type;
        if (value is IrLitNull) return to is IrClassRef or IrPtrType or IrFuncPtrType;
        if (SameType(from, to)) return true;
        if (to is IrVoidType) return false;
        if (value is IrLitInt or IrLitChar && IsNum(to)) return true;
        if (value is IrLitFloat && to.IsFloat) return true;
        if (IsNum(from) && IsNum(to)) return NumRank(from) <= NumRank(to);
        if (from.IsString && to.IsString) return true;
        if (from is IrPtrType fp && to is IrPtrType tp)
            return SameType(fp.Inner, tp.Inner) || fp.Inner is IrVoidType || tp.Inner is IrVoidType;
        if (from is IrClassRef fc && to is IrClassRef tc) return fc.ClassName == tc.ClassName;
        return false;
    }

    /// <summary>
    /// Returns a human-readable type name for use in diagnostic messages.
    /// </summary>
    private static string Describe(IrType t)
    {
        return t switch
        {
            IrVoidType => "void",
            IrPrimType p => p.CName,
            IrClassRef c => Mangler.DisplayName(c.ClassName),
            IrPtrType p => Describe(p.Inner) + "*",
            IrArrayType a => $"[{a.Size}]{Describe(a.Elem)}",
            IrResultType r => "throws " + Describe(r.Inner),
            IrFuncPtrType f => DescribeFuncPtr(f),
            IrUnionType u => u.Name,
            _ => t.ToCType()
        };
    }

    /// <summary>
    /// Returns the human-readable signature string for a function pointer type.
    /// </summary>
    private static string DescribeFuncPtr(IrFuncPtrType f)
    {
        var pnames = new string[f.Params.Count];
        for (int i = 0; i < f.Params.Count; i++) pnames[i] = Describe(f.Params[i]);
        return $"func({string.Join(", ", pnames)}) -> {Describe(f.Ret)}";
    }

    /// <summary>
    /// Reports a type-mismatch diagnostic when value cannot be assigned to the target type.
    /// </summary>
    private void CheckAssign(IrExpr value, IrType target, string what, ResolveCtx ctx, string code)
    {
        if (value.Type is IrResultType || target is IrResultType) return;
        if (!Assignable(value, target))
            diag.Error(code, ctx.File, value.Span,
                $"cannot assign '{Describe(value.Type)}' to {what} of type '{Describe(target)}'");
    }

    /// <summary>
    /// Emits an EmptyBlock warning when the resolved block has no statements.
    /// </summary>
    private void WarnIfEmpty(IrBlock blk, string what, ResolveCtx ctx, TextSpan span)
    {
        if (blk.Stmts.Count == 0)
            diag.Warn(Codes.EmptyBlock, ctx.File, span, $"empty '{what}' body");
    }

    /// <summary>
    /// Validates that the expression is bool-typed for use as a branch condition.
    /// </summary>
    private void CheckCondition(IrExpr c, ResolveCtx ctx)
    {
        if (c.Type is IrResultType) return;
        if (c.Type is not IrPrimType { CName: "bool" })
            diag.Error(Codes.ConditionNotBool, ctx.File, c.Span,
                $"condition must be 'bool', got '{Describe(c.Type)}'");
    }

    /// <summary>
    /// Validates that the expression is a legal assignment target (variable, field, element, or deref).
    /// </summary>
    private void CheckLValue(IrExpr target, ResolveCtx ctx)
    {
        if (target is IrVar or IrFieldLoad or IrIndex or IrDeref) return;
        diag.Error(Codes.NotAnLvalue, ctx.File, target.Span,
            "assignment target must be a variable, field, or element");
    }

    /// <summary>
    /// Validates both operands of a compound assignment operator for type correctness.
    /// </summary>
    private void CheckCompound(AssignOp op, IrExpr target, IrExpr value, ResolveCtx ctx)
    {
        bool bitwise = op.IsBitwise();
        bool okTarget = bitwise ? IsInteger(target.Type) : IsArith(target.Type);
        bool okValue = bitwise ? IsInteger(value.Type) : IsArith(value.Type);
        if (!okTarget)
            diag.Error(Codes.TypeMismatch, ctx.File, target.Span,
                $"operator '{op.Sym()}' cannot be applied to '{Describe(target.Type)}'");
        else if (!okValue)
            diag.Error(Codes.TypeMismatch, ctx.File, value.Span,
                $"operator '{op.Sym()}' requires a{(bitwise ? "n integer" : " numeric")} right-hand side, got '{Describe(value.Type)}'");
    }

    /// <summary>
    /// Validates that an explicit cast is valid: numeric, enum-to-int, or pointer (unsafe only).
    /// Reports an error for void, String, or class casts.
    /// </summary>
    private void CheckCast(IrExpr value, IrType to, ResolveCtx ctx)
    {
        var from = value.Type;
        if (SameType(from, to)) return;
        if (value is IrLitNull && to is IrClassRef or IrPtrType) return;
        bool numeric = IsNum(from) && IsNum(to);
        bool enumInt = (from is IrEnumType && IsInteger(to)) || (IsInteger(from) && to is IrEnumType);
        bool pointer = (from is IrPtrType || to is IrPtrType)
                       && (from is IrPtrType or IrPrimType) && (to is IrPtrType or IrPrimType);
        if (from is IrVoidType || to is IrVoidType) { Reject(); return; }
        if (numeric || enumInt) return;
        if (pointer)
        {
            if (!ctx.InUnsafe)
                diag.Error(Codes.UnsafeRequired, ctx.File, value.Span, "pointer cast requires an 'unsafe' block");
            return;
        }
        Reject();
        void Reject() => diag.Error(Codes.InvalidCast, ctx.File, value.Span,
            $"cannot cast '{Describe(from)}' to '{Describe(to)}'");
    }

    /// <summary>
    /// Returns true when both expressions are comparable with == or !=.
    /// </summary>
    private static bool ComparableEq(IrExpr l, IrExpr r)
    {
        var a = l.Type; var b = r.Type;
        if (l is IrLitNull || r is IrLitNull)
            return (l is IrLitNull ? b : a) is IrClassRef or IrPtrType or IrFuncPtrType;
        if (IsNum(a) && IsNum(b)) return true;
        if (a.IsString && b.IsString) return true;
        if (a is IrPtrType && b is IrPtrType) return true;
        if (a is IrClassRef ca && b is IrClassRef cb) return ca.ClassName == cb.ClassName;
        if (a is IrEnumType ea && b is IrEnumType eb) return ea.Name == eb.Name;
        if (a is IrFuncPtrType && b is IrFuncPtrType) return SameType(a, b);
        return false;
    }

    #endregion

    #region Control-flow analysis
    /// <summary>
    /// Returns true when at least one statement in the list definitely returns on every path.
    /// </summary>
    private static bool ReturnsList(IReadOnlyList<IrStmt> stmts)
    {
        return stmts.Any(DefinitelyReturns);
    }

    /// <summary>
    /// Returns true when the statement definitely returns or throws on every execution path.
    /// </summary>
    private static bool DefinitelyReturns(IrStmt s)
    {
        return s switch
        {
            IrReturn => true,
            IrThrow => true,
            IrBlock b => ReturnsList(b.Stmts),
            IrUnsafeBlock u => ReturnsList(u.Body.Stmts),
            IrIf i => i.Else != null && DefinitelyReturns(i.Then) && DefinitelyReturns(i.Else),
            IrWhile w => w.Cond is IrLitBool { Value: true } && !HasLoopBreak(w.Body),
            IrFor f => (f.Cond == null || f.Cond is IrLitBool { Value: true }) && !HasLoopBreak(f.Body),
            IrPanic => true,
            IrTryCatch t => DefinitelyReturns(t.Try) && DefinitelyReturns(t.Catch),
            IrSwitch sw => sw.Default != null && sw.Cases.All(c => DefinitelyReturns(c.Body))
                           && DefinitelyReturns(sw.Default),
            IrMatch ms => ms.Cases.All(c => DefinitelyReturns(c.Body))
                          && (ms.Default == null || DefinitelyReturns(ms.Default)),
            _ => false
        };
    }

    /// <summary>
    /// Returns true when the statement contains a 'break' that would exit the enclosing loop.
    /// Does not descend into nested loops, whose breaks target the inner loop instead.
    /// </summary>
    private static bool HasLoopBreak(IrStmt s)
    {
        return s switch
        {
            IrBreak => true,
            IrBlock b => b.Stmts.Any(HasLoopBreak),
            IrUnsafeBlock u => HasLoopBreak(u.Body),
            IrIf i => HasLoopBreak(i.Then) || (i.Else != null && HasLoopBreak(i.Else)),
            IrTryCatch t => HasLoopBreak(t.Try) || HasLoopBreak(t.Catch),
            IrSwitch sw => sw.Cases.Any(c => HasLoopBreak(c.Body)) || (sw.Default != null && HasLoopBreak(sw.Default)),
            IrMatch m => m.Cases.Any(c => HasLoopBreak(c.Body)) || (m.Default != null && HasLoopBreak(m.Default)),
            _ => false
        };
    }

    /// <summary>
    /// Reports MissingReturn when a non-void function body does not definitely return on every path.
    /// </summary>
    private void CheckMissingReturn(IrBlock? body, IrType ret, bool isThrows, TextSpan span, string display, ResolveCtx ctx)
    {
        if (body == null || isThrows || ret is IrVoidType || ret is IrResultType) return;
        if (!ReturnsList(body.Stmts))
            diag.Error(Codes.MissingReturn, ctx.File, span, $"'{display}' must return '{Describe(ret)}' on every path");
    }

    /// <summary>
    /// Checks for a redundant trailing 'return;' in a void function, and warns about
    /// unused local variables by walking the body.
    /// </summary>
    private void CheckBodyQuality(IrBlock body, IrType ret, TextSpan span, ResolveCtx ctx)
    {
        if (ret is IrVoidType && body.Stmts.Count > 0 && body.Stmts[^1] is IrReturn { Value: null })
            diag.Warn(Codes.RedundantReturn, ctx.File, span, "redundant trailing 'return;'");

        var visitor = new BodyQualityVisitor();
        visitor.St(body);
        if (visitor.Native) return;
        var seen = new HashSet<string>();
        for (int i = 0; i < visitor.Decls.Count; i++)
        {
            var (name, sp) = visitor.Decls[i];
            if (seen.Add(name) && !visitor.Used.Contains(name))
                diag.Warn(Codes.UnusedVariable, ctx.File, sp, $"unused variable '{name}'");
        }
    }

    /// <summary>
    /// Stack-allocated visitor that collects variable declarations and usages in a function body,
    /// used by CheckBodyQuality to detect unused local variables without heap allocation.
    /// </summary>
    private struct BodyQualityVisitor
    {
        public readonly List<(string Name, TextSpan Span)> Decls = [];
        public readonly HashSet<string> Used = [];
        public bool Native = false;

        public BodyQualityVisitor() {}

        /// <summary>
        /// Records variable usages in the expression and recurses into sub-expressions.
        /// </summary>
        public void Ex(IrExpr? e)
        {
            if (e == null) return;
            if (e is IrVar v) Used.Add(v.Name);
            switch (e)
            {
                case IrFieldLoad fl: Ex(fl.Obj); break;
                case IrIndex ix: Ex(ix.Obj); Ex(ix.Idx); break;
                case IrStaticCall sc:
                    for (int i = 0; i < sc.Args.Count; i++) Ex(sc.Args[i]);
                    break;
                case IrInstanceCall ic:
                    Ex(ic.Recv);
                    for (int i = 0; i < ic.Args.Count; i++) Ex(ic.Args[i]);
                    break;
                case IrThrowsCall tc:
                    for (int i = 0; i < tc.Args.Count; i++) Ex(tc.Args[i]);
                    break;
                case IrThrowsInstanceCall ti:
                    Ex(ti.Recv);
                    for (int i = 0; i < ti.Args.Count; i++) Ex(ti.Args[i]);
                    break;
                case IrBinOp b: Ex(b.Left); Ex(b.Right); break;
                case IrTernary t: Ex(t.Cond); Ex(t.Then); Ex(t.Else); break;
                case IrUnaryOp u: Ex(u.Operand); break;
                case IrPostfix p: Ex(p.Operand); break;
                case IrCast c: Ex(c.Value); break;
                case IrNew n:
                    for (int i = 0; i < n.Args.Count; i++) Ex(n.Args[i]);
                    break;
                case IrNewInit ni:
                    for (int i = 0; i < ni.Args.Count; i++) Ex(ni.Args[i]);
                    for (int i = 0; i < ni.Inits.Count; i++) Ex(ni.Inits[i]);
                    break;
                case IrArrayLit al:
                    for (int i = 0; i < al.Elems.Count; i++) Ex(al.Elems[i]);
                    break;
                case IrInterp ip:
                    for (int i = 0; i < ip.Parts.Count; i++) Ex(ip.Parts[i]);
                    break;
                case IrAddrOf a: Ex(a.Target); break;
                case IrDeref d: Ex(d.Ptr); break;
                case IrIndirectCall ic:
                    Ex(ic.Target);
                    for (int i = 0; i < ic.Args.Count; i++) Ex(ic.Args[i]);
                    break;
                case IrUnionConstruct uc:
                    for (int i = 0; i < uc.Args.Count; i++) Ex(uc.Args[i]);
                    break;
                case IrUnionField uf: Ex(uf.Union); break;
            }
        }

        /// <summary>
        /// Records variable declarations and usages in the statement, recursing into nested statements.
        /// </summary>
        public void St(IrStmt s)
        {
            switch (s)
            {
                case IrDeclVar d: Decls.Add((d.Name, d.Span)); Ex(d.Init); break;
                case IrAssign a: Ex(a.Target); Ex(a.Value); break;
                case IrExprStmt es: Ex(es.Expr); break;
                case IrReturn r: Ex(r.Value); break;
                case IrIf i: Ex(i.Cond); St(i.Then); if (i.Else != null) St(i.Else); break;
                case IrWhile w: Ex(w.Cond); St(w.Body); break;
                case IrFor f: if (f.Init != null) St(f.Init); Ex(f.Cond); Ex(f.Step); St(f.Body); break;
                case IrForIn fi: Ex(fi.Collection); St(fi.Body); break;
                case IrTryCatch t: St(t.Try); St(t.Catch); break;
                case IrSwitch sw:
                    Ex(sw.Scrutinee);
                    for (int i = 0; i < sw.Cases.Count; i++)
                    {
                        var c = sw.Cases[i];
                        for (int j = 0; j < c.Labels.Count; j++) Ex(c.Labels[j]);
                        St(c.Body);
                    }
                    if (sw.Default != null) St(sw.Default);
                    break;
                case IrMatch ms:
                    Ex(ms.Scrutinee);
                    for (int i = 0; i < ms.Cases.Count; i++) St(ms.Cases[i].Body);
                    if (ms.Default != null) St(ms.Default);
                    break;
                case IrUnsafeBlock u: St(u.Body); break;
                case IrDefer dfr: St(dfr.Action); break;
                case IrBlock b:
                    for (int i = 0; i < b.Stmts.Count; i++) St(b.Stmts[i]);
                    break;
                case IrNativeStmt: Native = true; break;
            }
        }
    }

    #endregion

    #region Access and throws validation
    /// <summary>
    /// Reports PrivateMember when a private member is accessed from outside its declaring class.
    /// </summary>
    private void CheckMemberAccess(string owner, string member, ResolveCtx ctx, TextSpan span)
    {
        if (sym.IsPrivateMember(owner, member) && ctx.CurClass != owner)
            diag.Error(Codes.PrivateMember, ctx.File, span,
                $"'{Mangler.DisplayName(owner)}.{member}' is private and cannot be accessed from outside '{Mangler.DisplayName(owner)}'");
    }

    /// <summary>
    /// Reports ThrowsOutsideTry when a throwing call appears outside a try block or throws function.
    /// </summary>
    private void CheckThrowsHandled(ResolveCtx ctx, TextSpan span)
    {
        if (!ctx.InTry && !ctx.InThrowsFunc)
            diag.Error(Codes.ThrowsOutsideTry, ctx.File, span,
                "throwing call must be inside a 'try' block or a 'throws' function");
    }

    /// <summary>
    /// Reports ThrowsOutsideTry when a throwing call is nested inside a non-statement expression.
    /// The allowRoot flag permits the call itself at the top of the expression tree.
    /// </summary>
    private void ForbidNestedThrows(IrExpr? e, ResolveCtx ctx, bool allowRoot)
    {
        if (e == null) return;
        if (!allowRoot && e is IrThrowsCall or IrThrowsInstanceCall)
            diag.Error(Codes.ThrowsOutsideTry, ctx.File, e.Span,
                "throwing call cannot appear inside a larger expression");

        switch (e)
        {
            case IrFieldLoad fl: ForbidNestedThrows(fl.Obj, ctx, false); break;
            case IrIndex ix: ForbidNestedThrows(ix.Obj, ctx, false); ForbidNestedThrows(ix.Idx, ctx, false); break;
            case IrStaticCall sc:
                for (int i = 0; i < sc.Args.Count; i++) ForbidNestedThrows(sc.Args[i], ctx, false);
                break;
            case IrInstanceCall ic:
                ForbidNestedThrows(ic.Recv, ctx, false);
                for (int i = 0; i < ic.Args.Count; i++) ForbidNestedThrows(ic.Args[i], ctx, false);
                break;
            case IrThrowsCall tc:
                for (int i = 0; i < tc.Args.Count; i++) ForbidNestedThrows(tc.Args[i], ctx, false);
                break;
            case IrThrowsInstanceCall ti:
                ForbidNestedThrows(ti.Recv, ctx, false);
                for (int i = 0; i < ti.Args.Count; i++) ForbidNestedThrows(ti.Args[i], ctx, false);
                break;
            case IrBinOp b: ForbidNestedThrows(b.Left, ctx, false); ForbidNestedThrows(b.Right, ctx, false); break;
            case IrTernary t: ForbidNestedThrows(t.Cond, ctx, false); ForbidNestedThrows(t.Then, ctx, false); ForbidNestedThrows(t.Else, ctx, false); break;
            case IrUnaryOp u: ForbidNestedThrows(u.Operand, ctx, false); break;
            case IrPostfix p: ForbidNestedThrows(p.Operand, ctx, false); break;
            case IrCast c: ForbidNestedThrows(c.Value, ctx, false); break;
            case IrNew n:
                for (int i = 0; i < n.Args.Count; i++) ForbidNestedThrows(n.Args[i], ctx, false);
                break;
            case IrNewInit ni:
                for (int i = 0; i < ni.Args.Count; i++) ForbidNestedThrows(ni.Args[i], ctx, false);
                for (int i = 0; i < ni.Inits.Count; i++) ForbidNestedThrows(ni.Inits[i], ctx, false);
                break;
            case IrArrayLit al:
                for (int i = 0; i < al.Elems.Count; i++) ForbidNestedThrows(al.Elems[i], ctx, false);
                break;
            case IrInterp ip:
                for (int i = 0; i < ip.Parts.Count; i++) ForbidNestedThrows(ip.Parts[i], ctx, false);
                break;
            case IrAddrOf a: ForbidNestedThrows(a.Target, ctx, false); break;
            case IrDeref d: ForbidNestedThrows(d.Ptr, ctx, false); break;
            case IrIndirectCall ic:
                ForbidNestedThrows(ic.Target, ctx, false);
                for (int i = 0; i < ic.Args.Count; i++) ForbidNestedThrows(ic.Args[i], ctx, false);
                break;
            case IrUnionConstruct uc:
                for (int i = 0; i < uc.Args.Count; i++) ForbidNestedThrows(uc.Args[i], ctx, false);
                break;
            case IrUnionField uf: ForbidNestedThrows(uf.Union, ctx, false); break;
        }
    }

    #endregion

    #region IR utilities
    /// <summary>
    /// Returns true when the expression is side-effect-free and safe to re-emit multiple times.
    /// </summary>
    private static bool IsPure(IrExpr e)
    {
        return e switch
        {
            IrLitInt or IrLitChar or IrLitFloat or IrLitBool or IrLitString or IrLitNull
                or IrEnumConst or IrVar or IrSelfExpr or IrFuncRef or IrSizeof or IrDefault => true,
            IrFieldLoad fl => IsPure(fl.Obj),
            IrIndex ix => IsPure(ix.Obj) && IsPure(ix.Idx),
            IrUnionField uf => IsPure(uf.Union),
            IrUnaryOp u => IsPure(u.Operand),
            IrBinOp b => IsPure(b.Left) && IsPure(b.Right),
            IrCast c => IsPure(c.Value),
            IrAddrOf a => IsPure(a.Target),
            IrDeref d => IsPure(d.Ptr),
            _ => false
        };
    }

    /// <summary>
    /// Returns the expression unchanged when it is pure, or hoists it into a fresh
    /// declared temporary and returns a reference to that temp.
    /// </summary>
    private IrExpr HoistIfImpure(IrExpr e, string prefix, List<IrStmt> stmts)
    {
        if (IsPure(e)) return e;
        string name = Tmp(prefix);
        stmts.Add(new IrDeclVar(name, e.Type, e));
        return new IrVar(name, e.Type);
    }

    /// <summary>
    /// Collapses a statement list to a single statement when the list has exactly one entry,
    /// avoiding an unnecessary nested block in the common case.
    /// </summary>
    private static IrStmt Seq(List<IrStmt> stmts, TextSpan span)
    {
        return stmts.Count == 1 ? stmts[0] with { Span = span } : new IrBlock(stmts) { Span = span };
    }

    /// <summary>
    /// Computes the common type for two ternary arms, or null when they cannot be unified.
    /// </summary>
    private static IrType? UnifyTernary(IrExpr a, IrExpr b)
    {
        if (a is IrLitNull && b is IrLitNull) return null;
        if (a is IrLitNull) return b.Type is IrClassRef or IrPtrType ? b.Type : null;
        if (b is IrLitNull) return a.Type is IrClassRef or IrPtrType ? a.Type : null;
        if (SameType(a.Type, b.Type)) return a.Type;
        if (IsNum(a.Type) && IsNum(b.Type)) return NumRank(a.Type) >= NumRank(b.Type) ? a.Type : b.Type;
        if (a.Type.IsString && b.Type.IsString) return IrType.String;
        if (a.Type is IrPtrType ap && b.Type is IrPtrType bp)
            return SameType(ap.Inner, bp.Inner) ? a.Type
                : ap.Inner is IrVoidType ? a.Type
                : bp.Inner is IrVoidType ? b.Type : null;
        return null;
    }

    /// <summary>
    /// Adapts an expression to a unified type: retypes a null literal, casts a narrower numeric up.
    /// </summary>
    private static IrExpr CoerceTo(IrExpr e, IrType t)
    {
        if (e is IrLitNull) return new IrLitNull(t) { Span = e.Span };
        if (SameType(e.Type, t)) return e;
        if (IsNum(e.Type) && IsNum(t)) return new IrCast(t, e) { Span = e.Span };
        return e;
    }

    /// <summary>
    /// Coerces an expression to the expected type, currently narrowing fixed-array literal
    /// element types when the destination declares a specific element type.
    /// </summary>
    private IrExpr Coerce(IrExpr e, IrType expected, ResolveCtx ctx)
    {
        if (expected is IrArrayType at && e is IrArrayLit lit && lit.Elems.Count == at.Size)
        {
            var coerced = new List<IrExpr>(lit.Elems.Count);
            for (int i = 0; i < lit.Elems.Count; i++)
            {
                coerced.Add(Coerce(lit.Elems[i], at.Elem, ctx));
            }
            return new IrArrayLit(Arr(at.Elem, at.Size), coerced) { Span = e.Span };
        }
        return e;
    }

    /// <summary>
    /// Resolves an intrinsic role to its bound C name, emitting a diagnostic if no binding exists.
    /// </summary>
    private string Intrinsic(string role, ResolveCtx ctx, TextSpan span)
    {
        var n = sym.IntrinsicOrNull(role);
        if (n != null) return n;
        diag.Error(Codes.MissingIntrinsic, ctx.File, span, $"no libappa symbol provides @intrinsic({role})");
        return $"appa_MISSING_{role}";
    }

    /// <summary>
    /// Coerces an expression to string by dispatching to the appropriate stringify intrinsic
    /// or the class's ToString method. Reports a diagnostic when no conversion is available.
    /// </summary>
    private IrExpr EnsureString(IrExpr e, ResolveCtx ctx)
    {
        if (e.Type.IsString) return e;
        if (e.Type.IsFloat)
            return new IrStaticCall(Intrinsic(Roles.StringifyFloat, ctx, e.Span), IrType.String, [e]) { Span = e.Span };
        if (e.Type.IsNumeric)
            return new IrStaticCall(Intrinsic(Roles.StringifyInt, ctx, e.Span), IrType.String, [e]) { Span = e.Span };
        var cls = ClassNameOf(e.Type);
        if (cls != null && sym.LookupMethod(cls, "ToString") is { } ts)
            return new IrInstanceCall(e, ts.CName, IrType.String, []) { Span = e.Span };
        diag.Error(Codes.TypeMismatch, ctx.File, e.Span,
            cls != null
                ? $"'{Mangler.DisplayName(cls)}' has no 'String func ToString()' to convert it to a String"
                : $"'{Describe(e.Type)}' cannot be converted to a String");
        return new IrLitString("\"\"") { Span = e.Span };
    }

    /// <summary>
    /// Extracts the class name from a class-reference type, or null for non-class types.
    /// Follows one level of pointer indirection for pointer-to-class patterns.
    /// </summary>
    private static string? ClassNameOf(IrType t)
    {
        return t switch
        {
            IrClassRef cr => cr.ClassName,
            IrPtrType pt => ClassNameOf(pt.Inner),
            _ => null
        };
    }

    #endregion

    #region Scope stack
    /// <summary>
    /// Maintains the chain of lexical scopes for variable declarations, tracking ref parameters.
    /// </summary>
    private sealed class ScopeStack
    {
        private readonly ScopeStack? _parent;
        private readonly Dictionary<string, IrType> _vars;
        private readonly HashSet<string> _refs;

        /// <summary>
        /// Constructs a root scope with no parent.
        /// </summary>
        public ScopeStack() { _parent = null; _vars = []; _refs = []; }

        private ScopeStack(ScopeStack parent) { _parent = parent; _vars = []; _refs = []; }

        /// <summary>
        /// Creates a child scope nested inside this one.
        /// </summary>
        public ScopeStack Push()
        {
            return new(this);
        }

        /// <summary>
        /// Declares a variable with the given name and type in the current scope.
        /// When isRef is true, the variable is a ref parameter and emits pointer indirection.
        /// </summary>
        public void Declare(string name, IrType type, bool isRef = false)
        {
            _vars[name] = type;
            if (isRef) _refs.Add(name);
        }

        /// <summary>
        /// Returns true when the name is declared in this (not a parent) scope.
        /// </summary>
        public bool DeclaredHere(string name)
        {
            return _vars.ContainsKey(name);
        }

        /// <summary>
        /// Searches this scope and all parent scopes for the named variable.
        /// Returns its type, or null when not found.
        /// </summary>
        public IrType? Lookup(string name)
        {
            for (var s = this; s != null; s = s._parent)
                if (s._vars.TryGetValue(name, out var t)) return t;
            return null;
        }

        /// <summary>
        /// Returns true when the named variable resolves to a ref parameter in this or an enclosing scope.
        /// </summary>
        public bool IsRef(string name)
        {
            for (var s = this; s != null; s = s._parent)
                if (s._vars.ContainsKey(name)) return s._refs.Contains(name);
            return false;
        }
    }

    #endregion

    #region Resolve context
    /// <summary>
    /// Immutable resolution context that flows through the AST walk, carrying the current file,
    /// realm, class, function, and loop/unsafe/try depth information.
    /// </summary>
    private readonly record struct ResolveCtx(
        string File,
        string Context,
        string CurClass,
        string? CurFunc,
        bool InStatic,
        bool InUnsafe,
        bool InTry,
        bool InThrowsFunc,
        string CatchLabel,
        int LoopDepth,
        ScopeStack Scope,
        bool InDefer = false)
    {
        /// <summary>
        /// Returns a context with the current class updated.
        /// </summary>
        public ResolveCtx WithClass(string c)
        {
            return this with { CurClass = c };
        }

        /// <summary>
        /// Returns a context with the current function name updated.
        /// </summary>
        public ResolveCtx WithFunc(string f)
        {
            return this with { CurFunc = f };
        }

        /// <summary>
        /// Returns a context with the static flag updated.
        /// </summary>
        public ResolveCtx WithStatic(bool s)
        {
            return this with { InStatic = s };
        }

        /// <summary>
        /// Returns a context with the unsafe flag updated.
        /// </summary>
        public ResolveCtx WithUnsafe(bool u)
        {
            return this with { InUnsafe = u };
        }

        /// <summary>
        /// Returns a context that marks entry into a try block with the given catch label.
        /// </summary>
        public ResolveCtx WithTry(string label)
        {
            return this with { InTry = true, CatchLabel = label };
        }

        /// <summary>
        /// Returns a context with the throws-function flag updated.
        /// </summary>
        public ResolveCtx WithThrowsFunc(bool t)
        {
            return this with { InThrowsFunc = t };
        }

        /// <summary>
        /// Returns a context with the realm context string updated.
        /// </summary>
        public ResolveCtx WithContext(string ctx)
        {
            return this with { Context = ctx };
        }

        /// <summary>
        /// Returns a context that marks entry into a defer body.
        /// </summary>
        public ResolveCtx WithDefer()
        {
            return this with { InDefer = true };
        }

        /// <summary>
        /// Returns a context with a new child scope pushed.
        /// </summary>
        public ResolveCtx PushScope()
        {
            return this with { Scope = Scope.Push() };
        }
    }

    #endregion

    #region Type predicates
    /// <summary>
    /// Returns true when the type is any numeric type (integer or float).
    /// </summary>
    private static bool IsNum(IrType t)
    {
        return t.IsNumeric || t.IsFloat;
    }

    /// <summary>
    /// Returns true when the type is numeric and not bool. Used for arithmetic operators.
    /// </summary>
    private static bool IsArith(IrType t)
    {
        return IsNum(t) && t is not IrPrimType { CName: "bool" };
    }

    /// <summary>
    /// Returns true when the type is an integer type (not float, not bool).
    /// </summary>
    private static bool IsInteger(IrType t)
    {
        return t.IsNumeric && t is not IrPrimType { CName: "bool" };
    }

    #endregion

    #region Module resolution
    /// <summary>
    /// Resolves all programs in the compilation unit and returns the fully typed IrModule.
    /// Generic template instances discovered during resolution are stamped after the main pass.
    /// </summary>
    public IrModule Resolve(List<(Program prog, string file)> programs)
    {
        var module = new IrModule([], [], [], [], [], _arrays, [], sym, _funcPtrTypes, []);
        foreach (var (prog, file) in programs)
            CollectFuncTemplates(prog.Items, "none", file);
        foreach (var (prog, file) in programs)
        {
            _scope = visible.GetValueOrDefault(file, [file]);
            var ctx = new ResolveCtx(file, "none", "", null, false, false, false, false, "", 0, new ScopeStack());
            foreach (var item in prog.Items)
                ResolveTop(item, ctx, module);
        }
        DrainGenericInstances(module);
        return module;
    }

    /// <summary>
    /// Scans top-level items for generic function templates and registers them for on-demand instantiation.
    /// </summary>
    private void CollectFuncTemplates(TopLevel[] items, string context, string file)
    {
        foreach (var item in items)
            switch (item)
            {
                case FuncDecl fd when fd.GenericParams.Length > 0:
                    _funcTemplates[fd.Name] = (fd, file, context);
                    break;
                case ContextDecl cd:
                    CollectFuncTemplates(cd.Items, cd.Kind, file);
                    break;
            }
    }

    /// <summary>
    /// Reports an error for an unknown @preamble target and returns a safe fallback section.
    /// </summary>
    private (NativeSection, Visibility) Unknown(string target, ResolveCtx ctx, TextSpan span)
    {
        diag.Error(Codes.UnknownPreambleTarget, ctx.File, span,
            $"unknown @preamble target '{target}'; expected 'boot', 'kernel', or 'user'");
        return (NativeSection.Preamble, Visibility.Shared);
    }

    /// <summary>
    /// Resolves a single top-level declaration and adds its output to the module.
    /// </summary>
    private void ResolveTop(TopLevel item, ResolveCtx ctx, IrModule module)
    {
        switch (item)
        {
            case ImportDecl:
            case ExternFuncDecl:
                break;
            case EnvironmentDecl ed:
                if (ctx.Context != "none")
                    diag.Error(Codes.TypeMismatch, ctx.File, ed.Span,
                        "an 'environment' declaration is only valid at the top level of a file, not inside a context block");
                break;
            case NativeBlock nb:
            {
                if (nb.Annotations?.Any(a => a is KeepAnnotation) == true)
                    diag.Error(Codes.WrongAnnotationKind, ctx.File, nb.Span,
                        "'@keep' is not valid on a native block; use it on a free function");
                var preambles = nb.Annotations?.OfType<PreambleAnnotation>().ToList() ?? [];
                if (preambles.Count > 1)
                    diag.Error(Codes.WrongAnnotationKind, ctx.File, nb.Span,
                        "a native block can carry only one '@preamble'; remove the extra one(s)");
                var pre = preambles.FirstOrDefault();
                var (section, vis) = pre is null
                    ? (NativeSection.Types, VisOf(ctx.Context))
                    : pre.Target switch
                    {
                        "boot" => (NativeSection.Boot, Visibility.Kernel),
                        "kernel" => (NativeSection.Preamble, Visibility.Kernel),
                        "user" => (NativeSection.Preamble, Visibility.User),
                        _ => Unknown(pre.Target, ctx, nb.Span),
                    };
                module.NativeBlocks.Add(new IrNativeBlock(nb.Body.KernelC, nb.Body.UserC, vis, section));
                break;
            }
            case ClassDecl cd:
                module.Classes.Add(ResolveClass(cd, ctx));
                break;
            case ContextDecl cdecl:
                var inner = ctx.WithContext(cdecl.Kind);
                foreach (var i in cdecl.Items) ResolveTop(i, inner, module);
                break;
            case FuncDecl fd:
                if (fd.GenericParams.Length > 0) break;
                module.FreeFunctions.Add(ResolveFreeFunc(fd, ctx));
                break;
            case NativeTypeDecl nd:
                var (kc, uc) = NativeC.Split(nd.CBody);
                module.NativeTypes.Add(new IrNativeType(nd.Name, Mangler.Class(nd.Name), kc, uc, VisOf(ctx.Context)));
                break;
            case EnumDecl ed:
                module.Enums.Add(ResolveEnum(ed, ctx));
                break;
            case UnionDecl ud:
                module.Unions.Add(ResolveUnion(ud, ctx));
                break;
            case ProcessDecl pd:
                module.Processes.Add(ResolveProcess(pd, ctx));
                break;
        }
    }

    /// <summary>
    /// Maps a realm context string to its IR visibility.
    /// </summary>
    private static Visibility VisOf(string ctx)
    {
        return ctx switch
        {
            "kernel" => Visibility.Kernel,
            "user" => Visibility.User,
            _ => Visibility.Shared
        };
    }

    #endregion

    #region Type conversion
    /// <summary>
    /// Converts a Gata type string to its IR type. Returns IrVoidType for null or "void".
    /// Handles function types, fixed-array types, pointer types, primitives, enums, unions, and classes.
    /// </summary>
    public IrType ResolveType(string? t)
    {
        return ResolveType(t.AsSpan());
    }

    /// <summary>
    /// Converts a Gata type span to its IR type. Returns IrVoidType for null or "void".
    /// Handles function types, fixed-array types, pointer types, primitives, enums, unions, and classes.
    /// </summary>
    private IrType ResolveType(ReadOnlySpan<char> t)
    {
        if (t.IsEmpty || t.Equals("void", StringComparison.Ordinal)) return IrType.Void;
        if (t.StartsWith("func("))
        {
            if (TrySplitFuncType(t, out var ps, out var rs))
            {
                var resolvedParams = new List<IrType>(ps.Count);
                for (int i = 0; i < ps.Count; i++)
                {
                    resolvedParams.Add(ResolveType(t[ps[i]]));
                }
                return FnPtr(ResolveType(rs), resolvedParams);
            }
            return IrType.Void;
        }
        if (t.StartsWith("["))
        {
            int close = t.IndexOf(']');
            int n = TryParseIntLit(t[1..close], out var v, out _, out _) ? (int)v : 0;
            return Arr(ResolveType(t[(close + 1)..]), n);
        }
        if (t.EndsWith("*")) return new IrPtrType(ResolveType(t[..^1]));
        if (t.Equals("String", StringComparison.Ordinal)) return IrType.String;
        if (t.Equals("Process", StringComparison.Ordinal) || t.Equals("Thread", StringComparison.Ordinal)) return new IrPtrType(IrType.Void);
        if (PrimTypes.IsPrim(t)) return new IrPrimType(t.ToString());
        if (sym.IsEnum(t)) return new IrEnumType(t.ToString());
        if (sym.IsUnion(t)) return new IrUnionType(t.ToString());
        return new IrClassRef(t.ToString());
    }

    #endregion

    #region Declaration resolvers

    /// <summary>
    /// Resolves a class declaration, including all fields, methods, and operator overloads.
    /// </summary>
    private IrClass ResolveClass(ClassDecl cd, ResolveCtx ctx)
    {
        bool lib = ctx.Context == "none";
        var vis = VisOf(ctx.Context);
        var classCtx = ctx.WithClass(cd.Name);

        var rawFields = new List<RawFieldBlock>();
        var fields = new List<IrField>();
        var methods = new List<IrFunction>();
        var operators = new List<IrOperator>();
        var fieldInits = new Dictionary<string, IrExpr>();

        foreach (var m in cd.Members)
        {
            switch (m)
            {
                case FieldsBlock fb:
                    rawFields.Add(new RawFieldBlock(fb.Body.KernelC, fb.Body.UserC));
                    break;
                case FieldDecl fd:
                    CheckType(fd.Type, classCtx, fd.Span);
                    var ft = ResolveType(fd.Type ?? "void");
                    IrExpr? init = null;
                    if (fd.Init != null)
                    {
                        init = Coerce(ResolveExpr(fd.Init, classCtx.WithStatic(false)), ft, classCtx);
                        CheckAssign(init, ft, $"field '{fd.Name}'", classCtx, Codes.TypeMismatch);
                        fieldInits[fd.Name] = init;
                    }
                    fields.Add(new IrField(fd.Name, ft, init));
                    break;
                case MethodDecl md:
                    methods.Add(ResolveMethod(cd.Name, md, classCtx, lib, vis, cd.IsModule));
                    break;
                case OperatorDecl od:
                    operators.Add(ResolveOperator(cd.Name, od, classCtx, lib, vis));
                    break;
            }
        }

        return new IrClass(
            cd.Name, Mangler.Class(cd.Name), lib, vis,
            rawFields, fields, methods, operators,
            hasInit.Contains(cd.Name), fieldInits, cd.IsModule,
            Keep: cd.Annotations.Any(a => a is KeepAnnotation));
    }

    /// <summary>
    /// Resolves a method declaration, type-checking its signature and body,
    /// and declaring parameters and optionally 'self' in the method's scope.
    /// </summary>
    private IrFunction ResolveMethod(string cls, MethodDecl md, ResolveCtx ctx, bool lib, Visibility vis, bool isModule)
    {
        bool isStatic = md.Modifiers.HasFlag(Modifiers.Static) || isModule;
        if (!md.Throws) CheckType(md.ReturnType, ctx, md.Span, allowVoid: true);
        foreach (var p in md.Params) CheckType(p.Type, ctx, p.Span);
        CheckParams(md.Params, ctx);
        var ret = md.Throws ? ResolveType(md.ReturnType ?? "int") : ResolveType(md.ReturnType ?? "void");
        
        var pars = new List<IrParam>(md.Params.Length);
        for (int i = 0; i < md.Params.Length; i++)
        {
            var p = md.Params[i];
            pars.Add(new IrParam(p.Name, ResolveType(p.Type), p.IsRef));
        }

        string cname = Mangler.Method(cls, md.Name, md.Params, sym.IsOverloadedMethod(cls, md.Name));
        var mctx = ctx.WithClass(cls).WithFunc(md.Name).WithStatic(isStatic)
            .WithThrowsFunc(md.Throws).PushScope();
        if (!isStatic) mctx.Scope.Declare("self", new IrClassRef(cls));
        foreach (var p in md.Params) mctx.Scope.Declare(p.Name, ResolveType(p.Type), p.IsRef);
        var (body, nk, nu) = ResolveBodyOrNative(md.Body, mctx, ret);
        CheckMissingReturn(body, ret, md.Throws, md.Span, $"{Mangler.DisplayName(cls)}.{md.Name}", ctx);
        if (body != null) CheckBodyQuality(body, ret, md.Span, ctx);
        return new IrFunction(md.Name, cname, ret, pars, isStatic, md.IsEntry, md.Throws, lib, vis,
            cls, body, nk, nu, [..md.Annotations]);
    }

    /// <summary>
    /// Resolves an operator declaration, type-checking its signature and body
    /// and registering 'self' and all parameters in the operator's scope.
    /// </summary>
    private IrOperator ResolveOperator(string cls, OperatorDecl od, ResolveCtx ctx, bool lib, Visibility vis)
    {
        CheckType(od.ReturnType, ctx, od.Span, allowVoid: true);
        foreach (var p in od.Params) CheckType(p.Type, ctx, p.Span);
        CheckParams(od.Params, ctx);
        var ret = ResolveType(od.ReturnType ?? (od.Op == "[]=" ? "void" : cls));
        
        var pars = new List<IrParam>(od.Params.Length);
        for (int i = 0; i < od.Params.Length; i++)
        {
            var p = od.Params[i];
            pars.Add(new IrParam(p.Name, ResolveType(p.Type), p.IsRef));
        }

        string cname = Mangler.Operator(cls, od.Op);
        var octx = ctx.WithClass(cls).WithFunc($"op_{Mangler.OpSuffix(od.Op)}").WithStatic(false).PushScope();
        octx.Scope.Declare("self", new IrClassRef(cls));
        foreach (var p in od.Params) octx.Scope.Declare(p.Name, ResolveType(p.Type), p.IsRef);
        var (body, nk, nu) = ResolveBodyOrNative(od.Body, octx, ret);
        CheckMissingReturn(body, ret, false, od.Span, $"operator {od.Op} on {Mangler.DisplayName(cls)}", ctx);
        if (body != null) CheckBodyQuality(body, ret, od.Span, ctx);
        return new IrOperator(od.Op, cname, ret, pars, cls, lib, vis, body, nk, nu);
    }

    /// <summary>
    /// Resolves a free function declaration, type-checking its signature and body,
    /// and producing a fully typed IR function node.
    /// </summary>
    private IrFunction ResolveFreeFunc(FuncDecl fd, ResolveCtx ctx)
    {
        bool lib = ctx.Context == "none";
        var vis = VisOf(ctx.Context);
        if (!fd.Throws) CheckType(fd.ReturnType, ctx, fd.Span, allowVoid: true);
        foreach (var p in fd.Params) CheckType(p.Type, ctx, p.Span);
        CheckParams(fd.Params, ctx);
        var ret = fd.Throws ? ResolveType(fd.ReturnType ?? "int") : ResolveType(fd.ReturnType ?? "void");
        
        var pars = new List<IrParam>(fd.Params.Length);
        for (int i = 0; i < fd.Params.Length; i++)
        {
            var p = fd.Params[i];
            pars.Add(new IrParam(p.Name, ResolveType(p.Type), p.IsRef));
        }

        string cname = fd.Modifiers.HasFlag(Modifiers.Private)
            ? Mangler.PrivateFreeFunc(Mangler.FileToken(ctx.File), fd.Name, fd.Params,
                sym.PrivateFuncOverloads(ctx.File, fd.Name).Count > 1)
            : Mangler.FreeFunc(fd.Name, fd.Params, sym.IsOverloadedFunc(fd.Name), fd.IsEntry, isExtern: false);
        var fctx = ctx.WithFunc(fd.Name).WithStatic(true).WithThrowsFunc(fd.Throws).PushScope();
        foreach (var p in fd.Params) fctx.Scope.Declare(p.Name, ResolveType(p.Type), p.IsRef);
        var (body, nk, nu) = ResolveBodyOrNative(fd.Body, fctx, ret);
        CheckMissingReturn(body, ret, fd.Throws, fd.Span, fd.Name, ctx);
        if (body != null) CheckBodyQuality(body, ret, fd.Span, ctx);
        return new IrFunction(fd.Name, cname, ret, pars, true, fd.IsEntry, fd.Throws, lib, vis,
            null, body, nk, nu, [..fd.Annotations]);
    }

    /// <summary>
    /// Resolves a method body or native block, returning the IR block and raw C kernel/user strings.
    /// </summary>
    private (IrBlock? Body, string? Kernel, string? User) ResolveBodyOrNative(MethodBody b, ResolveCtx ctx, IrType ret)
    {
        return b switch
        {
            NativeMethodBody nmb => (null, nmb.Native.KernelC, nmb.Native.UserC),
            BlockBody bb => (ResolveBlock(bb.Block, ctx, ret), null, null),
            _ => (null, null, null)
        };
    }

    /// <summary>
    /// Resolves a process declaration to its IR form, resolving each thread's entry function.
    /// </summary>
    private IrProcess ResolveProcess(ProcessDecl pd, ResolveCtx ctx)
    {
        var vis = VisOf(ctx.Context);
        var threads = new List<IrThread>(pd.Threads.Length);
        for (int i = 0; i < pd.Threads.Length; i++)
        {
            var td = pd.Threads[i];
            if (td.Mode != null)
                diag.Error(Codes.ThreadModeNotAllowed, ctx.File, td.Span,
                    $"thread '{td.Name}' has explicit mode '{td.Mode}'; threads do not support 'foreground' or 'background' modifiers");
            string tFull = $"{pd.Name}_{td.Name}";
            threads.Add(new IrThread(td.Name, "foreground", tFull, ResolveThreadEntry(tFull, td.Entry, ctx, vis)));
        }
        return new IrProcess(pd.Name, pd.Mode, threads);
    }

    /// <summary>
    /// Resolves a thread entry function declaration, checking parameter types and building the IR body.
    /// Applies CheckBodyQuality so unused-variable warnings are emitted for entry code.
    /// </summary>
    private IrFunction ResolveThreadEntry(string fullName, EntryFuncDecl ef, ResolveCtx ctx, Visibility vis)
    {
        foreach (var p in ef.Params) CheckType(p.Type, ctx, p.Span);
        CheckParams(ef.Params, ctx);
        
        var pars = new List<IrParam>(ef.Params.Length);
        for (int i = 0; i < ef.Params.Length; i++)
        {
            var p = ef.Params[i];
            pars.Add(new IrParam(p.Name, ResolveType(p.Type)));
        }

        var fctx = ctx.WithStatic(true).PushScope();
        foreach (var p in ef.Params) fctx.Scope.Declare(p.Name, ResolveType(p.Type));
        var body = ResolveBlock(ef.Body, fctx, IrType.Void);
        CheckBodyQuality(body, IrType.Void, ef.Span, ctx);
        return new IrFunction(fullName, Mangler.ThreadEntry(fullName), IrType.Void, pars, true, true, false,
            false, vis, null, body, null, null, []);
    }

    /// <summary>
    /// Resolves a call to a generic free function by inferring type arguments from the
    /// supplied argument types, mangling the name, and queuing the instantiation for
    /// resolution after the main pass completes.
    /// </summary>
    private IrExpr ResolveGenericCall(
        (FuncDecl Decl, string File, string Context) t,
        List<IrExpr> args, ResolveCtx ctx, TextSpan span, Expr[]? astArgs = null)
    {
        var fd = t.Decl;
        string fallback = Mangler.FreeFunc(fd.Name, [], false, false, false);
        if (fd.Params.Length != args.Count)
        {
            diag.Error(Codes.WrongArgCount, ctx.File, span,
                $"generic '{fd.Name}' expects {fd.Params.Length} argument(s), got {args.Count}");
            return new IrStaticCall(fallback, IrType.Void, args);
        }

        var binds = new Dictionary<string, string>();
        for (int i = 0; i < fd.Params.Length; i++)
            if (!Monomorphizer.UnifyParam(fd.Params[i].Type, args[i].Type, fd.GenericParams, binds))
                diag.Error(Codes.ArgTypeMismatch, ctx.File, span,
                    $"in call to generic '{fd.Name}', argument {i + 1} ('{Describe(args[i].Type)}') conflicts with an earlier binding of the same type parameter");

        var missing = fd.GenericParams.Where(p => !binds.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            diag.Error(Codes.UndefinedType, ctx.File, span,
                $"cannot infer type argument {string.Join(", ", missing.Select(m => $"'{m}'"))} for generic '{fd.Name}' from its arguments");
            return new IrStaticCall(fallback, IrType.Void, args);
        }

        string mangled = fd.Name + "_" + string.Join("_", fd.GenericParams.Select(p => Monomorphizer.SanitizeTypeName(binds[p])));
        if (_genericSeen.Add(mangled))
            _genericQueue.Enqueue((fd, t.File, t.Context, binds, mangled));

        var concreteParams = Monomorphizer.SubParams(fd.Params, binds);
        string cname = Mangler.FreeFunc(mangled, concreteParams, overloaded: false, isEntry: false, isExtern: false);
        var ret = ResolveType(Monomorphizer.SubType(fd.ReturnType ?? (fd.Throws ? "int" : "void"), binds));
        CoerceArgs(args, new MethodSig(fd.ReturnType ?? "void", [..concreteParams], true, fd.Throws, false, [..fd.Annotations]), ctx, astArgs);

        if (fd.Throws) { CheckThrowsHandled(ctx, span); return new IrThrowsCall(cname, ret, args); }
        return new IrStaticCall(cname, ret, args);
    }

    /// <summary>
    /// Resolves every generic free-function instantiation queued during the main pass,
    /// substituting concrete type bindings and registering the result in the module.
    /// </summary>
    private void DrainGenericInstances(IrModule module)
    {
        while (_genericQueue.Count > 0)
        {
            var (fd, file, context, binds, mangled) = _genericQueue.Dequeue();
            var cMap = binds.ToDictionary(kv => kv.Key, kv => Monomorphizer.CTypeOf(kv.Value));
            var inst = new FuncDecl(fd.Modifiers, fd.Annotations,
                Monomorphizer.SubType(fd.ReturnType, binds), mangled, [],
                [..Monomorphizer.SubParams(fd.Params, binds)], fd.IsEntry, fd.Throws,
                Monomorphizer.SubBody(fd.Body, binds, cMap), fd.Span);
            _scope = visible.GetValueOrDefault(file, [file]);
            var ctx = new ResolveCtx(file, context, "", null, false, false, false, false, "", 0, new ScopeStack());
            module.FreeFunctions.Add(ResolveFreeFunc(inst, ctx));
        }
    }

    /// <summary>
    /// Checks whether a bare call is a retain/release ARC intrinsic and returns the appropriate
    /// IR node. Returns null for all other names.
    /// </summary>
    private IrExpr? TryResolveArcIntrinsic(string name, List<IrExpr> args, ResolveCtx ctx, TextSpan span)
    {
        var fsym = sym.LookupFreeFunc(name);
        if (fsym == null || !FuncInScope(fsym)) return null;
        bool isRetain = fsym.CName == sym.IntrinsicOrNull(Roles.Retain);
        bool isRelease = fsym.CName == sym.IntrinsicOrNull(Roles.Release);
        if (!isRetain && !isRelease) return null;
        if (!ctx.InUnsafe)
            diag.Error(Codes.UnsafeRequired, ctx.File, span, $"'{name}' requires an 'unsafe' block");
        if (args.Count != 1)
        {
            diag.Error(Codes.WrongArgCount, ctx.File, span, $"'{name}' expects 1 argument, got {args.Count}");
            return new IrLitInt(0);
        }
        var a = args[0];
        if (isRetain)
            return IsManagedRef(a.Type) ? new IrStaticCall(fsym.CName, a.Type, [a]) : a;
        return IsManagedRef(a.Type) ? new IrStaticCall(fsym.CName, IrType.Void, [a]) : new IrCast(IrType.Void, a);
    }

    /// <summary>
    /// Returns true for class-type values that participate in ARC reference counting.
    /// </summary>
    private bool IsManagedRef(IrType t)
    {
        return t is IrClassRef cr && sym.IsClass(cr.ClassName) && !sym.Modules.Contains(cr.ClassName);
    }

    /// <summary>
    /// Resolves an enum declaration to its IR form.
    /// Members may carry optional explicit integer values parsed from integer literals.
    /// </summary>
    private IrEnum ResolveEnum(EnumDecl ed, ResolveCtx ctx)
    {
        var members = new List<(string, string?)>();
        var seen = new HashSet<string>();
        foreach (var m in ed.Members)
        {
            if (!seen.Add(m.Name))
                diag.Error(Codes.DuplicateName, ctx.File, m.Span,
                    $"enum '{ed.Name}' already declares a member '{m.Name}'");
            string? cval = null;
            var (neg, lit) = m.Value is UnaryExpr { Op: UnOp.Neg, Operand: IntLitExpr nil }
                ? (true, nil) : (false, m.Value as IntLitExpr);
            if (lit != null && TryParseIntLit(lit.Value.AsSpan(), out var v, out _, out _))
                cval = neg ? (-v).ToString() : v.ToString();
            else if (m.Value != null)
                diag.Error(Codes.TypeMismatch, ctx.File, m.Span,
                    $"enum '{ed.Name}' member '{m.Name}' must be an integer literal");
            members.Add((m.Name, cval));
        }
        return new IrEnum(ed.Name, Mangler.Enum(ed.Name), members);
    }

    /// <summary>
    /// Resolves a union declaration to its IR form.
    /// Variant fields must be unmanaged value types; class references are rejected.
    /// </summary>
    private IrUnion ResolveUnion(UnionDecl ud, ResolveCtx ctx)
    {
        var variants = new List<IrUnionVariant>();
        var seen = new HashSet<string>();
        foreach (var v in ud.Variants)
        {
            if (!seen.Add(v.Name))
                diag.Error(Codes.DuplicateName, ctx.File, v.Span,
                    $"union '{ud.Name}' already declares a variant '{v.Name}'");
            var fields = new List<IrParam>();
            foreach (var f in v.Fields)
            {
                CheckType(f.Type, ctx, f.Span);
                var ft = ResolveType(f.Type);
                if (ft is IrClassRef)
                    diag.Error(Codes.TypeMismatch, ctx.File, f.Span,
                        $"union variant field '{f.Name}' has type '{Describe(ft)}', but union variant fields must be unmanaged value types (no String/class types)");
                fields.Add(new IrParam(f.Name, ft));
            }
            variants.Add(new IrUnionVariant(v.Name, Mangler.UnionTag(ud.Name, v.Name), fields));
        }
        return new IrUnion(ud.Name, Mangler.Union(ud.Name), variants);
    }

    #endregion

    #region Statement resolvers
    /// <summary>
    /// Resolves a block by pushing a new scope, resolving all statements, and warning on unreachable code.
    /// </summary>
    private IrBlock ResolveBlock(Block b, ResolveCtx ctx, IrType retType)
    {
        var inner = ctx.PushScope();
        var stmts = new List<IrStmt>();
        foreach (var s in b.Stmts) stmts.Add(ResolveStmt(s, inner, retType));
        for (int i = 1; i < stmts.Count; i++)
            if (DefinitelyReturns(stmts[i - 1]) || stmts[i - 1] is IrBreak or IrContinue)
            {
                diag.Warn(Codes.UnreachableCode, ctx.File, stmts[i].Span, "unreachable code");
                break;
            }
        return new IrBlock(stmts) { Span = b.Span };
    }

    /// <summary>
    /// Resolves a statement, propagating the source span to the result when the resolver did not set one.
    /// </summary>
    private IrStmt ResolveStmt(Stmt s, ResolveCtx ctx, IrType retType)
    {
        var r = ResolveStmtCore(s, ctx, retType);
        return r.Span.IsNone ? r with { Span = s.Span } : r;
    }

    /// <summary>
    /// Core statement resolver. Handles all statement forms: native blocks, let declarations,
    /// assignments, control flow, loops, try/catch, defer, match, switch, and panic/debug/throw.
    /// </summary>
    private IrStmt ResolveStmtCore(Stmt s, ResolveCtx ctx, IrType retType)
    {
        switch (s)
        {
            case NativeStmt ns: return new IrNativeStmt(ns.Body.KernelC, ns.Body.UserC);
            case Block b: return ResolveBlock(b, ctx, retType);
            case LetStmt ls: return ResolveLet(ls, ctx);

            case AssignStmt asgn:
            {
                if (asgn.Target is IndexExpr ixt)
                    return ResolveIndexAssign(ixt, asgn, ctx);

                var target = ResolveExpr(asgn.Target, ctx);
                var value = ResolveExpr(asgn.Value, ctx);
                CheckLValue(target, ctx);
                if (asgn.Op == AssignOp.Assign)
                {
                    value = Coerce(value, target.Type, ctx);
                    CheckAssign(value, target.Type, "the assignment target", ctx, Codes.TypeMismatch);
                    ForbidNestedThrows(value, ctx, allowRoot: false);
                    return new IrAssign(target, AssignOp.Assign, value);
                }
                string baseOp = asgn.Op.BaseOp()!.Value.Sym();
                string? lhsClass = ClassNameOf(target.Type);
                if (lhsClass != null && sym.LookupOperator(lhsClass, baseOp) is { } opSym)
                {
                    var composed = new IrStaticCall(opSym.CName, ResolveType(opSym.Type), [target, value]);
                    CheckAssign(composed, target.Type, "the assignment target", ctx, Codes.TypeMismatch);
                    ForbidNestedThrows(composed, ctx, allowRoot: false);
                    return new IrAssign(target, AssignOp.Assign, composed);
                }
                CheckCompound(asgn.Op, target, value, ctx);
                ForbidNestedThrows(value, ctx, allowRoot: false);
                return new IrAssign(target, asgn.Op, value);
            }

            case ExprStmt es:
            {
                var e = ResolveExpr(es.E, ctx);
                ForbidNestedThrows(e, ctx, allowRoot: true);
                return new IrExprStmt(e);
            }

            case ReturnStmt rs:
            {
                if (ctx.InDefer)
                    diag.Error(Codes.TypeMismatch, ctx.File, rs.Span, "a 'defer' body cannot 'return'");
                if (rs.Value == null)
                {
                    if (retType is not IrVoidType && retType is not IrResultType)
                        diag.Error(Codes.ReturnTypeMismatch, ctx.File, rs.Span,
                            $"function must return '{Describe(retType)}'");
                    return new IrReturn(null);
                }
                var v = Coerce(ResolveExpr(rs.Value, ctx), retType, ctx);
                ForbidNestedThrows(v, ctx, allowRoot: false);
                CheckAssign(v, retType, "the function's return", ctx, Codes.ReturnTypeMismatch);
                return new IrReturn(v);
            }

            case IfStmt ifs:
            {
                var cond = ResolveExpr(ifs.Cond, ctx);
                ForbidNestedThrows(cond, ctx, allowRoot: false);
                CheckCondition(cond, ctx);
                var then = WrapBlock(ifs.Then, ctx, retType);
                var els = ifs.Else != null ? WrapBlock(ifs.Else, ctx, retType) : null;
                WarnIfEmpty(then, "if", ctx, ifs.Span);
                if (els != null) WarnIfEmpty(els, "else", ctx, ifs.Span);
                return new IrIf(cond, then, els);
            }

            case WhileStmt ws:
            {
                var cond = ResolveExpr(ws.Cond, ctx);
                ForbidNestedThrows(cond, ctx, allowRoot: false);
                CheckCondition(cond, ctx);
                var body = WrapBlock(ws.Body, ctx with { LoopDepth = ctx.LoopDepth + 1 }, retType);
                WarnIfEmpty(body, "while", ctx, ws.Span);
                return new IrWhile(cond, body);
            }

            case ForStmt fs:
                return ResolveFor(fs, ctx, retType);

            case ForInStmt fi:
                return ResolveForIn(fi, ctx, retType);

            case UnsafeBlock ub:
            {
                var uctx = ctx.WithUnsafe(true).PushScope();
                var stmts = new List<IrStmt>(ub.Stmts.Length);
                for (int i = 0; i < ub.Stmts.Length; i++)
                {
                    stmts.Add(ResolveStmt(ub.Stmts[i], uctx, retType));
                }
                return new IrUnsafeBlock(new IrBlock(stmts) { Span = ub.Span });
            }

            case SwitchStmt sw:
                return ResolveSwitch(sw, ctx, retType);

            case MatchStmt ms:
                return ResolveMatch(ms, ctx, retType);

            case BreakStmt:
                if (ctx.LoopDepth == 0)
                    diag.Error(Codes.BreakOutsideLoop, ctx.File, s.Span, "'break' is only valid inside a loop");
                if (ctx.InDefer)
                    diag.Error(Codes.TypeMismatch, ctx.File, s.Span, "a 'defer' body cannot 'break'");
                return new IrBreak();

            case ContinueStmt:
                if (ctx.LoopDepth == 0)
                    diag.Error(Codes.BreakOutsideLoop, ctx.File, s.Span, "'continue' is only valid inside a loop");
                if (ctx.InDefer)
                    diag.Error(Codes.TypeMismatch, ctx.File, s.Span, "a 'defer' body cannot 'continue'");
                return new IrContinue();

            case TryCatchStmt tc:
                return ResolveTryCatch(tc, ctx, retType);

            case DeferStmt ds:
            {
                if (ds.Action is DeferStmt)
                    diag.Error(Codes.TypeMismatch, ctx.File, ds.Span, "a 'defer' body cannot itself 'defer'");
                var dctx = ctx.WithDefer().PushScope();
                return new IrDefer(ResolveStmt(ds.Action, dctx, retType));
            }

            case ThrowStmt:
                if (ctx.InDefer)
                    diag.Error(Codes.TypeMismatch, ctx.File, s.Span, "a 'defer' body cannot 'throw'");
                CheckThrowsHandled(ctx, s.Span);
                return new IrThrow();

            case DebugStmt d:
                if (releaseMode)
                    diag.Error(Codes.DiagInRelease, ctx.File, s.Span,
                        "'debug' is not allowed in a release build -- remove it before shipping");
                return new IrDebug(d.Raw) { Span = s.Span };

            case PanicStmt p:
                if (releaseMode)
                    diag.Error(Codes.DiagInRelease, ctx.File, s.Span,
                        "'panic' is not allowed in a release build -- remove it before shipping");
                if (ctx.Context != "kernel")
                    diag.Error(Codes.PanicOutsideKernel, ctx.File, s.Span,
                        "'panic' is only valid in the kernel realm");
                return new IrPanic(p.Raw) { Span = s.Span };

            default:
                throw new NotImplementedException($"[TypeResolver] unhandled Stmt: {s.GetType().Name} -- additional statement forms added in later commits");
        }
    }

    /// <summary>
    /// Wraps a single statement in an IrBlock, pushing a new scope.
    /// When the statement is already a Block, resolves it directly without double-wrapping.
    /// </summary>
    private IrBlock WrapBlock(Stmt s, ResolveCtx ctx, IrType retType)
    {
        if (s is Block b) return ResolveBlock(b, ctx, retType);
        var inner = ctx.PushScope();
        return new IrBlock([ResolveStmt(s, inner, retType)]) { Span = s.Span };
    }

    /// <summary>
    /// Resolves a for statement, handling let, assignment, and expression init clauses
    /// in a new scope with the loop depth incremented.
    /// </summary>
    private IrStmt ResolveFor(ForStmt fs, ResolveCtx ctx, IrType retType)
    {
        var fctx = ctx.PushScope() with { LoopDepth = ctx.LoopDepth + 1 };
        IrStmt? init = null;
        if (fs.Init is LetStmt ls) init = ResolveLet(ls, fctx);
        else if (fs.Init is AssignStmt asgn)
        {
            var t = ResolveExpr(asgn.Target, fctx);
            var v = ResolveExpr(asgn.Value, fctx);
            CheckLValue(t, fctx);
            if (asgn.Op == AssignOp.Assign) { v = Coerce(v, t.Type, fctx); CheckAssign(v, t.Type, "the assignment target", fctx, Codes.TypeMismatch); }
            else CheckCompound(asgn.Op, t, v, fctx);
            init = new IrAssign(t, asgn.Op, v) { Span = asgn.Span };
        }
        else if (fs.Init is ExprStmt es) init = new IrExprStmt(ResolveExpr(es.E, fctx)) { Span = es.Span };
        IrExpr? cond = fs.Cond != null ? ResolveExpr(fs.Cond, fctx) : null;
        IrExpr? step = fs.Step != null ? ResolveExpr(fs.Step, fctx) : null;
        ForbidNestedThrows(cond, fctx, allowRoot: false);
        if (cond != null) CheckCondition(cond, fctx);
        ForbidNestedThrows(step, fctx, allowRoot: false);
        var body = ResolveBlock(fs.Body, fctx, retType);
        WarnIfEmpty(body, "for", fctx, fs.Span);
        return new IrFor(init, cond, step, body);
    }

    /// <summary>
    /// Resolves a for-in statement over a fixed array or any class with Length and Get methods.
    /// </summary>
    private IrStmt ResolveForIn(ForInStmt fi, ResolveCtx ctx, IrType retType)
    {
        var collection = ResolveExpr(fi.Collection, ctx);
        ForbidNestedThrows(collection, ctx, allowRoot: false);

        if (collection.Type is IrArrayType at)
        {
            var ainner = ctx.PushScope() with { LoopDepth = ctx.LoopDepth + 1 };
            ainner.Scope.Declare(fi.Var, at.Elem);
            var abody = ResolveBlock(fi.Body, ainner, retType);
            WarnIfEmpty(abody, "for..in", ctx, fi.Span);
            return new IrForIn(fi.Var, at.Elem, "", "", collection, abody, at.Size);
        }

        // Structural for..in: any class with zero-arg Length() -> int and single-int-arg Get(int) -> T.
        string? collClass = ClassNameOf(collection.Type);
        string lenCName = "", getCName = "";
        IrType elemType;
        var lenSym = collClass != null ? sym.LookupMethod(collClass, "Length") : null;
        var getSym = collClass != null ? sym.LookupMethod(collClass, "Get") : null;
        bool lengthOk = lenSym is { Sig.Params.Count: 0 } && IsInteger(ResolveType(lenSym.Type));
        bool getOk = getSym is { Sig.Params: [{ Type: var gpType }] } && IsInteger(ResolveType(gpType));
        if (lengthOk && getOk)
        {
            lenCName = lenSym!.CName;
            getCName = getSym!.CName;
            elemType = ResolveType(getSym.Type);
        }
        else
        {
            string why = collClass == null ? "" :
                !lengthOk && !getOk ? " (no 'Length() -> int' or 'Get(int)' method)" :
                !lengthOk ? " (no 'Length() -> int' method)" : " (no 'Get(int)' method)";
            diag.Error(Codes.NotIterable, ctx.File, fi.Collection.Span,
                $"'{Describe(collection.Type)}' is not iterable with 'for..in'{why}");
            elemType = IrType.Int;
        }

        var inner = ctx.PushScope() with { LoopDepth = ctx.LoopDepth + 1 };
        inner.Scope.Declare(fi.Var, elemType);
        var body = ResolveBlock(fi.Body, inner, retType);
        WarnIfEmpty(body, "for..in", ctx, fi.Span);
        return new IrForIn(fi.Var, elemType, lenCName, getCName, collection, body);
    }

    /// <summary>
    /// Resolves a switch statement on an integer or enum scrutinee,
    /// validating that each case label is comparable to the scrutinee type.
    /// </summary>
    private IrStmt ResolveSwitch(SwitchStmt sw, ResolveCtx ctx, IrType retType)
    {
        var scrut = ResolveExpr(sw.Scrutinee, ctx);
        ForbidNestedThrows(scrut, ctx, allowRoot: false);
        if (!(IsInteger(scrut.Type) || scrut.Type is IrEnumType))
            diag.Error(Codes.TypeMismatch, ctx.File, sw.Scrutinee.Span,
                $"switch requires an integer or enum value, got '{Describe(scrut.Type)}'");
        var cases = new List<IrSwitchCase>();
        var seenLabels = new HashSet<string>();
        foreach (var c in sw.Cases)
        {
            var labels = new List<IrExpr>(c.Labels.Length);
            for (int i = 0; i < c.Labels.Length; i++)
            {
                labels.Add(ResolveExpr(c.Labels[i], ctx));
            }
            for (int i = 0; i < labels.Count; i++)
            {
                var lbl = labels[i];
                if (!ComparableEq(scrut, lbl))
                    diag.Error(Codes.TypeMismatch, ctx.File, lbl.Span,
                        $"case label of type '{Describe(lbl.Type)}' is not comparable to the switch value '{Describe(scrut.Type)}'");
                if (ConstLabelKey(lbl) is { } key && !seenLabels.Add(key))
                    diag.Error(Codes.DuplicateName, ctx.File, lbl.Span,
                        "this 'case' value is already handled by an earlier arm");
            }
            cases.Add(new IrSwitchCase(labels, ResolveBlock(c.Body, ctx, retType)));
        }
        var def = sw.Default == null ? null : ResolveBlock(sw.Default, ctx, retType);
        return new IrSwitch(scrut, cases, def);
    }

    /// <summary>
    /// Returns a duplicate-detection key for a constant case label, or null for
    /// non-constant labels that cannot be checked at compile time.
    /// Int and char labels share a key space since C compares them as integers.
    /// </summary>
    private static string? ConstLabelKey(IrExpr lbl)
    {
        return lbl switch
        {
            IrLitInt li => "n:" + li.Value,
            IrLitChar lc => "n:" + lc.Codepoint,
            IrEnumConst ec => $"e:{ec.EnumName}.{ec.Member}",
            _ => null
        };
    }

    /// <summary>
    /// Resolves a match statement on a union scrutinee, binding each variant's fields
    /// into scope and checking exhaustiveness unless a default case is present.
    /// </summary>
    private IrStmt ResolveMatch(MatchStmt ms, ResolveCtx ctx, IrType retType)
    {
        var scrut = ResolveExpr(ms.Scrutinee, ctx);
        ForbidNestedThrows(scrut, ctx, allowRoot: false);
        if (scrut.Type is not IrUnionType ut)
        {
            diag.Error(Codes.TypeMismatch, ctx.File, ms.Scrutinee.Span,
                $"'match' requires a union value, got '{Describe(scrut.Type)}'");
            var fallbackCases = new List<IrMatchCase>(ms.Cases.Length);
            for (int i = 0; i < ms.Cases.Length; i++)
            {
                fallbackCases.Add(new IrMatchCase(0, [], ResolveBlock(ms.Cases[i].Body, ctx, retType)));
            }
            return new IrMatch(scrut, new IrUnionType("?"), fallbackCases,
                ms.Default == null ? null : ResolveBlock(ms.Default, ctx, retType));
        }
        var variants = sym.UnionDef(ut.Name)!;
        var cases = new List<IrMatchCase>();
        var covered = new HashSet<int>();
        foreach (var c in ms.Cases)
        {
            int idx = variants.FindIndex(v => v.Name == c.Variant);
            if (idx < 0)
            {
                diag.Error(Codes.UndefinedVariable, ctx.File, c.Span, $"union '{ut.Name}' has no variant '{c.Variant}'");
                cases.Add(new IrMatchCase(0, [], ResolveBlock(c.Body, ctx, retType)));
                continue;
            }
            if (!covered.Add(idx))
                diag.Error(Codes.DuplicateName, ctx.File, c.Span, $"variant '{c.Variant}' is already matched in this 'match'");
            var fields = variants[idx].Fields;
            if (c.Bindings.Length != fields.Length)
                diag.Error(Codes.WrongArgCount, ctx.File, c.Span,
                    $"'{c.Variant}' has {fields.Length} field(s), but {c.Bindings.Length} binding(s) were given");
            var caseCtx = ctx.PushScope();
            var binds = new List<IrMatchBind>();
            for (int i = 0; i < c.Bindings.Length && i < fields.Length; i++)
            {
                var ft = ResolveType(fields[i].Type);
                caseCtx.Scope.Declare(c.Bindings[i], ft);
                binds.Add(new IrMatchBind(fields[i].Name, c.Bindings[i], ft));
            }
            cases.Add(new IrMatchCase(idx, binds, ResolveBlock(c.Body, caseCtx, retType)));
        }
        var def = ms.Default == null ? null : ResolveBlock(ms.Default, ctx, retType);
        if (def == null && covered.Count < variants.Count)
        {
            var missingList = new List<string>();
            for (int i = 0; i < variants.Count; i++)
            {
                if (!covered.Contains(i)) missingList.Add(variants[i].Name);
            }
            diag.Error(Codes.NonExhaustiveMatch, ctx.File, ms.Span,
                $"'match' on '{ut.Name}' is not exhaustive; missing variant(s): {string.Join(", ", missingList)} (add a 'default' case or handle them all)");
        }
        return new IrMatch(scrut, ut, cases, def);
    }

    /// <summary>
    /// Resolves a try/catch statement, giving the try block a catch label so
    /// throwing calls inside it know where to jump on failure.
    /// </summary>
    private IrStmt ResolveTryCatch(TryCatchStmt tc, ResolveCtx ctx, IrType retType)
    {
        int seq = _labelSeq++;
        var tctx = ctx.WithTry($"_catch_{seq}");
        var tryBlock = ResolveBlock(tc.Try, tctx, retType);
        var catchBlock = ResolveBlock(tc.Catch, ctx, retType);
        return new IrTryCatch(tryBlock, catchBlock, seq);
    }

    /// <summary>
    /// Resolves a let declaration: infers or checks its type, resolves the initializer,
    /// checks assignability, and declares the variable in the current scope.
    /// </summary>
    private IrDeclVar ResolveLet(LetStmt ls, ResolveCtx ctx)
    {
        IrType type;
        IrExpr? init = ls.Init != null ? ResolveExpr(ls.Init, ctx) : null;

        if (ls.Type != null)
        {
            CheckType(ls.Type, ctx, ls.Span);
            type = ResolveType(ls.Type);
        }
        else if (init != null)
        {
            type = init.Type is IrResultType rt ? rt.Inner : init.Type;
            if (init is IrLitNull)
            {
                diag.Error(Codes.CannotInfer, ctx.File, ls.Span,
                    $"cannot infer a type for '{ls.Name}' from 'null'; give it an explicit type");
                type = IrType.Int;
            }
            else if (type is IrVoidType)
            {
                diag.Error(Codes.CannotInfer, ctx.File, ls.Span,
                    $"cannot declare '{ls.Name}': the initializer has no value (its type is 'void')");
                type = IrType.Int;
            }
        }
        else
        {
            diag.Error(Codes.CannotInfer, ctx.File, ls.Span,
                $"cannot infer a type for '{ls.Name}'; add a type ('let int {ls.Name};') or an initializer");
            type = IrType.Int;
        }

        if (init != null && init.Type is not IrResultType)
        {
            init = Coerce(init, type, ctx);
            if (ls.Type != null)
                CheckAssign(init, type, $"'{ls.Name}'", ctx, Codes.TypeMismatch);
        }

        if (init != null) ForbidNestedThrows(init, ctx, allowRoot: true);

        if (ctx.Scope.DeclaredHere(ls.Name))
            diag.Error(Codes.DuplicateName, ctx.File, ls.Span, $"'{ls.Name}' is already declared in this scope");
        ctx.Scope.Declare(ls.Name, type);
        return new IrDeclVar(ls.Name, type, init);
    }

    #endregion

    #region Expression resolvers
    /// <summary>
    /// Resolves an expression and propagates the source span when the resolver did not set one.
    /// </summary>
    private IrExpr ResolveExpr(Expr e, ResolveCtx ctx)
    {
        var r = ResolveExprCore(e, ctx);
        return r.Span.IsNone ? r with { Span = e.Span } : r;
    }

    /// <summary>
    /// Core expression resolver. Handles literals, identifiers, casts, postfix, unary, and binary expressions.
    /// Additional expression forms added in later commits.
    /// </summary>
    private IrExpr ResolveExprCore(Expr e, ResolveCtx ctx)
    {
        switch (e)
        {
            case IntLitExpr il:
                if (!TryParseIntLit(il.Value.AsSpan(), out var ival, out var ity, out var ictext))
                    diag.Error(Codes.TypeMismatch, ctx.File, e.Span,
                        $"integer literal '{il.Value}' does not fit in 64 bits");
                return new IrLitInt(ival, ity, ictext);
            case CharLitExpr cl: return new IrLitChar(cl.Value);
            case FloatLitExpr fl: return new IrLitFloat(fl.Value, FloatLitType(fl.Value));
            case BoolLitExpr bl: return new IrLitBool(bl.Value == "true");
            case StrLitExpr sl: return new IrLitString(sl.Value);
            case NullExpr: return new IrLitNull(IrType.Void);
            case IdentExpr ie: return ResolveIdent(ie, ctx);
            case CastExpr ce:
            {
                CheckType(ce.TargetType, ctx, ce.Span, allowVoid: true);
                var inner = ResolveExpr(ce.Value, ctx);
                var to = ResolveType(ce.TargetType);
                CheckCast(inner, to, ctx);
                return new IrCast(to, inner);
            }
            case PostfixExpr pf:
            {
                var opnd = ResolveExpr(pf.Operand, ctx);
                if (opnd is not (IrVar or IrFieldLoad or IrIndex or IrDeref))
                    diag.Error(Codes.NotAnLvalue, ctx.File, pf.Span,
                        $"'{pf.Op.Sym()}' needs a variable, field, or element to modify");
                else if (opnd.Type is IrPtrType)
                {
                    if (!ctx.InUnsafe)
                        diag.Error(Codes.UnsafeRequired, ctx.File, pf.Span,
                            $"pointer '{pf.Op.Sym()}' requires an 'unsafe' block");
                }
                else if (!IsArith(opnd.Type))
                    diag.Error(Codes.TypeMismatch, ctx.File, pf.Span,
                        $"'{pf.Op.Sym()}' requires a numeric operand, got '{Describe(opnd.Type)}'");
                return new IrPostfix(pf.Op, opnd);
            }
            case UnaryExpr un: return ResolveUnary(un, ctx);
            case BinExpr be: return ResolveBin(be, ctx);
            case CallExpr ce: return ResolveCall(ce, ctx);
            case MemberAccessExpr ma: return ResolveMemberAccess(ma, ctx);
            case NewExpr ne: return ResolveNew(ne, ctx);
            case ArrayLitExpr al: return ResolveArrayLit(al, ctx);
            case IndexExpr ix: return ResolveIndex(ix, ctx);
            case SizeofExpr so:
                CheckType(so.TypeName, ctx, so.Span);
                return new IrSizeof(ResolveType(so.TypeName));
            case DefaultExpr de:
                CheckType(de.TypeName, ctx, de.Span);
                return new IrDefault(ResolveType(de.TypeName));
            case AddrOfExpr ao:
                if (!ctx.InUnsafe)
                    diag.Error(Codes.UnsafeRequired, ctx.File, ao.Span, "address-of '&' requires an 'unsafe' block");
                return new IrAddrOf(ResolveExpr(ao.Target, ctx));
            case DerefExpr dr:
            {
                if (!ctx.InUnsafe)
                    diag.Error(Codes.UnsafeRequired, ctx.File, dr.Span, "pointer dereference '*' requires an 'unsafe' block");
                var ptr = ResolveExpr(dr.Ptr, ctx);
                var inner = ptr.Type is IrPtrType pt ? pt.Inner : IrType.Void;
                return new IrDeref(ptr, inner);
            }
            case TernaryExpr te:
            {
                var cond = ResolveExpr(te.Cond, ctx);
                ForbidNestedThrows(cond, ctx, allowRoot: false);
                CheckCondition(cond, ctx);
                var then = ResolveExpr(te.Then, ctx);
                var els = ResolveExpr(te.Else, ctx);
                ForbidNestedThrows(then, ctx, allowRoot: false);
                ForbidNestedThrows(els, ctx, allowRoot: false);
                IrType? unified = UnifyTernary(then, els);
                if (unified == null)
                {
                    diag.Error(Codes.TypeMismatch, ctx.File, te.Span,
                        $"ternary branches have incompatible types '{Describe(then.Type)}' and '{Describe(els.Type)}'");
                    return new IrTernary(cond, then, els, then.Type);
                }
                return new IrTernary(cond, CoerceTo(then, unified), CoerceTo(els, unified), unified);
            }
            case InterpStrExpr istr:
            {
                var parts = new List<IrExpr>(istr.Parts.Length);
                for (int i = 0; i < istr.Parts.Length; i++)
                {
                    parts.Add(EnsureString(ResolveExpr(istr.Parts[i], ctx), ctx));
                }
                return parts.Count == 0 ? new IrLitString("\"\"") { Span = istr.Span } : new IrInterp(parts);
            }
            default:
                throw new NotImplementedException($"[TypeResolver] unhandled Expr: {e.GetType().Name} -- additional expression forms added in later commits");
        }
    }

    /// <summary>
    /// Resolves a unary expression, validating that the operand type is compatible with the operator.
    /// </summary>
    private IrExpr ResolveUnary(UnaryExpr un, ResolveCtx ctx)
    {
        var operand = ResolveExpr(un.Operand, ctx);
        if (un.Op == UnOp.Not && operand.Type is not IrPrimType { CName: "bool" })
            diag.Error(Codes.TypeMismatch, ctx.File, un.Span,
                $"operator '!' requires 'bool', got '{Describe(operand.Type)}'");
        else if (un.Op == UnOp.Neg && !IsArith(operand.Type))
            diag.Error(Codes.TypeMismatch, ctx.File, un.Span,
                $"unary '-' requires a numeric operand, got '{Describe(operand.Type)}'");
        else if (un.Op == UnOp.BitNot && !IsInteger(operand.Type))
            diag.Error(Codes.TypeMismatch, ctx.File, un.Span,
                $"operator '~' requires an integer operand, got '{Describe(operand.Type)}'");
        var t = un.Op == UnOp.Not ? IrType.Bool : operand.Type;
        return new IrUnaryOp(un.Op, operand, t);
    }

    /// <summary>
    /// Resolves a binary expression. Handles string concatenation, operator overloading,
    /// pointer arithmetic, logical, equality, relational, bitwise, and arithmetic operators.
    /// </summary>
    private IrExpr ResolveBin(BinExpr be, ResolveCtx ctx)
    {
        var left = ResolveExpr(be.Left, ctx);
        var right = ResolveExpr(be.Right, ctx);

        // String concatenation: '+' with a String operand stringifies the other side.
        if (be.Op == BinOp.Add && (left.Type.IsString || right.Type.IsString))
        {
            var sop = sym.LookupOperator("String", "+");
            string cn = sop?.CName ?? Mangler.Operator("String", "+");
            return new IrStaticCall(cn, IrType.String, [EnsureString(left, ctx), EnsureString(right, ctx)]);
        }

        string? lhsClass = ClassNameOf(left.Type);
        if (lhsClass != null && sym.LookupOperator(lhsClass, be.Op.Sym()) is { } op)
            return new IrStaticCall(op.CName, ResolveType(op.Type), [left, right]);

        if (left.Type is IrPtrType && be.Op is BinOp.Add or BinOp.Sub && right.Type.IsNumeric)
        {
            if (!ctx.InUnsafe)
                diag.Error(Codes.UnsafeRequired, ctx.File, be.Span, "pointer arithmetic requires an 'unsafe' block");
            return new IrBinOp(be.Op, left, right, left.Type);
        }

        if (be.Op is BinOp.And or BinOp.Or)
        {
            if (left.Type is not IrPrimType { CName: "bool" } || right.Type is not IrPrimType { CName: "bool" })
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"operator '{be.Op.Sym()}' requires 'bool' operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            return new IrBinOp(be.Op, left, right, IrType.Bool);
        }

        if (be.Op is BinOp.Eq or BinOp.Ne)
        {
            if (!ComparableEq(left, right))
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"'{be.Op.Sym()}' operands are not comparable: '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            return new IrBinOp(be.Op, left, right, IrType.Bool);
        }

        if (be.Op is BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge)
        {
            if (!(IsArith(left.Type) && IsArith(right.Type)))
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"operator '{be.Op.Sym()}' requires numeric operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            return new IrBinOp(be.Op, left, right, IrType.Bool);
        }

        if (be.Op is BinOp.BitAnd or BinOp.BitOr or BinOp.BitXor or BinOp.Shl or BinOp.Shr)
        {
            if (!(IsInteger(left.Type) && IsInteger(right.Type)))
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"operator '{be.Op.Sym()}' requires integer operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            IrType bt = be.Op is BinOp.Shl or BinOp.Shr ? left.Type
                      : NumRank(left.Type) >= NumRank(right.Type) ? left.Type : right.Type;
            return new IrBinOp(be.Op, left, right, bt);
        }

        if (!(IsArith(left.Type) && IsArith(right.Type)))
            diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                $"operator '{be.Op.Sym()}' cannot be applied to '{Describe(left.Type)}' and '{Describe(right.Type)}'");
        IrType t = NumRank(left.Type) >= NumRank(right.Type) ? left.Type : right.Type;
        return new IrBinOp(be.Op, left, right, t);
    }

    /// <summary>
    /// Parses an integer literal lexeme into its bit pattern, IR type, and optional verbatim C text.
    /// Returns false when the magnitude does not fit in 64 bits.
    /// </summary>
    private static bool TryParseIntLit(ReadOnlySpan<char> raw, out long v, out IrType type, out string? ctext)
    {
        v = 0; type = IrType.Int; ctext = null;

        int end = raw.Length;
        bool hasU = false; int lCount = 0;
        while (end > 0 && raw[end - 1] is 'u' or 'U' or 'l' or 'L')
        {
            if (raw[end - 1] is 'u' or 'U') hasU = true; else lCount++;
            end--;
        }
        ReadOnlySpan<char> core = raw[..end];
        bool hasSuffix = end < raw.Length;
        bool isHex = core.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        ulong mag;
        if (isHex)
        {
            if (!ulong.TryParse(core[2..], System.Globalization.NumberStyles.HexNumber, ci, out mag))
                return false;
        }
        else if (!ulong.TryParse(core, System.Globalization.NumberStyles.None, ci, out mag))
            return false;

        v = unchecked((long)mag);

        bool isLong = lCount >= 1;
        type =
            hasU && isLong       ? new IrPrimType("uint64") :
            isLong               ? IrType.Long :
            hasU                 ? (mag <= uint.MaxValue ? new IrPrimType("uint") : new IrPrimType("uint64")) :
            mag <= int.MaxValue  ? IrType.Int :
            mag <= long.MaxValue ? IrType.Long :
                                   new IrPrimType("uint64");

        ctext =
            isHex || hasSuffix                     ? raw.ToString() :
            type is IrPrimType { CName: "uint64" } ? mag.ToString(ci) + "ULL" :
                                                     null;
        return true;
    }

    /// <summary>
    /// Returns the IR type for a floating-point literal: float for an f/F suffix, double otherwise.
    /// </summary>
    private static IrPrimType FloatLitType(string raw)
    {
        return raw.Length > 0 && raw[^1] is 'f' or 'F' ? IrType.Float : IrType.Double;
    }

    /// <summary>
    /// Resolves a bare identifier expression to a variable reference, bool/null literal,
    /// self-expression, or class reference. Reports UndefinedVariable for unknown names.
    /// </summary>
    private IrExpr ResolveIdent(IdentExpr ie, ResolveCtx ctx)
    {
        string name = ie.Name;
        if (name == "true")  return new IrLitBool(true);
        if (name == "false") return new IrLitBool(false);
        if (name == "null")  return new IrLitNull(IrType.Void);

        if (name == "self")
        {
            if (!ctx.InStatic && !string.IsNullOrEmpty(ctx.CurClass))
                return new IrSelfExpr(ctx.CurClass);
            diag.Error(Codes.UndefinedVariable, ctx.File, ie.Span,
                string.IsNullOrEmpty(ctx.CurClass)
                    ? "'self' is only valid inside an instance method"
                    : "'self' is not available in a static context");
            return new IrSelfExpr(ctx.CurClass);
        }

        var local = ctx.Scope.Lookup(name);
        if (local != null) return new IrVar(name, local, ctx.Scope.IsRef(name));

        if (ClassInScope(name)) return new IrVar(name, new IrClassRef(name));

        // A bare reference to an unambiguous, non-throws, non-entry free function
        // decays to a function-pointer value. Ref-parameter functions are excluded
        // because the func(...)->R type syntax has no position for 'ref'.
        var fsym = sym.LookupFreeFunc(name);
        if (fsym != null && FuncInScope(fsym))
        {
            if (sym.IsOverloadedFunc(name))
            {
                diag.Error(Codes.AmbiguousOverload, ctx.File, ie.Span,
                    $"cannot take the address of overloaded function '{name}'");
                return new IrVar(name, IrType.Int);
            }
            if (fsym.Sig!.IsEntry)
            {
                diag.Error(Codes.CallToEntry, ctx.File, ie.Span,
                    $"'{name}' is an entry point and cannot be used as a value");
                return new IrVar(name, IrType.Int);
            }
            if (fsym.Sig.IsThrows)
            {
                diag.Error(Codes.TypeMismatch, ctx.File, ie.Span,
                    $"'{name}' is a 'throws' function and cannot be used as a function-pointer value");
                return new IrVar(name, IrType.Int);
            }
            if (fsym.Sig.Params.Any(p => p.IsRef))
            {
                diag.Error(Codes.TypeMismatch, ctx.File, ie.Span,
                    $"'{name}' has a 'ref' parameter and cannot be used as a function-pointer value " +
                    "(func(...) -> R types cannot express which parameters are 'ref')");
                return new IrVar(name, IrType.Int);
            }
            var ps = new List<IrType>(fsym.Sig.Params.Count);
            for (int i = 0; i < fsym.Sig.Params.Count; i++)
            {
                ps.Add(ResolveType(fsym.Sig.Params[i].Type));
            }
            return new IrFuncRef(fsym.CName, FnPtr(ResolveType(fsym.Sig.ReturnType), ps));
        }

        string msg =
            sym.IsField(ctx.CurClass, name)
                ? ctx.InStatic
                    ? $"'{name}' is an instance field and cannot be used in a static context"
                    : $"'{name}' is a field; write 'self.{name}'"
                : sym.IsClass(name)
                    ? $"'{Mangler.DisplayName(name)}' is not in scope; import its module"
                    : $"'{name}' is not defined";
        diag.Error(Codes.UndefinedVariable, ctx.File, ie.Span, msg);
        return new IrVar(name, IrType.Int);
    }

    /// <summary>
    /// Resolves a member access expression, handling enum constants and class field loads.
    /// </summary>
    private IrExpr ResolveMemberAccess(MemberAccessExpr ma, ResolveCtx ctx)
    {
        // Enum member access: Color.Red.
        if (ma.Object is IdentExpr eid && sym.IsEnum(eid.Name) && ctx.Scope.Lookup(eid.Name) == null)
        {
            if (!sym.IsEnumMember(eid.Name, ma.Member))
                diag.Error(Codes.UndefinedVariable, ctx.File, ma.Span,
                    $"enum '{eid.Name}' has no member '{ma.Member}'");
            return new IrEnumConst(eid.Name, ma.Member) { Span = ma.Span };
        }

        var obj = ResolveExpr(ma.Object, ctx);
        string? cls = ClassNameOf(obj.Type);
        IrType fieldType = IrType.Int;
        if (cls != null)
        {
            var ft = sym.FieldType(cls, ma.Member);
            if (ft != null)
            {
                fieldType = ResolveType(ft);
                CheckMemberAccess(cls, ma.Member, ctx, ma.Span);
            }
            else if (!HasOpaqueFields(cls))
                diag.Error(Codes.UndefinedVariable, ctx.File, ma.Span,
                    $"'{Mangler.DisplayName(cls)}' has no field '{ma.Member}'");
        }
        return new IrFieldLoad(obj, ma.Member, fieldType);
    }

    /// <summary>
    /// Coerces each resolved argument to its declared parameter type and validates ref/non-ref passing.
    /// </summary>
    private void CoerceArgs(List<IrExpr> args, MethodSig? sig, ResolveCtx ctx, Expr[]? astArgs = null)
    {
        if (sig == null) return;
        for (int i = 0; i < args.Count && i < sig.Params.Count; i++)
        {
            var pt = ResolveType(sig.Params[i].Type);
            args[i] = Coerce(args[i], pt, ctx);
            CheckAssign(args[i], pt, $"parameter '{sig.Params[i].Name}'", ctx, Codes.ArgTypeMismatch);

            if (astArgs == null || i >= astArgs.Length) continue;
            bool argIsRef = astArgs[i] is RefArgExpr;
            bool paramIsRef = sig.Params[i].IsRef;
            if (argIsRef && !paramIsRef)
                diag.Error(Codes.RefArgMismatch, ctx.File, astArgs[i].Span,
                    $"argument {i + 1} is passed 'ref' but parameter '{sig.Params[i].Name}' is not 'ref'");
            else if (!argIsRef && paramIsRef)
                diag.Error(Codes.RefArgMismatch, ctx.File, astArgs[i].Span,
                    $"parameter '{sig.Params[i].Name}' is 'ref'; pass argument {i + 1} as 'ref ...'");
            else if (argIsRef)
            {
                CheckLValue(args[i], ctx);
                args[i] = new IrAddrOf(args[i]);
            }
        }
    }

    /// <summary>
    /// Resolves a call expression: member calls, bare free-function calls, sibling method calls,
    /// indirect function-pointer calls, and ARC intrinsics. Uses overload resolution throughout.
    /// </summary>
    private IrExpr ResolveCall(CallExpr ce, ResolveCtx ctx)
    {
        // ref arguments are resolved as their plain target so type inference sees the real type;
        // ref/non-ref matching and address-of wrapping happen in CoerceArgs once the callee is known.
        var args = new List<IrExpr>(ce.Args.Length);
        for (int i = 0; i < ce.Args.Length; i++)
        {
            var a = ce.Args[i];
            args.Add(ResolveExpr(a is RefArgExpr ra ? ra.Target : a, ctx));
        }

        // member access call: obj.Method(args) or ClassName.StaticMethod(args)
        if (ce.Callee is MemberAccessExpr ma)
        {
            string objName = ma.Object is IdentExpr oid ? oid.Name : "";

            if (!string.IsNullOrEmpty(objName) && sym.IsUnion(objName) && ctx.Scope.Lookup(objName) == null)
                return ResolveUnionConstruct(objName, ma.Member, args, ctx, ce.Span);

            if (!string.IsNullOrEmpty(objName) && ClassInScope(objName.AsSpan()) && ctx.Scope.Lookup(objName) == null)
            {
                var msym = sym.LookupMethod(objName, ma.Member);
                if (msym == null)
                {
                    if (!IsOpaqueStruct(objName))
                        diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                            $"'{Mangler.DisplayName(objName)}' has no method '{ma.Member}'");
                }
                else if (msym.Sig is { IsStatic: false })
                    diag.Error(Codes.StaticOnInstance, ctx.File, ce.Span,
                        $"'{Mangler.DisplayName(objName)}.{ma.Member}' is an instance method; call it on a value");
                CheckMemberAccess(objName, ma.Member, ctx, ce.Span);
                var chosen = ChooseOverload(sym.MethodOverloads(objName, ma.Member), msym, args,
                    $"{Mangler.DisplayName(objName)}.{ma.Member}", ctx, ce.Span);
                string cn = chosen?.CName ?? Mangler.Method(objName, ma.Member, [], false);
                var ret = chosen != null ? ResolveType(chosen.Type) : IrType.Void;
                CoerceArgs(args, chosen?.Sig, ctx, ce.Args);
                if (chosen?.Sig?.IsThrows == true)
                    { CheckThrowsHandled(ctx, ce.Span); return new IrThrowsCall(cn, ret, args); }
                return new IrStaticCall(cn, ret, args);
            }

            var recv = ResolveExpr(ma.Object, ctx);
            string? cls = ClassNameOf(recv.Type);
            if (cls != null)
            {
                var msym = sym.LookupMethod(cls, ma.Member);
                if (msym == null)
                {
                    // field holding a function pointer used as a callback
                    var cbt = sym.FieldType(cls, ma.Member);
                    if (cbt != null && ResolveType(cbt) is IrFuncPtrType cbfp)
                    {
                        CheckMemberAccess(cls, ma.Member, ctx, ce.Span);
                        return ResolveIndirectCallArgs(new IrFieldLoad(recv, ma.Member, cbfp), cbfp, args, ctx, ce.Span, ce.Args);
                    }
                    if (!IsOpaqueStruct(cls))
                        diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                            $"'{Mangler.DisplayName(cls)}' has no method '{ma.Member}'");
                }
                else if (msym.Sig is { IsStatic: true })
                    diag.Error(Codes.InstanceOnStatic, ctx.File, ce.Span,
                        $"'{Mangler.DisplayName(cls)}.{ma.Member}' is static; call it as '{Mangler.DisplayName(cls)}.{ma.Member}(...)'");
                CheckMemberAccess(cls, ma.Member, ctx, ce.Span);
                var chosen = ChooseOverload(sym.MethodOverloads(cls, ma.Member), msym, args,
                    $"{Mangler.DisplayName(cls)}.{ma.Member}", ctx, ce.Span);
                string cn = chosen?.CName ?? Mangler.Method(cls, ma.Member, [], false);
                var ret = chosen != null ? ResolveType(chosen.Type) : IrType.Void;
                CoerceArgs(args, chosen?.Sig, ctx, ce.Args);
                if (chosen?.Sig?.IsThrows == true)
                    { CheckThrowsHandled(ctx, ce.Span); return new IrThrowsInstanceCall(recv, cn, ret, args); }
                return new IrInstanceCall(recv, cn, ret, args);
            }

            diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                $"cannot call '{ma.Member}' on '{Describe(recv.Type)}'");
            return new IrInstanceCall(recv, Mangler.FreeFunc(ma.Member, [], false, false, false), IrType.Void, args);
        }

        // bare call: name(args)
        if (ce.Callee is IdentExpr id)
        {
            // local variable holding a function pointer shadows any free function of the same name
            var calleeLocal = ctx.Scope.Lookup(id.Name);
            if (calleeLocal is IrFuncPtrType localFp)
                return ResolveIndirectCallArgs(new IrVar(id.Name, localFp, ctx.Scope.IsRef(id.Name)), localFp, args, ctx, ce.Span, ce.Args);

            if (TryResolveArcIntrinsic(id.Name, args, ctx, ce.Span) is { } arc) return arc;

            if (_funcTemplates.TryGetValue(id.Name, out var tmpl))
                return ResolveGenericCall(tmpl, args, ctx, ce.Span, ce.Args);

            // file-local private free functions take priority over globals
            var pfsym = sym.LookupPrivateFunc(ctx.File, id.Name);
            if (pfsym != null)
            {
                var pchosen = ChooseOverload(sym.PrivateFuncOverloads(ctx.File, id.Name), pfsym, args, id.Name, ctx, ce.Span);
                string pcn = pchosen?.CName ?? Mangler.PrivateFreeFunc(Mangler.FileToken(ctx.File), id.Name, [], false);
                var pret = pchosen != null ? ResolveType(pchosen.Type) : IrType.Void;
                CoerceArgs(args, pchosen?.Sig, ctx, ce.Args);
                if (pchosen?.Sig?.IsThrows == true)
                    { CheckThrowsHandled(ctx, ce.Span); return new IrThrowsCall(pcn, pret, args); }
                return new IrStaticCall(pcn, pret, args);
            }

            var fsym = sym.LookupFreeFunc(id.Name);
            if (fsym != null && FuncInScope(fsym))
            {
                if (fsym.Sig?.IsEntry == true)
                    diag.Error(Codes.CallToEntry, ctx.File, ce.Span,
                        $"'{id.Name}' is an entry point and cannot be called directly");
                var chosen = ChooseOverload(sym.FuncOverloads(id.Name), fsym, args, id.Name, ctx, ce.Span);
                string cn = chosen?.CName ?? Mangler.FreeFunc(id.Name, [], false, false, false);
                var ret = chosen != null ? ResolveType(chosen.Type) : IrType.Void;
                CoerceArgs(args, chosen?.Sig, ctx, ce.Args);
                if (chosen?.Sig?.IsThrows == true)
                    { CheckThrowsHandled(ctx, ce.Span); return new IrThrowsCall(cn, ret, args); }
                return new IrStaticCall(cn, ret, args);
            }

            // sibling method of the current class
            if (!string.IsNullOrEmpty(ctx.CurClass))
            {
                var msym = sym.LookupMethod(ctx.CurClass, id.Name);
                if (msym != null)
                {
                    bool isStatic = msym.Sig?.IsStatic ?? false;
                    var chosen = ChooseOverload(sym.MethodOverloads(ctx.CurClass, id.Name), msym, args,
                        $"{Mangler.DisplayName(ctx.CurClass)}.{id.Name}", ctx, ce.Span);
                    string cn = chosen?.CName ?? Mangler.Method(ctx.CurClass, id.Name, [], false);
                    var ret = chosen != null ? ResolveType(chosen.Type) : IrType.Void;
                    if (!isStatic)
                    {
                        diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                            $"'{id.Name}' is an instance method; call it as 'self.{id.Name}(...)'");
                        CoerceArgs(args, chosen?.Sig, ctx, ce.Args);
                        return new IrInstanceCall(new IrSelfExpr(ctx.CurClass), cn, ret, args);
                    }
                    CoerceArgs(args, chosen?.Sig, ctx, ce.Args);
                    if (chosen?.Sig?.IsThrows == true)
                        { CheckThrowsHandled(ctx, ce.Span); return new IrThrowsCall(cn, ret, args); }
                    return new IrStaticCall(cn, ret, args);
                }
            }

            diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                sym.LookupFreeFunc(id.Name) != null
                    ? $"'{id.Name}' is not in scope; import its module"
                    : $"call to undefined function '{id.Name}'");
            return new IrStaticCall(Mangler.FreeFunc(id.Name, [], false, false, false), IrType.Void, args);
        }

        // indirect call through any other expression
        var calleeExpr = ResolveExpr(ce.Callee, ctx);
        if (calleeExpr.Type is IrFuncPtrType gfp)
            return ResolveIndirectCallArgs(calleeExpr, gfp, args, ctx, ce.Span, ce.Args);
        diag.Error(Codes.TypeMismatch, ctx.File, ce.Span, "callee expression is not callable");
        return new IrLitInt(0);
    }

    /// <summary>
    /// Resolves an indirect function-pointer call, checking argument count and types against the pointer's signature.
    /// </summary>
    private IrExpr ResolveIndirectCallArgs(IrExpr target, IrFuncPtrType fpt, List<IrExpr> args, ResolveCtx ctx,
        TextSpan span, Expr[]? astArgs = null)
    {
        if (args.Count != fpt.Params.Count)
            diag.Error(Codes.WrongArgCount, ctx.File, span,
                $"function pointer expects {fpt.Params.Count} argument(s), got {args.Count}");
        for (int i = 0; i < args.Count && i < fpt.Params.Count; i++)
        {
            args[i] = Coerce(args[i], fpt.Params[i], ctx);
            CheckAssign(args[i], fpt.Params[i], $"argument {i + 1}", ctx, Codes.ArgTypeMismatch);

            if (astArgs == null || i >= astArgs.Length) continue;
            bool argIsRef = astArgs[i] is RefArgExpr;
            if (argIsRef)
                diag.Error(Codes.RefArgMismatch, ctx.File, astArgs[i].Span,
                    "indirect call through a function pointer does not support 'ref' arguments");
        }
        return new IrIndirectCall(target, fpt.Ret, args);
    }

    /// <summary>
    /// Resolves an index expression, dispatching to the class operator [] overload,
    /// fixed-array element access, or unsafe pointer indexing.
    /// </summary>
    private IrExpr ResolveIndex(IndexExpr ix, ResolveCtx ctx)
    {
        var obj = ResolveExpr(ix.Object, ctx);
        var idx = ResolveExpr(ix.Index, ctx);
        if (obj.Type is IrClassRef icr && sym.LookupOperator(icr.ClassName, "[]") is { } getOp)
        {
            var idxType = ResolveType(getOp.Sig!.Params[0].Type);
            idx = Coerce(idx, idxType, ctx);
            CheckAssign(idx, idxType, "the index", ctx, Codes.TypeMismatch);
            return new IrInstanceCall(obj, getOp.CName, ResolveType(getOp.Type), [idx]) { Span = ix.Span };
        }
        IrType elem;
        if (obj.Type is IrArrayType at) elem = at.Elem;
        else if (obj.Type is IrPtrType pt)
        {
            if (!ctx.InUnsafe)
                diag.Error(Codes.UnsafeRequired, ctx.File, ix.Span, "pointer indexing requires an 'unsafe' block");
            elem = pt.Inner;
        }
        else
        {
            diag.Error(Codes.IndexOnNonCollection, ctx.File, ix.Span, $"'{Describe(obj.Type)}' cannot be indexed");
            elem = IrType.Int;
        }
        return new IrIndex(obj, idx, elem);
    }

    /// <summary>
    /// Resolves an indexed assignment, handling operator []= overloads with compound
    /// assignment hoisting, and plain fixed-array or pointer index targets.
    /// </summary>
    private IrStmt ResolveIndexAssign(IndexExpr ixt, AssignStmt asgn, ResolveCtx ctx)
    {
        var obj = ResolveExpr(ixt.Object, ctx);
        var idx = ResolveExpr(ixt.Index, ctx);

        if (obj.Type is IrClassRef cr && sym.LookupOperator(cr.ClassName, "[]=") is { } setOp)
        {
            var idxType = ResolveType(setOp.Sig!.Params[0].Type);
            var valType = ResolveType(setOp.Sig!.Params[1].Type);
            idx = Coerce(idx, idxType, ctx);
            CheckAssign(idx, idxType, "the index", ctx, Codes.TypeMismatch);
            if (asgn.Op == AssignOp.Assign)
            {
                var value = Coerce(ResolveExpr(asgn.Value, ctx), valType, ctx);
                CheckAssign(value, valType, "the assignment target", ctx, Codes.TypeMismatch);
                ForbidNestedThrows(value, ctx, allowRoot: false);
                return new IrExprStmt(new IrInstanceCall(obj, setOp.CName, IrType.Void, [idx, value])) { Span = asgn.Span };
            }
            // compound: obj/idx used twice — hoist to avoid double evaluation
            var stmts = new List<IrStmt>();
            var objRef = HoistIfImpure(obj, "_ixo", stmts);
            var idxRef = HoistIfImpure(idx, "_ixi", stmts);
            var getOp = sym.LookupOperator(cr.ClassName, "[]");
            IrExpr current;
            if (getOp != null)
                current = new IrInstanceCall(objRef, getOp.CName, ResolveType(getOp.Type), [idxRef]) { Span = ixt.Span };
            else
            {
                diag.Error(Codes.NoIndexSetter, ctx.File, asgn.Span,
                    $"'{Describe(obj.Type)}' has '[]=' but no '[]' getter; cannot use a compound assignment");
                current = new IrLitInt(0);
            }
            var rhs = ResolveExpr(asgn.Value, ctx);
            BinOp baseOp = asgn.Op.BaseOp()!.Value;
            string? elemClass = ClassNameOf(current.Type);
            IrExpr combined;
            if (elemClass != null && sym.LookupOperator(elemClass, baseOp.Sym()) is { } elemOp)
                combined = new IrStaticCall(elemOp.CName, ResolveType(elemOp.Type), [current, rhs]);
            else
            {
                CheckCompound(asgn.Op, current, rhs, ctx);
                combined = new IrBinOp(baseOp, current, rhs, current.Type);
            }
            var value2 = Coerce(combined, valType, ctx);
            ForbidNestedThrows(value2, ctx, allowRoot: false);
            stmts.Add(new IrExprStmt(new IrInstanceCall(objRef, setOp.CName, IrType.Void, [idxRef, value2])));
            return Seq(stmts, asgn.Span);
        }

        if (obj.Type is IrClassRef cr2 && sym.LookupOperator(cr2.ClassName, "[]") != null)
        {
            diag.Error(Codes.NoIndexSetter, ctx.File, asgn.Span,
                $"'{Describe(obj.Type)}' has a '[]' getter but no '[]=' setter; cannot assign to it");
            return new IrExprStmt(new IrLitInt(0));
        }

        IrType elem;
        if (obj.Type is IrArrayType at) elem = at.Elem;
        else if (obj.Type is IrPtrType pt)
        {
            if (!ctx.InUnsafe)
                diag.Error(Codes.UnsafeRequired, ctx.File, ixt.Span, "pointer indexing requires an 'unsafe' block");
            elem = pt.Inner;
        }
        else
        {
            diag.Error(Codes.IndexOnNonCollection, ctx.File, ixt.Span, $"'{Describe(obj.Type)}' cannot be indexed");
            elem = IrType.Int;
        }
        var val = ResolveExpr(asgn.Value, ctx);
        if (asgn.Op == AssignOp.Assign)
        {
            var target = new IrIndex(obj, idx, elem) { Span = ixt.Span };
            val = Coerce(val, target.Type, ctx);
            CheckAssign(val, target.Type, "the assignment target", ctx, Codes.TypeMismatch);
            ForbidNestedThrows(val, ctx, allowRoot: false);
            return new IrAssign(target, AssignOp.Assign, val);
        }
        string elemBaseOp = asgn.Op.BaseOp()!.Value.Sym();
        if (ClassNameOf(elem) is { } elemClass2 && sym.LookupOperator(elemClass2, elemBaseOp) is { } elemOp2)
        {
            var stmts = new List<IrStmt>();
            var objRef = HoistIfImpure(obj, "_ixo", stmts);
            var idxRef = HoistIfImpure(idx, "_ixi", stmts);
            var readTarget = new IrIndex(objRef, idxRef, elem) { Span = ixt.Span };
            var writeTarget = new IrIndex(objRef, idxRef, elem) { Span = ixt.Span };
            var composed = new IrStaticCall(elemOp2.CName, ResolveType(elemOp2.Type), [readTarget, val]);
            CheckAssign(composed, elem, "the assignment target", ctx, Codes.TypeMismatch);
            ForbidNestedThrows(composed, ctx, allowRoot: false);
            stmts.Add(new IrAssign(writeTarget, AssignOp.Assign, composed));
            return Seq(stmts, asgn.Span);
        }
        var plainTarget = new IrIndex(obj, idx, elem) { Span = ixt.Span };
        CheckCompound(asgn.Op, plainTarget, val, ctx);
        ForbidNestedThrows(val, ctx, allowRoot: false);
        return new IrAssign(plainTarget, asgn.Op, val);
    }

    /// <summary>
    /// Resolves a new expression, validating the type is a class in scope and checking
    /// the constructor argument count. Handles collection initializers via ResolveCollectionInit.
    /// </summary>
    private IrExpr ResolveNew(NewExpr ne, ResolveCtx ctx)
    {
        var args = new List<IrExpr>(ne.Args.Length);
        for (int i = 0; i < ne.Args.Length; i++)
        {
            var a = ne.Args[i];
            args.Add(ResolveExpr(a is RefArgExpr ra ? ra.Target : a, ctx));
        }
        if (sym.Modules.Contains(ne.Type))
        {
            diag.Error(Codes.NewOnNonClass, ctx.File, ne.Span,
                $"'{Mangler.DisplayName(ne.Type)}' is a module and cannot be instantiated");
            return new IrNew(ne.Type, args);
        }
        if (!ClassInScope(ne.Type))
        {
            diag.Error(Codes.NewOnNonClass, ctx.File, ne.Span,
                sym.IsClass(ne.Type) ? $"'{Mangler.DisplayName(ne.Type)}' is not in scope; import its module"
                : SymbolTable.Primitives.Contains(ne.Type) ? $"'{Mangler.DisplayName(ne.Type)}' is a primitive; use 'let', not 'new'"
                : $"'{Mangler.DisplayName(ne.Type)}' is not a class");
            return new IrNew(ne.Type, args);
        }
        var init = sym.LookupMethod(ne.Type, "_init");
        if (init?.Sig is { } isig && isig.Params.Count > 0)
        {
            CheckArgCount(isig, args.Count, $"{Mangler.DisplayName(ne.Type)} constructor", ctx, ne.Span);
            CoerceArgs(args, isig, ctx, ne.Args);
        }
        else if (args.Count > 0)
            diag.Error(Codes.WrongArgCount, ctx.File, ne.Span,
                $"'{Mangler.DisplayName(ne.Type)}' has no constructor taking arguments");
        if (ne.CollectionInit.Length > 0)
            return ResolveCollectionInit(ne, args, ctx);
        return new IrNew(ne.Type, args);
    }

    /// <summary>
    /// Resolves a collection initializer by looking up an Add method and coercing each element.
    /// </summary>
    private IrExpr ResolveCollectionInit(NewExpr ne, List<IrExpr> ctorArgs, ResolveCtx ctx)
    {
        var add = sym.LookupMethod(ne.Type, "Add");
        if (add?.Sig == null)
        {
            diag.Error(Codes.UndefinedMethod, ctx.File, ne.Span,
                $"'{Mangler.DisplayName(ne.Type)}' has no 'Add' method for a collection initializer");
            return new IrNew(ne.Type, ctorArgs);
        }
        if (add.Sig.Params.Count != 1)
        {
            diag.Error(Codes.WrongArgCount, ctx.File, ne.Span,
                $"'{Mangler.DisplayName(ne.Type)}.Add' must take exactly one argument to be used in a collection initializer");
            return new IrNew(ne.Type, ctorArgs);
        }
        var elemType = ResolveType(add.Sig.Params[0].Type);
        var inits = new List<IrExpr>(ne.CollectionInit.Length);
        foreach (var el in ne.CollectionInit)
        {
            var r = Coerce(ResolveExpr(el, ctx), elemType, ctx);
            CheckAssign(r, elemType, $"a '{Mangler.DisplayName(ne.Type)}' element", ctx, Codes.ArgTypeMismatch);
            ForbidNestedThrows(r, ctx, allowRoot: false);
            inits.Add(r);
        }
        return new IrNewInit(ne.Type, ctorArgs, add.CName, inits);
    }

    /// <summary>
    /// Resolves a fixed-size array literal, checking that all elements share a common type.
    /// </summary>
    private IrExpr ResolveArrayLit(ArrayLitExpr al, ResolveCtx ctx)
    {
        if (al.Elems.Length == 0)
        {
            diag.Error(Codes.TypeMismatch, ctx.File, al.Span, "empty array literal '[]' has no element type");
            return new IrArrayLit(Arr(IrType.Int, 0), []);
        }
        var elems = new List<IrExpr>(al.Elems.Length);
        for (int i = 0; i < al.Elems.Length; i++)
        {
            elems.Add(ResolveExpr(al.Elems[i], ctx));
        }
        var elemType = elems[0].Type;
        for (int i = 1; i < elems.Count; i++)
        {
            elems[i] = Coerce(elems[i], elemType, ctx);
            CheckAssign(elems[i], elemType, "an array element", ctx, Codes.TypeMismatch);
        }
        return new IrArrayLit(Arr(elemType, elems.Count), elems);
    }

    /// <summary>
    /// Resolves a union variant construction call, validating the variant name and
    /// coercing each argument to its declared field type.
    /// </summary>
    private IrExpr ResolveUnionConstruct(string unionName, string variant, List<IrExpr> args, ResolveCtx ctx, TextSpan span)
    {
        var variants = sym.UnionDef(unionName)!;
        int idx = variants.FindIndex(v => v.Name == variant);
        if (idx < 0)
        {
            diag.Error(Codes.UndefinedVariable, ctx.File, span, $"union '{unionName}' has no variant '{variant}'");
            return new IrUnionConstruct(new IrUnionType(unionName), 0, args);
        }
        var fields = variants[idx].Fields;
        if (fields.Length != args.Count)
            diag.Error(Codes.WrongArgCount, ctx.File, span,
                $"'{unionName}.{variant}' expects {fields.Length} argument(s), got {args.Count}");
        for (int i = 0; i < args.Count && i < fields.Length; i++)
        {
            var ft = ResolveType(fields[i].Type);
            args[i] = Coerce(args[i], ft, ctx);
            if (!Assignable(args[i], ft))
                diag.Error(Codes.ArgTypeMismatch, ctx.File, args[i].Span,
                    $"argument {i + 1} ('{Describe(args[i].Type)}') is not assignable to '{Describe(ft)}'");
        }
        return new IrUnionConstruct(new IrUnionType(unionName), idx, args);
    }

    #endregion

    private readonly struct FuncPtrKey(IrType ret, List<IrType> ps) : IEquatable<FuncPtrKey>
    {
        public readonly IrType Ret = ret;
        public readonly List<IrType> Params = ps;

        public bool Equals(FuncPtrKey other)
        {
            if (!SameType(Ret, other.Ret)) return false;
            if (Params.Count != other.Params.Count) return false;
            for (int i = 0; i < Params.Count; i++)
            {
                if (!SameType(Params[i], other.Params[i])) return false;
            }
            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is FuncPtrKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Ret);
            for (int i = 0; i < Params.Count; i++)
            {
                hash.Add(Params[i]);
            }
            return hash.ToHashCode();
        }
    }
}
