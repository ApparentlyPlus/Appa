namespace Appa;

sealed class TypeResolver(
    SymbolTable sym,
    HashSet<string> hasInit,
    HashSet<string> nativeStructs,
    HashSet<string> opaqueFieldClasses,
    Dictionary<string, HashSet<string>> visible,
    bool releaseMode,
    DiagnosticBag diag)
{
    // Modules visible to the file currently being resolved (set per file).
    HashSet<string> _scope = [];

    /// <summary>
    /// Returns true when a class name is declared in a module the current file imports.
    /// </summary>
    bool ClassInScope(string name) => sym.ClassModule(name) is { } m && _scope.Contains(m);

    /// <summary>
    /// Returns true when a class name is declared in a module the current file imports.
    /// </summary>
    bool ClassInScope(ReadOnlySpan<char> name) => sym.ClassModule(name) is { } m && _scope.Contains(m);

    /// <summary>
    /// Returns true when a free-function symbol is in scope for the current file.
    /// </summary>
    bool FuncInScope(Symbol? f) => f != null && _scope.Contains(f.Module);

    // Every fixed-array (T, N) pair used; the emitter stamps one struct per pair.
    readonly List<IrArrayType> _arrays = [];
    int _tmpSeq;

    /// <summary>
    /// Allocates a unique temporary variable name with the given prefix.
    /// </summary>
    string Tmp(string prefix) => $"{prefix}{_tmpSeq++}";

    /// <summary>
    /// Records a fixed-array type usage and returns the IrArrayType node.
    /// </summary>
    IrArrayType Arr(IrType elem, int size)
    {
        var a = new IrArrayType(elem, size);
        _arrays.Add(a);
        return a;
    }

    // Every distinct function-pointer signature used; the emitter stamps one typedef per signature.
    readonly List<IrFuncPtrType> _funcPtrTypes = [];
    readonly Dictionary<FuncPtrKey, IrFuncPtrType> _funcPtrSeen = new();

    /// <summary>
    /// Returns or creates a function-pointer type for the given return type and parameter list.
    /// </summary>
    IrFuncPtrType FnPtr(IrType ret, List<IrType> ps)
    {
        var key = new FuncPtrKey(ret, ps);
        if (_funcPtrSeen.TryGetValue(key, out var existing)) return existing;
        var f = new IrFuncPtrType(ret, ps);
        _funcPtrSeen[key] = f;
        _funcPtrTypes.Add(f);
        return f;
    }

    // Generic free-function templates; each distinct instantiation is stamped once.
    readonly Dictionary<string, (FuncDecl Decl, string File, string Context)> _funcTemplates = new();
    readonly Queue<(FuncDecl Decl, string File, string Context, Dictionary<string, string> Binds, string Mangled)> _genericQueue = new();
    readonly HashSet<string> _genericSeen = [];
    int _labelSeq;

    // Splits "func(T1,T2)->R" into its parameter type-strings and return type-string,
    // tracking paren/bracket depth so nested types don't split at the wrong comma.
    /// <summary>
    /// Splits an encoded function-type string into parameter types and return type.
    /// </summary>
    static bool TrySplitFuncType(ReadOnlySpan<char> t, out List<Range> ps, out ReadOnlySpan<char> rs)
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
    bool IsOpaqueStruct(string cls) => nativeStructs.Contains(cls);

    /// <summary>
    /// Returns true when the class has either a native struct body or raw C field blocks.
    /// </summary>
    bool HasOpaqueFields(string cls) => nativeStructs.Contains(cls) || opaqueFieldClasses.Contains(cls);

    /// <summary>
    /// Validates that the given Gata type name refers to a real, in-scope type.
    /// Reports a diagnostic on any unknown or out-of-scope name.
    /// </summary>
    void CheckType(string? gataType, ResolveCtx ctx, TextSpan span, bool allowVoid = false) =>
        CheckType(gataType.AsSpan(), ctx, span, allowVoid);

    /// <summary>
    /// Validates that the given Gata type name refers to a real, in-scope type.
    /// Reports a diagnostic on any unknown or out-of-scope name.
    /// </summary>
    void CheckType(ReadOnlySpan<char> gataType, ResolveCtx ctx, TextSpan span, bool allowVoid = false)
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
    void CheckParams(Param[] ps, ResolveCtx ctx)
    {
        var seen = new HashSet<string>();
        foreach (var p in ps)
            if (!seen.Add(p.Name))
                diag.Error(Codes.DuplicateName, ctx.File, p.Span, $"duplicate parameter '{p.Name}'");
    }

    /// <summary>
    /// Reports a diagnostic when the argument count does not match the expected parameter count.
    /// </summary>
    void CheckArgCount(MethodSig? sig, int argCount, string display, ResolveCtx ctx, TextSpan span)
    {
        if (sig != null && sig.Params.Count != argCount)
            diag.Error(Codes.WrongArgCount, ctx.File, span,
                $"'{display}' expects {sig.Params.Count} argument(s), got {argCount}");
    }

    // Overload resolution -- picks the cheapest match from a candidate set.
    /// <summary>
    /// Picks the best-matching overload from the candidates for the given argument list.
    /// Reports a diagnostic when no overload matches or multiple overloads tie.
    /// </summary>
    Symbol? ChooseOverload(IReadOnlyList<Symbol> cands, Symbol? primary,
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
    int? MatchCost(MethodSig sig, List<IrExpr> args)
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
    static int? ArgConvCost(IrExpr arg, IrType to)
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
    static int NumRank(IrType t) => t is IrPrimType p ? p.CName switch
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

    /// <summary>
    /// Returns a comma-separated list of argument types for use in diagnostic messages.
    /// </summary>
    static string DescribeArgs(List<IrExpr> args) => string.Join(", ", args.Select(a => Describe(a.Type)));

    // Type compatibility helpers.
    /// <summary>
    /// Returns true when both IR types are structurally identical.
    /// </summary>
    static bool SameType(IrType a, IrType b) => (a, b) switch
    {
        (IrVoidType, IrVoidType) => true,
        (IrPrimType x, IrPrimType y) => x.CName == y.CName,
        (IrClassRef x, IrClassRef y) => x.ClassName == y.ClassName,
        (IrEnumType x, IrEnumType y) => x.Name == y.Name,
        (IrPtrType x, IrPtrType y) => SameType(x.Inner, y.Inner),
        (IrArrayType x, IrArrayType y) => x.Size == y.Size && SameType(x.Elem, y.Elem),
        (IrResultType x, IrResultType y) => SameType(x.Inner, y.Inner),
        (IrFuncPtrType x, IrFuncPtrType y) =>
            SameType(x.Ret, y.Ret) && x.Params.Count == y.Params.Count
            && x.Params.Zip(y.Params, SameType).All(v => v),
        (IrUnionType x, IrUnionType y) => x.Name == y.Name,
        _ => false
    };

    /// <summary>
    /// Returns true when value's type is assignment-compatible with the target type,
    /// accounting for implicit numeric widening, null-to-reference, and pointer covariance.
    /// </summary>
    static bool Assignable(IrExpr value, IrType to)
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
    /// Returns a human-readable display name for the type, used in diagnostic messages.
    /// </summary>
    static string Describe(IrType t) => t switch
    {
        IrVoidType => "void",
        IrPrimType p => p.CName,
        IrClassRef c => Mangler.DisplayName(c.ClassName),
        IrPtrType p => Describe(p.Inner) + "*",
        IrArrayType a => $"[{a.Size}]{Describe(a.Elem)}",
        IrResultType r => "throws " + Describe(r.Inner),
        IrFuncPtrType f => $"func({string.Join(", ", f.Params.Select(Describe))}) -> {Describe(f.Ret)}",
        IrUnionType u => u.Name,
        _ => t.ToCType()
    };

    /// <summary>
    /// Reports a type-mismatch diagnostic when value cannot be assigned to the target type.
    /// </summary>
    void CheckAssign(IrExpr value, IrType target, string what, ResolveCtx ctx, string code)
    {
        if (value.Type is IrResultType || target is IrResultType) return;
        if (!Assignable(value, target))
            diag.Error(code, ctx.File, value.Span,
                $"cannot assign '{Describe(value.Type)}' to {what} of type '{Describe(target)}'");
    }

    /// <summary>
    /// Emits an EmptyBlock warning when the resolved block has no statements.
    /// </summary>
    void WarnIfEmpty(IrBlock blk, string what, ResolveCtx ctx, TextSpan span)
    {
        if (blk.Stmts.Count == 0)
            diag.Warn(Codes.EmptyBlock, ctx.File, span, $"empty '{what}' body");
    }

    /// <summary>
    /// Validates that the expression is bool-typed for use as a branch condition.
    /// </summary>
    void CheckCondition(IrExpr c, ResolveCtx ctx)
    {
        if (c.Type is IrResultType) return;
        if (c.Type is not IrPrimType { CName: "bool" })
            diag.Error(Codes.ConditionNotBool, ctx.File, c.Span,
                $"condition must be 'bool', got '{Describe(c.Type)}'");
    }

    /// <summary>
    /// Validates that the expression is a legal assignment target (variable, field, element, or deref).
    /// </summary>
    void CheckLValue(IrExpr target, ResolveCtx ctx)
    {
        if (target is IrVar or IrFieldLoad or IrIndex or IrDeref) return;
        diag.Error(Codes.NotAnLvalue, ctx.File, target.Span,
            "assignment target must be a variable, field, or element");
    }

    /// <summary>
    /// Validates both operands of a compound assignment operator for type correctness.
    /// </summary>
    void CheckCompound(string op, IrExpr target, IrExpr value, ResolveCtx ctx)
    {
        bool bitwise = op is "&=" or "|=" or "^=" or "<<=" or ">>=";
        bool okTarget = bitwise ? IsInteger(target.Type) : IsArith(target.Type);
        bool okValue = bitwise ? IsInteger(value.Type) : IsArith(value.Type);
        if (!okTarget)
            diag.Error(Codes.TypeMismatch, ctx.File, target.Span,
                $"operator '{op}' cannot be applied to '{Describe(target.Type)}'");
        else if (!okValue)
            diag.Error(Codes.TypeMismatch, ctx.File, value.Span,
                $"operator '{op}' requires a{(bitwise ? "n integer" : " numeric")} right-hand side, got '{Describe(value.Type)}'");
    }

    /// <summary>
    /// Validates that an explicit cast is valid: numeric, enum-to-int, or pointer (unsafe only).
    /// Reports an error for void, String, or class casts.
    /// </summary>
    void CheckCast(IrExpr value, IrType to, ResolveCtx ctx)
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
    static bool ComparableEq(IrExpr l, IrExpr r)
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

    // Definite-return analysis.
    /// <summary>
    /// Returns true when at least one statement in the list definitely returns on every path.
    /// </summary>
    static bool ReturnsList(IReadOnlyList<IrStmt> stmts) => stmts.Any(DefinitelyReturns);

    /// <summary>
    /// Returns true when the statement definitely returns or throws on every execution path.
    /// </summary>
    static bool DefinitelyReturns(IrStmt s) => s switch
    {
        IrReturn => true,
        IrThrow => true,
        IrBlock b => ReturnsList(b.Stmts),
        IrUnsafeBlock u => ReturnsList(u.Body.Stmts),
        IrIf i => i.Else != null && DefinitelyReturns(i.Then) && DefinitelyReturns(i.Else),
        IrWhile w => w.Cond is IrLitBool { Value: true },
        IrTryCatch t => DefinitelyReturns(t.Try) && DefinitelyReturns(t.Catch),
        IrSwitch sw => sw.Default != null && sw.Cases.All(c => DefinitelyReturns(c.Body))
                       && DefinitelyReturns(sw.Default),
        IrMatch ms => ms.Cases.All(c => DefinitelyReturns(c.Body))
                      && (ms.Default == null || DefinitelyReturns(ms.Default)),
        _ => false
    };

    /// <summary>
    /// Reports MissingReturn when a non-void function body does not definitely return on every path.
    /// </summary>
    void CheckMissingReturn(IrBlock? body, IrType ret, bool isThrows, TextSpan span, string display, ResolveCtx ctx)
    {
        if (body == null || isThrows || ret is IrVoidType || ret is IrResultType) return;
        if (!ReturnsList(body.Stmts))
            diag.Error(Codes.MissingReturn, ctx.File, span, $"'{display}' must return '{Describe(ret)}' on every path");
    }

    /// <summary>
    /// Checks for a redundant trailing 'return;' in a void function, and warns about
    /// unused local variables by walking the body.
    /// </summary>
    void CheckBodyQuality(IrBlock body, IrType ret, TextSpan span, ResolveCtx ctx)
    {
        if (ret is IrVoidType && body.Stmts.Count > 0 && body.Stmts[^1] is IrReturn { Value: null })
            diag.Warn(Codes.RedundantReturn, ctx.File, span, "redundant trailing 'return;'");

        var decls = new List<(string Name, TextSpan Span)>();
        var used = new HashSet<string>();
        bool native = false;
        void Ex(IrExpr? e)
        {
            if (e == null) return;
            if (e is IrVar v) used.Add(v.Name);
            foreach (var c in ChildrenOf(e)) Ex(c);
        }
        void St(IrStmt s)
        {
            switch (s)
            {
                case IrDeclVar d:    decls.Add((d.Name, d.Span)); Ex(d.Init); break;
                case IrAssign a:     Ex(a.Target); Ex(a.Value); break;
                case IrExprStmt es:  Ex(es.Expr); break;
                case IrReturn r:     Ex(r.Value); break;
                case IrIf i:         Ex(i.Cond); St(i.Then); if (i.Else != null) St(i.Else); break;
                case IrWhile w:      Ex(w.Cond); St(w.Body); break;
                case IrFor f:        if (f.Init != null) St(f.Init); Ex(f.Cond); Ex(f.Step); St(f.Body); break;
                case IrForIn fi:     Ex(fi.Collection); St(fi.Body); break;
                case IrTryCatch t:   St(t.Try); St(t.Catch); break;
                case IrSwitch sw:
                    Ex(sw.Scrutinee);
                    foreach (var c in sw.Cases) { foreach (var l in c.Labels) Ex(l); St(c.Body); }
                    if (sw.Default != null) St(sw.Default);
                    break;
                case IrMatch ms:
                    Ex(ms.Scrutinee);
                    foreach (var c in ms.Cases) St(c.Body);
                    if (ms.Default != null) St(ms.Default);
                    break;
                case IrUnsafeBlock u: St(u.Body); break;
                case IrDefer dfr:    St(dfr.Action); break;
                case IrBlock b:      foreach (var x in b.Stmts) St(x); break;
                case IrNativeStmt:   native = true; break;
            }
        }
        St(body);
        if (native) return;
        var seen = new HashSet<string>();
        foreach (var (name, sp) in decls)
            if (seen.Add(name) && !used.Contains(name))
                diag.Warn(Codes.UnusedVariable, ctx.File, sp, $"unused variable '{name}'");
    }

    /// <summary>
    /// Reports PrivateMember when a private member is accessed from outside its declaring class.
    /// </summary>
    void CheckMemberAccess(string owner, string member, ResolveCtx ctx, TextSpan span)
    {
        if (sym.IsPrivateMember(owner, member) && ctx.CurClass != owner)
            diag.Error(Codes.PrivateMember, ctx.File, span,
                $"'{Mangler.DisplayName(owner)}.{member}' is private and cannot be accessed from outside '{Mangler.DisplayName(owner)}'");
    }

    /// <summary>
    /// Reports ThrowsOutsideTry when a throwing call appears outside a try block or throws function.
    /// </summary>
    void CheckThrowsHandled(ResolveCtx ctx, TextSpan span)
    {
        if (!ctx.InTry && !ctx.InThrowsFunc)
            diag.Error(Codes.ThrowsOutsideTry, ctx.File, span,
                "throwing call must be inside a 'try' block or a 'throws' function");
    }

    /// <summary>
    /// Returns the direct child expressions of the given IR expression node.
    /// </summary>
    static List<IrExpr> ChildrenOf(IrExpr e) => e switch
    {
        IrFieldLoad fl => [fl.Obj],
        IrIndex ix => [ix.Obj, ix.Idx],
        IrStaticCall sc => sc.Args,
        IrInstanceCall ic => [ic.Recv, .. ic.Args],
        IrThrowsCall tc => tc.Args,
        IrThrowsInstanceCall ti => [ti.Recv, .. ti.Args],
        IrBinOp b => [b.Left, b.Right],
        IrTernary t => [t.Cond, t.Then, t.Else],
        IrUnaryOp u => [u.Operand],
        IrPostfix p => [p.Operand],
        IrCast c => [c.Value],
        IrNew n => n.Args,
        IrNewInit ni => [.. ni.Args, .. ni.Inits],
        IrArrayLit al => al.Elems,
        IrInterp ip => ip.Parts,
        IrAddrOf a => [a.Target],
        IrDeref d => [d.Ptr],
        IrIndirectCall ic => [ic.Target, .. ic.Args],
        IrUnionConstruct uc => uc.Args,
        IrUnionField uf => [uf.Union],
        _ => []
    };

    /// <summary>
    /// Reports ThrowsOutsideTry when a throwing call is nested inside a non-statement expression.
    /// The allowRoot flag permits the call itself at the top of the expression tree.
    /// </summary>
    void ForbidNestedThrows(IrExpr? e, ResolveCtx ctx, bool allowRoot)
    {
        if (e == null) return;
        if (!allowRoot && e is IrThrowsCall or IrThrowsInstanceCall)
            diag.Error(Codes.ThrowsOutsideTry, ctx.File, e.Span,
                "throwing call cannot appear inside a larger expression");
        foreach (var c in ChildrenOf(e))
            ForbidNestedThrows(c, ctx, allowRoot: false);
    }

    /// <summary>
    /// Returns true when the expression is side-effect-free and safe to re-emit multiple times.
    /// </summary>
    static bool IsPure(IrExpr e) => e switch
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

    /// <summary>
    /// Returns the expression unchanged when it is pure, or hoists it into a fresh
    /// declared temporary and returns a reference to that temp.
    /// </summary>
    IrExpr HoistIfImpure(IrExpr e, string prefix, List<IrStmt> stmts)
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
    static IrStmt Seq(List<IrStmt> stmts, TextSpan span) =>
        stmts.Count == 1 ? stmts[0] with { Span = span } : new IrBlock(stmts) { Span = span };

    /// <summary>
    /// Computes the common type for two ternary arms, or null when they cannot be unified.
    /// </summary>
    static IrType? UnifyTernary(IrExpr a, IrExpr b)
    {
        if (a is IrLitNull && b is IrLitNull) return null;
        if (a is IrLitNull) return b.Type is IrClassRef or IrPtrType ? b.Type : null;
        if (b is IrLitNull) return a.Type is IrClassRef or IrPtrType ? a.Type : null;
        if (SameType(a.Type, b.Type)) return a.Type;
        if (IsNum(a.Type) && IsNum(b.Type)) return NumRank(a.Type) >= NumRank(b.Type) ? a.Type : b.Type;
        if (a.Type.IsString && b.Type.IsString) return IrType.String;
        if (a.Type is IrPtrType ap && b.Type is IrPtrType bp)
            return SameType(ap.Inner, bp.Inner) ? a.Type
                 : ap.Inner is IrVoidType       ? a.Type
                 : bp.Inner is IrVoidType       ? b.Type : null;
        return null;
    }

    /// <summary>
    /// Adapts an expression to a unified type: retypes a null literal, casts a narrower numeric up.
    /// </summary>
    static IrExpr CoerceTo(IrExpr e, IrType t)
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
    IrExpr Coerce(IrExpr e, IrType expected, ResolveCtx ctx)
    {
        if (expected is IrArrayType at && e is IrArrayLit lit && lit.Elems.Count == at.Size)
        {
            var coerced = lit.Elems.Select(x => Coerce(x, at.Elem, ctx)).ToList();
            return new IrArrayLit(Arr(at.Elem, at.Size), coerced) { Span = e.Span };
        }
        return e;
    }

    /// <summary>
    /// Wraps or converts an expression to a String value. Requires ARC intrinsic bindings;
    /// fully implemented when string interpolation support is added.
    /// </summary>
    IrExpr EnsureString(IrExpr e, ResolveCtx ctx) =>
        throw new NotImplementedException("EnsureString not yet implemented -- string interpolation added in a later commit");

    /// <summary>
    /// Extracts the class name from a class-reference type, or null for non-class types.
    /// Follows one level of pointer indirection for pointer-to-class patterns.
    /// </summary>
    static string? ClassNameOf(IrType t) => t switch
    {
        IrClassRef cr => cr.ClassName,
        IrPtrType pt => ClassNameOf(pt.Inner),
        _ => null
    };

    // Scope stack -- lexical variable scoping with ref tracking.
    /// <summary>
    /// Maintains the chain of lexical scopes for variable declarations, tracking ref parameters.
    /// </summary>
    sealed class ScopeStack
    {
        readonly ScopeStack? _parent;
        readonly Dictionary<string, IrType> _vars;
        readonly HashSet<string> _refs;

        /// <summary>
        /// Constructs a root scope with no parent.
        /// </summary>
        public ScopeStack() { _parent = null; _vars = new(); _refs = new(); }

        ScopeStack(ScopeStack parent) { _parent = parent; _vars = new(); _refs = new(); }

        /// <summary>
        /// Creates a child scope nested inside this one.
        /// </summary>
        public ScopeStack Push() => new(this);

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
        public bool DeclaredHere(string name) => _vars.ContainsKey(name);

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

    /// <summary>
    /// Immutable resolution context that flows through the AST walk, carrying the current file,
    /// realm, class, function, and loop/unsafe/try depth information.
    /// </summary>
    readonly record struct ResolveCtx(
        string     File,
        string     Context,
        string     CurClass,
        string?    CurFunc,
        bool       InStatic,
        bool       InUnsafe,
        bool       InTry,
        bool       InThrowsFunc,
        string     CatchLabel,
        int        LoopDepth,
        ScopeStack Scope,
        bool       InDefer = false)
    {
        /// <summary>
        /// Returns a context with the current class updated.
        /// </summary>
        public ResolveCtx WithClass(string c) => this with { CurClass = c };

        /// <summary>
        /// Returns a context with the current function name updated.
        /// </summary>
        public ResolveCtx WithFunc(string f) => this with { CurFunc = f };

        /// <summary>
        /// Returns a context with the static flag updated.
        /// </summary>
        public ResolveCtx WithStatic(bool s) => this with { InStatic = s };

        /// <summary>
        /// Returns a context with the unsafe flag updated.
        /// </summary>
        public ResolveCtx WithUnsafe(bool u) => this with { InUnsafe = u };

        /// <summary>
        /// Returns a context that marks entry into a try block with the given catch label.
        /// </summary>
        public ResolveCtx WithTry(string label) => this with { InTry = true, CatchLabel = label };

        /// <summary>
        /// Returns a context with the throws-function flag updated.
        /// </summary>
        public ResolveCtx WithThrowsFunc(bool t) => this with { InThrowsFunc = t };

        /// <summary>
        /// Returns a context with the realm context string updated.
        /// </summary>
        public ResolveCtx WithContext(string ctx) => this with { Context = ctx };

        /// <summary>
        /// Returns a context that marks entry into a defer body.
        /// </summary>
        public ResolveCtx WithDefer() => this with { InDefer = true };

        /// <summary>
        /// Returns a context with a new child scope pushed.
        /// </summary>
        public ResolveCtx PushScope() => this with { Scope = Scope.Push() };
    }

    /// <summary>
    /// Returns true when the type is any numeric type (integer or float).
    /// </summary>
    static bool IsNum(IrType t) => t.IsNumeric || t.IsFloat;

    /// <summary>
    /// Returns true when the type is numeric and not bool. Used for arithmetic operators.
    /// </summary>
    static bool IsArith(IrType t) => IsNum(t) && t is not IrPrimType { CName: "bool" };

    /// <summary>
    /// Returns true when the type is an integer type (not float, not bool).
    /// </summary>
    static bool IsInteger(IrType t) => t.IsNumeric && t is not IrPrimType { CName: "bool" };

    // Entry point.
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
    void CollectFuncTemplates(TopLevel[] items, string context, string file)
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
    (NativeSection, Visibility) Unknown(string target, ResolveCtx ctx, TextSpan span)
    {
        diag.Error(Codes.UnknownPreambleTarget, ctx.File, span,
            $"unknown @preamble target '{target}'; expected 'boot', 'kernel', or 'user'");
        return (NativeSection.Preamble, Visibility.Shared);
    }

    /// <summary>
    /// Resolves a single top-level declaration and adds its output to the module.
    /// </summary>
    void ResolveTop(TopLevel item, ResolveCtx ctx, IrModule module)
    {
        switch (item)
        {
            case ImportDecl:
            case EnvironmentDecl:
            case ExternFuncDecl:
                break;
            case NativeBlock nb:
            {
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
    static Visibility VisOf(string ctx) => ctx switch
    {
        "kernel" => Visibility.Kernel,
        "user" => Visibility.User,
        _ => Visibility.Shared
    };

    // Type conversion.
    /// <summary>
    /// Converts a Gata type string to its IR type. Returns IrVoidType for null or "void".
    /// Handles function types, fixed-array types, pointer types, primitives, enums, unions, and classes.
    /// </summary>
    public IrType ResolveType(string? t) => ResolveType(t.AsSpan());

    /// <summary>
    /// Converts a Gata type span to its IR type. Returns IrVoidType for null or "void".
    /// Handles function types, fixed-array types, pointer types, primitives, enums, unions, and classes.
    /// </summary>
    IrType ResolveType(ReadOnlySpan<char> t)
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
        if (t.EndsWith("*"))          return new IrPtrType(ResolveType(t[..^1]));
        if (t.Equals("String", StringComparison.Ordinal))            return IrType.String;
        if (t.Equals("Process", StringComparison.Ordinal) || t.Equals("Thread", StringComparison.Ordinal)) return new IrPtrType(IrType.Void);
        if (PrimTypes.IsPrim(t))      return new IrPrimType(t.ToString());
        if (sym.IsEnum(t))            return new IrEnumType(t.ToString());
        if (sym.IsUnion(t))           return new IrUnionType(t.ToString());
        return new IrClassRef(t.ToString());
    }

    // Declaration resolvers -- class/method/function resolution added in a later commit.
    /// <summary>
    /// Resolves a class declaration to its IR form. Implemented when class resolution is added.
    /// </summary>
    IrClass ResolveClass(ClassDecl cd, ResolveCtx ctx) =>
        throw new NotImplementedException($"[TypeResolver] ResolveClass('{cd.Name}') -- class resolution not yet implemented");

    /// <summary>
    /// Resolves a method declaration to its IR form. Implemented when class resolution is added.
    /// </summary>
    IrFunction ResolveMethod(string cls, MethodDecl md, ResolveCtx ctx, bool lib, Visibility vis, bool isModule) =>
        throw new NotImplementedException($"[TypeResolver] ResolveMethod('{cls}.{md.Name}') -- class resolution not yet implemented");

    /// <summary>
    /// Resolves an operator declaration to its IR form. Implemented when class resolution is added.
    /// </summary>
    IrOperator ResolveOperator(string cls, OperatorDecl od, ResolveCtx ctx, bool lib, Visibility vis) =>
        throw new NotImplementedException($"[TypeResolver] ResolveOperator('{cls}.{od.Op}') -- class resolution not yet implemented");

    /// <summary>
    /// Resolves a free function declaration to its IR form. Implemented when function call resolution is added.
    /// </summary>
    IrFunction ResolveFreeFunc(FuncDecl fd, ResolveCtx ctx) =>
        throw new NotImplementedException($"[TypeResolver] ResolveFreeFunc('{fd.Name}') -- function resolution not yet implemented");

    /// <summary>
    /// Resolves a method body or native block to its IR block and raw C strings.
    /// Implemented alongside function resolution.
    /// </summary>
    (IrBlock? Body, string? Kernel, string? User) ResolveBodyOrNative(MethodBody b, ResolveCtx ctx, IrType ret) =>
        throw new NotImplementedException("[TypeResolver] ResolveBodyOrNative -- function resolution not yet implemented");

    /// <summary>
    /// Resolves a process declaration to its IR form. Implemented when process resolution is added.
    /// </summary>
    IrProcess ResolveProcess(ProcessDecl pd, ResolveCtx ctx) =>
        throw new NotImplementedException($"[TypeResolver] ResolveProcess('{pd.Name}') -- process resolution not yet implemented");

    /// <summary>
    /// Stamps and resolves each generic function instantiation discovered during the main pass.
    /// Fully implemented when generic call resolution is added.
    /// </summary>
    void DrainGenericInstances(IrModule module) { }

    /// <summary>
    /// Checks whether a bare call is a retain/release ARC intrinsic and returns the appropriate
    /// IR node. Returns null for all other names. Fully implemented alongside ARC support.
    /// </summary>
    IrExpr? TryResolveArcIntrinsic(string name, List<IrExpr> args, ResolveCtx ctx, TextSpan span) => null;

    /// <summary>
    /// Resolves an enum declaration to its IR form.
    /// Members may carry optional explicit integer values parsed from integer literals.
    /// </summary>
    IrEnum ResolveEnum(EnumDecl ed, ResolveCtx ctx)
    {
        var members = new List<(string, string?)>();
        foreach (var m in ed.Members)
        {
            string? cval = null;
            if (m.Value is IntLitExpr il && TryParseIntLit(il.Value.AsSpan(), out var v, out _, out _))
                cval = v.ToString();
            else if (m.Value != null)
                diag.Error(Codes.TypeMismatch, ctx.File, ed.Span,
                    $"enum '{ed.Name}' member '{m.Name}' must be an integer literal");
            members.Add((m.Name, cval));
        }
        return new IrEnum(ed.Name, Mangler.Enum(ed.Name), members);
    }

    /// <summary>
    /// Resolves a union declaration to its IR form.
    /// Variant fields must be unmanaged value types; class references are rejected.
    /// </summary>
    IrUnion ResolveUnion(UnionDecl ud, ResolveCtx ctx)
    {
        var variants = new List<IrUnionVariant>();
        foreach (var v in ud.Variants)
        {
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

    // Blocks and statements.
    /// <summary>
    /// Resolves a block by pushing a new scope, resolving all statements, and warning on unreachable code.
    /// </summary>
    IrBlock ResolveBlock(Block b, ResolveCtx ctx, IrType retType)
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
    IrStmt ResolveStmt(Stmt s, ResolveCtx ctx, IrType retType)
    {
        var r = ResolveStmtCore(s, ctx, retType);
        return r.Span.IsNone ? r with { Span = s.Span } : r;
    }

    /// <summary>
    /// Core statement resolver. Currently handles native blocks, nested blocks, and variable declarations.
    /// Additional statement forms are added in later commits.
    /// </summary>
    IrStmt ResolveStmtCore(Stmt s, ResolveCtx ctx, IrType retType)
    {
        switch (s)
        {
            case NativeStmt ns: return new IrNativeStmt(ns.Body.KernelC, ns.Body.UserC);
            case Block b:       return ResolveBlock(b, ctx, retType);
            case LetStmt ls:    return ResolveLet(ls, ctx);
            default:
                throw new NotImplementedException($"[TypeResolver] unhandled Stmt: {s.GetType().Name} -- additional statement forms added in later commits");
        }
    }

    /// <summary>
    /// Wraps a single statement in an IrBlock, pushing a new scope.
    /// When the statement is already a Block, resolves it directly without double-wrapping.
    /// </summary>
    IrBlock WrapBlock(Stmt s, ResolveCtx ctx, IrType retType)
    {
        if (s is Block b) return ResolveBlock(b, ctx, retType);
        var inner = ctx.PushScope();
        return new IrBlock([ResolveStmt(s, inner, retType)]) { Span = s.Span };
    }

    /// <summary>
    /// Resolves a let declaration: infers or checks its type, resolves the initializer,
    /// checks assignability, and declares the variable in the current scope.
    /// </summary>
    IrDeclVar ResolveLet(LetStmt ls, ResolveCtx ctx)
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
        }
        else type = IrType.Int;

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

    // Expressions.
    /// <summary>
    /// Resolves an expression and propagates the source span when the resolver did not set one.
    /// </summary>
    IrExpr ResolveExpr(Expr e, ResolveCtx ctx)
    {
        var r = ResolveExprCore(e, ctx);
        return r.Span.IsNone ? r with { Span = e.Span } : r;
    }

    /// <summary>
    /// Core expression resolver. Handles literals, identifiers, casts, postfix, unary, and binary expressions.
    /// Additional expression forms added in later commits.
    /// </summary>
    IrExpr ResolveExprCore(Expr e, ResolveCtx ctx)
    {
        switch (e)
        {
            case IntLitExpr il:
                if (!TryParseIntLit(il.Value.AsSpan(), out var ival, out var ity, out var ictext))
                    diag.Error(Codes.TypeMismatch, ctx.File, e.Span,
                        $"integer literal '{il.Value}' does not fit in 64 bits");
                return new IrLitInt(ival, ity, ictext);
            case CharLitExpr cl:  return new IrLitChar(cl.Value);
            case FloatLitExpr fl: return new IrLitFloat(fl.Value, FloatLitType(fl.Value));
            case BoolLitExpr bl:  return new IrLitBool(bl.Value == "true");
            case StrLitExpr sl:   return new IrLitString(sl.Value);
            case NullExpr:        return new IrLitNull(IrType.Void);
            case IdentExpr ie:    return ResolveIdent(ie, ctx);
            case CastExpr ce:
            {
                CheckType(ce.TargetType, ctx, ce.Span, allowVoid: true);
                var inner = ResolveExpr(ce.Value, ctx);
                var to = ResolveType(ce.TargetType);
                CheckCast(inner, to, ctx);
                return new IrCast(to, inner);
            }
            case PostfixExpr pf:  return new IrPostfix(pf.Op, ResolveExpr(pf.Operand, ctx));
            case UnaryExpr un:    return ResolveUnary(un, ctx);
            case BinExpr be:      return ResolveBin(be, ctx);
            default:
                throw new NotImplementedException($"[TypeResolver] unhandled Expr: {e.GetType().Name} -- additional expression forms added in later commits");
        }
    }

    /// <summary>
    /// Resolves a unary expression, validating that the operand type is compatible with the operator.
    /// </summary>
    IrExpr ResolveUnary(UnaryExpr un, ResolveCtx ctx)
    {
        var operand = ResolveExpr(un.Operand, ctx);
        if (un.Op == "!" && operand.Type is not IrPrimType { CName: "bool" })
            diag.Error(Codes.TypeMismatch, ctx.File, un.Span,
                $"operator '!' requires 'bool', got '{Describe(operand.Type)}'");
        else if (un.Op == "-" && !IsArith(operand.Type))
            diag.Error(Codes.TypeMismatch, ctx.File, un.Span,
                $"unary '-' requires a numeric operand, got '{Describe(operand.Type)}'");
        else if (un.Op == "~" && !IsInteger(operand.Type))
            diag.Error(Codes.TypeMismatch, ctx.File, un.Span,
                $"operator '~' requires an integer operand, got '{Describe(operand.Type)}'");
        var t = un.Op == "!" ? IrType.Bool : operand.Type;
        return new IrUnaryOp(un.Op, operand, t);
    }

    /// <summary>
    /// Resolves a binary expression. Handles string concatenation, operator overloading,
    /// pointer arithmetic, logical, equality, relational, bitwise, and arithmetic operators.
    /// </summary>
    IrExpr ResolveBin(BinExpr be, ResolveCtx ctx)
    {
        var left = ResolveExpr(be.Left, ctx);
        var right = ResolveExpr(be.Right, ctx);

        // String concatenation: '+' with a String operand stringifies the other side.
        if (be.Op == "+" && (left.Type.IsString || right.Type.IsString))
        {
            var sop = sym.LookupOperator("String", "+");
            string cn = sop?.CName ?? Mangler.Operator("String", "+");
            return new IrStaticCall(cn, IrType.String, [EnsureString(left, ctx), EnsureString(right, ctx)]);
        }

        string? lhsClass = ClassNameOf(left.Type);
        if (lhsClass != null && sym.LookupOperator(lhsClass, be.Op) is { } op)
            return new IrStaticCall(op.CName, ResolveType(op.Type), [left, right]);

        if (left.Type is IrPtrType && be.Op is "+" or "-" && right.Type.IsNumeric)
        {
            if (!ctx.InUnsafe)
                diag.Error(Codes.UnsafeRequired, ctx.File, be.Span, "pointer arithmetic requires an 'unsafe' block");
            return new IrBinOp(be.Op, left, right, left.Type);
        }

        if (be.Op is "&&" or "||")
        {
            if (left.Type is not IrPrimType { CName: "bool" } || right.Type is not IrPrimType { CName: "bool" })
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"operator '{be.Op}' requires 'bool' operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            return new IrBinOp(be.Op, left, right, IrType.Bool);
        }

        if (be.Op is "==" or "!=")
        {
            if (!ComparableEq(left, right))
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"'{be.Op}' operands are not comparable: '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            return new IrBinOp(be.Op, left, right, IrType.Bool);
        }

        if (be.Op is "<" or ">" or "<=" or ">=")
        {
            if (!(IsArith(left.Type) && IsArith(right.Type)))
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"operator '{be.Op}' requires numeric operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            return new IrBinOp(be.Op, left, right, IrType.Bool);
        }

        if (be.Op is "&" or "|" or "^" or "<<" or ">>")
        {
            if (!(IsInteger(left.Type) && IsInteger(right.Type)))
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"operator '{be.Op}' requires integer operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            IrType bt = be.Op is "<<" or ">>" ? left.Type
                      : NumRank(left.Type) >= NumRank(right.Type) ? left.Type : right.Type;
            return new IrBinOp(be.Op, left, right, bt);
        }

        if (!(IsArith(left.Type) && IsArith(right.Type)))
            diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                $"operator '{be.Op}' cannot be applied to '{Describe(left.Type)}' and '{Describe(right.Type)}'");
        IrType t = NumRank(left.Type) >= NumRank(right.Type) ? left.Type : right.Type;
        return new IrBinOp(be.Op, left, right, t);
    }

    // Parse an integer literal lexeme (hex or decimal, with an optional u/U/l/L suffix).
    // On success: v = 64-bit bit pattern, type = selected C type, ctext = verbatim C text or null.
    /// <summary>
    /// Parses an integer literal lexeme into its bit pattern, IR type, and optional verbatim C text.
    /// Returns false when the magnitude does not fit in 64 bits.
    /// </summary>
    static bool TryParseIntLit(ReadOnlySpan<char> raw, out long v, out IrType type, out string? ctext)
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
    static IrPrimType FloatLitType(string raw) =>
        raw.Length > 0 && raw[^1] is 'f' or 'F' ? IrType.Float : IrType.Double;

    /// <summary>
    /// Resolves a bare identifier expression to a variable reference, bool/null literal,
    /// self-expression, or class reference. Reports UndefinedVariable for unknown names.
    /// </summary>
    IrExpr ResolveIdent(IdentExpr ie, ResolveCtx ctx)
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
    /// Coerces each resolved argument to its declared parameter type and validates ref passing.
    /// Fully implemented when function call resolution is added.
    /// </summary>
    void CoerceArgs(List<IrExpr> args, MethodSig? sig, ResolveCtx ctx, Expr[]? astArgs = null) { }

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

        public override bool Equals(object? obj) => obj is FuncPtrKey other && Equals(other);

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
