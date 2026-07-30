namespace Appa;

internal sealed class TypeResolver(
    SymbolTable sym,
    HashSet<string> hasInit,
    HashSet<string> nativeStructs,
    HashSet<string> opaqueFieldClasses,
    Dictionary<string, HashSet<string>> visible,
    Dictionary<string, string> genericRequestFile,
    bool releaseMode,
    DiagnosticBag diag)
{
    // Modules visible to the file currently being resolved (set per file).
    private HashSet<string> _scope = [];

    // Scope for the file currently being resolved, before any per-item widening.
    private HashSet<string> _fileScope = [];

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

    /// <summary>
    /// Picks the generic free-function template a bare call resolves to: an own-file private
    /// template always wins, otherwise the first in-scope public one.
    /// </summary>
    private (FuncDecl Decl, string File, Realm Realm)? ResolveFuncTemplate(
        string name, string ctxFile, out List<string> collidingFiles)
    {
        collidingFiles = [];
        if (!_funcTemplates.TryGetValue(name, out var bucket) || bucket.Count == 0) return null;

        var ownPrivate = bucket.Find(e => e.IsPrivate && e.File == ctxFile);
        if (ownPrivate.Decl != null) return (ownPrivate.Decl, ownPrivate.File, ownPrivate.Realm);

        var publicInScope = bucket.FindAll(e => !e.IsPrivate && _scope.Contains(e.File));
        if (publicInScope.Count == 0) return null;
        if (publicInScope.Count > 1)
            collidingFiles = [.. publicInScope.Select(e => e.File).Distinct()];

        var chosen = publicInScope[0];
        return (chosen.Decl, chosen.File, chosen.Realm);
    }

    /// <summary>
    /// Resolves `ns.name(...)` where `ns` is an in-scope file's basename - the escape hatch for a
    /// collision that cannot be qualified through a class or module. Null if nothing matches, so
    /// the caller falls through to the instance-receiver path.
    /// </summary>
    private IrExpr? TryResolveFileNamespacedCall(string ns, string name, List<IrExpr> args, ResolveCtx ctx, CallExpr ce)
    {
        if (_funcTemplates.TryGetValue(name, out var bucket))
        {
            var match = bucket.Find(e => Path.GetFileNameWithoutExtension(e.File) == ns
                && (e.File == ctx.File || (!e.IsPrivate && _scope.Contains(e.File))));
            if (match.Decl != null)
                return ResolveGenericCall((match.Decl, match.File, match.Realm), args, ctx, ce.Span, ce.Args);
        }

        var priv = sym.LookupPrivateFunc(ctx.File, name);
        if (priv != null && Path.GetFileNameWithoutExtension(ctx.File) == ns)
        {
            return BuildCall(sym.PrivateFuncOverloads(ctx.File, name), priv, args, name,
                Mangler.PrivateFreeFunc(Mangler.FileToken(ctx.File), name, [], false), null, ctx, ce);
        }

        var fsym = sym.LookupFreeFunc(name);
        if (fsym != null && Path.GetFileNameWithoutExtension(fsym.Module) == ns && _scope.Contains(fsym.Module))
        {
            return BuildCall(sym.FuncOverloads(name), fsym, args, name,
                Mangler.FreeFunc(name, [], false, false, false), null, ctx, ce);
        }

        return null;
    }

    // Every distinct fixed-array (T, N) pair used; the emitter stamps one struct per pair.
    // Deduped on the way in, mirroring _funcPtrTypes below, so the emitter's sort in
    // EmitArrayTypes runs over distinct entries rather than one per syntactic occurrence.
    private readonly List<IrArrayType> _arrays = [];
    private readonly HashSet<string> _arraysSeen = [];
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
        if (_arraysSeen.Add(a.MangledName)) _arrays.Add(a);
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

    // Generic free function templates, bucketed by name (several files may each declare their
    // own private generic under the same name without clobbering one another); each distinct
    // instantiation is stamped once.
    private readonly Dictionary<string, List<(FuncDecl Decl, string File, Realm Realm, bool IsPrivate)>> _funcTemplates = [];
    
    // Generic method templates on classes/modules, keyed by owner+name; mirrors _funcTemplates.
    private readonly Dictionary<MemberKey, (MethodDecl Decl, string File, Realm Realm)> _methodTemplates = [];
    private readonly Queue<(FuncDecl Decl, string File, Realm Realm, Dictionary<string, TypeSpec> Binds, string Mangled)> _genericQueue = new();
    private readonly Queue<(MethodDecl Decl, string Owner, string File, Realm Realm, Dictionary<string, TypeSpec> Binds, string Mangled)> _genericMethodQueue = new();
    private readonly HashSet<string> _genericSeen = [];
    private int _labelSeq;

    // Generic templates that at least one call site instantiated, so the never-used ones can be told apart
    private readonly HashSet<(string File, string Name)> _usedFuncTemplates = [];
    private readonly HashSet<MemberKey> _usedMethodTemplates = [];

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
    /// Validates that a type spec refers to real, in-scope types, node by node. Each node carries
    /// its own source span, so the caret lands on the offending part of a compound type instead of
    /// the whole declaration.
    /// </summary>
    private void CheckType(TypeSpec? t, ResolveCtx ctx, TextSpan span, bool allowVoid = false)
    {
        switch (t)
        {
            case null:
                return;
            case FuncSpec f:
                foreach (var p in f.Params) CheckType(p, ctx, Sp(p, span));
                CheckType(f.Ret, ctx, Sp(f.Ret, span), allowVoid: true);
                return;
            case ArraySpec a:
                if (!(TryParseIntLit(a.SizeText, out var n, out _, out _) && n > 0))
                    diag.Error(Codes.UndefinedType, ctx.File, Sp(a, span),
                        $"invalid fixed-array size in '{a.ToSpecString()}'");
                else
                    CheckType(a.Elem, ctx, Sp(a.Elem, span));
                return;
            case PtrSpec ptr:
            {
                // Any level of pointer to void is legal; otherwise validate the pointee.
                TypeSpec inner = ptr.Inner;
                while (inner is PtrSpec ip) inner = ip.Inner;
                if (inner is NamedSpec { Name: "void", Args.Length: 0 }) return;
                CheckType(inner, ctx, Sp(inner, span));
                return;
            }
            case NamedSpec { Name: NamedSpec.Poison }:
                return;
            case NamedSpec nm:
            {
                string name = nm.Mangled;
                if (name == "void")
                {
                    if (!allowVoid)
                        diag.Error(Codes.UndefinedType, ctx.File, Sp(nm, span), "'void' is not a value type");
                    return;
                }
                if (SymbolTable.Primitives.Contains(name)) return;
                if (BuiltinTypes.All.Contains(name)) return;
                if (sym.IsEnum(name)) return;
                if (sym.IsUnion(name)) return;
                if (ClassInScope(name)) return;
                if (sym.IsClass(name))
                {
                    var scopeHints = new List<string>();
                    AddInstantiationHint(scopeHints, ctx);
                    diag.Error(Codes.UndefinedType, ctx.File, Sp(nm, span),
                        $"type '{Mangler.DisplayName(name)}' is not in scope; import its module",
                        scopeHints.Count == 0 ? null : [.. scopeHints]);
                    return;
                }
                if (ReportNotVisible("type", nm.Name, ctx.File, Sp(nm, span))) return;
                if (ReportWrongKind(Codes.UndefinedType, nm.Args.Length > 0 ? "a generic type" : "a type",
                                    nm.Name, ctx.File, Sp(nm, span))) return;
                if (Mangler.GenericFailed(name)) return;
                var unknownHints = new List<string>();
                AddInstantiationHint(unknownHints, ctx);
                diag.Error(Codes.UndefinedType, ctx.File, Sp(nm, span), $"unknown type '{Written(nm)}'",
                    unknownHints.Count == 0 ? null : [.. unknownHints]);
                return;
            }
        }
    }

    /// <summary>
    /// Prefers the spec node's own span; falls back to the declaration span when the node was
    /// synthesized without one.
    /// </summary>
    private static TextSpan Sp(TypeSpec t, TextSpan fallback)
    {
        return t.Span.IsNone ? fallback : t.Span;
    }

    /// <summary>
    /// Validates that no two parameters in the list share the same name.
    /// </summary>
    private void CheckParams(Param[] ps, ResolveCtx ctx)
    {
        var seen = new HashSet<string>();
        foreach (var p in ps)
        {
            if (!seen.Add(p.Name))
                diag.Error(Codes.DuplicateName, ctx.File, p.Span, $"duplicate parameter '{p.Name}'");
            CheckNotReservedLocal(p.Name, p.Span, "parameter", ctx);
        }
    }

    /// <summary>
    /// Rejects a local or parameter name that the compiler also generates for its own temporaries.
    /// Both would be emitted verbatim into the same C scope, so the two declarations would collide,
    /// and no renaming rule can separate them after the fact.
    /// </summary>
    private void CheckNotReservedLocal(string name, TextSpan span, string what, ResolveCtx ctx)
    {
        if (!Mangler.IsReservedLocal(name)) return;
        diag.Error(Codes.DuplicateName, ctx.File, span,
            $"'{name}' is reserved for compiler-generated {what}s",
            ["pick another name; this shape is used for the temporaries lowering introduces"]);
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
    /// The common tail of every resolved call: pick the overload, settle on a C name, resolve the
    /// return type, coerce the arguments, then build the matching IR node.
    /// </summary>
    private IrExpr BuildCall(IReadOnlyList<Symbol> cands, Symbol? primary, List<IrExpr> args,
                             string display, string fallbackCName, IrExpr? recv,
                             ResolveCtx ctx, CallExpr ce)
    {
        var chosen = ChooseOverload(cands, primary, args, display, ctx, ce.Span);
        string cn = chosen?.CName ?? fallbackCName;
        var ret = chosen != null ? ResolveType(chosen.Type) : IrType.Void;
        CoerceArgs(args, chosen?.Sig, ctx, ce.Args);

        if (chosen?.Sig?.IsThrows == true)
        {
            CheckThrowsHandled(ctx, ce.Span);
            return recv == null
                ? new IrThrowsCall(cn, ret, args)
                : new IrThrowsInstanceCall(recv, cn, ret, args);
        }
        return recv == null
            ? new IrStaticCall(cn, ret, args)
            : new IrInstanceCall(recv, cn, ret, args);
    }

    /// <summary>
    /// Picks the best-matching overload from the candidates for the given argument list. Reports a
    /// diagnostic when no overload matches or multiple overloads tie.
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

    /// <summary>
    /// Returns the conversion cost from the argument's type to the target type, or null when the
    /// types are incompatible.
    /// </summary>
    private static int? ArgConvCost(IrExpr arg, IrType to)
    {
        var from = arg.Type;
        if (from.IsError || to.IsError) return 0;
        if (arg is IrLitNull) return to is IrClassRef or IrPtrType or IrFuncPtrType ? 0 : null;
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
    /// Ranks live in the primitive table (PrimTypes) alongside each type's other facts.
    /// </summary>
    private static int NumRank(IrType t)
    {
        return t is IrPrimType p ? PrimTypes.Rank(p.CName) : 4;
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
    /// Returns true when value's type is assignment-compatible with the target type, accounting for
    /// implicit numeric widening, null-to-reference, and pointer covariance.
    /// </summary>
    private static bool Assignable(IrExpr value, IrType to)
    {
        var from = value.Type;
        if (from.IsError || to.IsError) return true;
        if (value is IrLitNull) return to is IrClassRef or IrPtrType or IrFuncPtrType;
        if (SameType(from, to)) return true;
        if (to is IrVoidType) return false;
        if ((value is IrLitChar || LiteralValue(value) is not null) && IsNum(to)) return true;
        if (value is IrLitFloat && to.IsFloat) return true;
        if (IsNum(from) && IsNum(to)) return NumRank(from) <= NumRank(to);
        if (from.IsString && to.IsString) return true;
        if (from is IrPtrType fp && to is IrPtrType tp)
            return SameType(fp.Inner, tp.Inner) || fp.Inner is IrVoidType || tp.Inner is IrVoidType;
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
            IrUnionType u => Mangler.DisplayName(u.Name),
            IrEnumType e => Mangler.DisplayName(e.Name),
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
        {
            diag.Error(code, ctx.File, value.Span,
                $"cannot assign '{Describe(value.Type)}' to {what} of type '{Describe(target)}'");
            return;
        }
        CheckLiteralFits(value, target, what, ctx);
    }

    /// <summary>
    /// Rejects an integer literal too large for the type it is being stored in
    /// </summary>
    private void CheckLiteralFits(IrExpr value, IrType target, string what, ResolveCtx ctx)
    {
        if (LiteralValue(value) is not { } n) return;
        if (target is not IrPrimType pt) return;
        int bits = PrimTypes.IntBits(pt.CName);
        if (bits is 0 or 1 or 64) return;

        bool unsigned = target.IsUnsigned;
        long lo = unsigned ? 0 : -(1L << (bits - 1));
        long hi = unsigned ? (1L << bits) - 1 : (1L << (bits - 1)) - 1;
        if (n >= lo && n <= hi) return;

        diag.Error(Codes.TypeMismatch, ctx.File, value.Span,
            $"{n} does not fit in '{Describe(target)}', the type of {what}",
            [$"'{Describe(target)}' holds {lo} to {hi}; the conversion is silent, so this would store " +
             $"{Truncate(n, bits, unsigned)}",
             "widen the type, or write the value you meant"]);
    }

    /// <summary>
    /// The constant an expression denotes when it is an integer literal, optionally negated. Only
    /// these two shapes: anything else may depend on values this pass cannot see.
    /// </summary>
    private static long? LiteralValue(IrExpr e) => e switch
    {
        IrLitInt li => li.Value,
        IrUnaryOp { Op: UnOp.Neg, Operand: IrLitInt li } => -li.Value,
        _ => null
    };

    /// <summary>
    /// What C would actually store, for the hint.
    /// </summary>
    private static long Truncate(long n, int bits, bool unsigned)
    {
        ulong masked = (ulong)n & (bits == 64 ? ulong.MaxValue : (1UL << bits) - 1);
        if (unsigned || bits == 64) return (long)masked;
        return (masked & (1UL << (bits - 1))) != 0 ? (long)(masked - (1UL << bits)) : (long)masked;
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
    /// Warns when an expression used as a statement computes a value and then throws it away
    /// without doing anything else. IsPure is exactly the right test for the lowered form: every
    /// shape it accepts is side-effect-free, so evaluating it for its own sake is dead work.
    /// </summary>
    private void WarnIfNoEffect(Expr src, IrExpr e, ResolveCtx ctx)
    {
        if (src is CallExpr or NewExpr) return;
        if (!IsPure(e)) return;
        bool isComparison = e is IrBinOp { Op: BinOp.Eq or BinOp.Ne }
                            || (e is IrStaticCall sc2 && IsUnionEqCall(sc2))
                            || (e is IrUnaryOp { Op: UnOp.Not, Operand: IrStaticCall sc3 } && IsUnionEqCall(sc3));

        diag.Warn(Codes.NoEffect, ctx.File, e.Span,
            "this expression is computed as a statement but its value is never used",
            isComparison
                ? ["'==' compares two values; use '=' to assign"]
                : ["remove it, or use its result"]);
    }

    /// <summary>
    /// Validates that the expression is bool typed for use as a branch condition.
    /// </summary>
    private void CheckCondition(IrExpr c, ResolveCtx ctx, bool allowConst = false)
    {
        if (c.Type is IrResultType || c.Type.IsError) return;
        if (c.Type is not IrPrimType { CName: "bool" })
        {
            diag.Error(Codes.ConditionNotBool, ctx.File, c.Span,
                $"condition must be 'bool', got '{Describe(c.Type)}'");
            return;
        }
        WarnConstCondition(c, ctx, allowConst);
    }

    /// <summary>
    /// Warns when a condition is a compile-time constant, or compares a value against itself. Both
    /// mean the branch is decided before it is ever evaluated.
    /// </summary>
    private void WarnConstCondition(IrExpr c, ResolveCtx ctx, bool allowConst)
    {
        if (c is IrLitBool lb && !allowConst)
            diag.Warn(Codes.ConstantCondition, ctx.File, c.Span,
                $"this condition is always {(lb.Value ? "true" : "false")}",
                [lb.Value ? "the branch always runs" : "the branch is never taken"]);
        else if (c is IrBinOp { Op: BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Le or BinOp.Gt or BinOp.Ge } b
                 && SameStorage(b.Left, b.Right))
            diag.Warn(Codes.SelfComparison, ctx.File, c.Span,
                "this compares a value against itself, so the result is constant",
                ["did you mean to compare against a different value?"]);
        else if (IsSelfUnionComparison(c))
            diag.Warn(Codes.SelfComparison, ctx.File, c.Span,
                "this compares a value against itself, so the result is constant",
                ["did you mean to compare against a different value?"]);
    }

    /// <summary>
    /// True if the expression is a union equality, or its negation, over two operands naming the
    /// same storage. Matched by shape: two same-typed union arguments dispatched to exactly that
    /// union's generated equality, whose name is mangled.
    /// </summary>
    private static bool IsSelfUnionComparison(IrExpr c)
    {
        if (c is IrUnaryOp { Op: UnOp.Not } neg) c = neg.Operand;
        return c is IrStaticCall call && IsUnionEqCall(call) && SameStorage(call.Args[0], call.Args[1]);
    }

    /// <summary>
    /// Validates that the expression is a legal assignment target (variable, field, element, or
    /// deref).
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
        if (target.Type.IsError || value.Type.IsError) return;
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
    /// Finds the destination class's 'as' operator whose parameter matches the source type, or
    /// null. 'as' is always a static factory on the type converted TO, so this is the only place a
    /// match comes from and it never chains.
    /// </summary>
    private Symbol? FindAsOperator(string destCls, IrType from)
    {
        foreach (var op in sym.OperatorOverloads(destCls, "as"))
        {
            if (op.Sig!.Params.Count == 1 && SameType(ResolveType(op.Sig.Params[0].Type), from)
                && SameType(ResolveType(op.Sig.ReturnType), new IrClassRef(destCls)))
                return op;
        }
        return null;
    }

    /// <summary>
    /// Validates that an explicit cast is valid: numeric, enum-to-int, or pointer (unsafe only).
    /// Reports an error for void, String, or class casts.
    /// </summary>
    private void CheckCast(IrExpr value, IrType to, ResolveCtx ctx)
    {
        var from = value.Type;
        if (SameType(from, to))
        {
            // Casting to the type a value already has converts nothing. It is usually left
            // over from an earlier signature, and it hides a later real type change.
            
            // A cast on a literal is exempt: '0x00100000 as int' pins the width of a bit
            // pattern at the point it is written, which is deliberate documentation in
            // bit-manipulation code even when inference would have picked the same type.
            if (from is not IrVoidType && !IsLiteral(value))
                diag.Warn(Codes.RedundantCast, ctx.File, value.Span,
                    $"this cast is redundant: the value is already '{Describe(to)}'",
                    ["remove the cast"]);
            return;
        }
        if (value is IrLitNull && to is IrClassRef or IrPtrType) return;
        bool numeric = IsNum(from) && IsNum(to);
        bool enumInt = (from is IrEnumType && IsInteger(to)) || (IsInteger(from) && to is IrEnumType);
        bool pointer = (from is IrPtrType || to is IrPtrType)
                       && (from is IrPtrType or IrPrimType) && (to is IrPtrType or IrPrimType);
        if (from.IsError || to.IsError) return;
        if (from is IrVoidType || to is IrVoidType) { Reject(); return; }
        if (numeric || enumInt) return;
        if (pointer)
        {
            if (!ctx.InUnsafe)
                diag.Error(Codes.UnsafeRequired, ctx.File, value.Span, "pointer cast requires an 'unsafe' block");
            return;
        }
        Reject();
        void Reject()
        {
            var hints = new List<string>();
            if (from is IrClassRef && to is IrPrimType or IrEnumType)
                hints.Add($"'as' only converts INTO a class, never out of one to a primitive - " +
                          $"add a named conversion method on '{Describe(from)}' instead, e.g. '{Describe(to)} func ToSomething()'");
            AddInstantiationHint(hints, ctx);
            diag.Error(Codes.InvalidCast, ctx.File, value.Span,
                $"cannot cast '{Describe(from)}' to '{Describe(to)}'", hints.Count == 0 ? null : [.. hints]);
        }
    }

    // The stamped instance whose body is being resolved, or null at top level
    private string? _curInstance;

    /// <summary>
    /// Sets the stamped instance being resolved for the duration of the returned scope, or leaves
    /// it unchanged when the name is not an instantiation.
    /// </summary>
    private IDisposable TrackInstance(string name)
    {
        var previous = _curInstance;
        if (Mangler.TryGetGenericInstance(name, out _, out _)) _curInstance = name;
        return new InstanceReset(this, previous);
    }

    private sealed class InstanceReset(TypeResolver r, string? previous) : IDisposable
    {
        public void Dispose() => r._curInstance = previous;
    }

    /// <summary>
    /// Names the generic instantiation an error came from. A stamped instance lives in the
    /// template's file, so 'Map[String, int]' reports a cast error inside Map.g with nothing to say
    /// which of the author's types is at fault.
    /// </summary>
    private void AddInstantiationHint(List<string> hints, ResolveCtx ctx) =>
        AddInstantiationHint(hints, _curInstance ?? ctx.CurClass);

    /// <summary>
    /// The same hint for a named stamped instance, for declaration kinds a ResolveCtx does not
    /// carry. A union is not a class, so its fields resolve with no CurClass to read.
    /// </summary>
    private static void AddInstantiationHint(List<string> hints, string? instance)
    {
        if (string.IsNullOrEmpty(instance)) return;
        if (!Mangler.TryGetGenericInstance(instance, out _, out _)) return;
        hints.Add($"this comes from the instantiation '{Mangler.DisplayName(instance)}'; " +
                  $"the type arguments have to satisfy what the generic's body does with them");
    }

    /// <summary>
    /// Warns when a plain string literal contains '{name}' and 'name' is a variable actually in
    /// scope - the signature of a '$' dropped from an interpolated string, which otherwise fails
    /// silently by printing the braces verbatim.
    /// </summary>
    private void WarnIfLooksInterpolated(StrLitExpr sl, ResolveCtx ctx)
    {
        var raw = sl.Value.AsSpan();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '{') continue;
            int close = raw[(i + 1)..].IndexOf('}');
            if (close < 0) return;
            var inner = raw.Slice(i + 1, close);
            i += close;
            if (inner.Length == 0 || !(char.IsLetter(inner[0]) || inner[0] == '_')) continue;
            bool ident = true;
            for (int j = 1; j < inner.Length && ident; j++)
                ident = char.IsLetterOrDigit(inner[j]) || inner[j] == '_';
            if (!ident) continue;
            string name = inner.ToString();
            if (ctx.Locals.Lookup(name) == null) continue;
            diag.Warn(Codes.MissingInterpolation, ctx.File, sl.Span,
                $"this string contains '{{{name}}}' and '{name}' is a variable in scope, but the string is not interpolated",
                [$"write $\"...\" to substitute the value, or escape the brace if the text is literal"]);
            return;
        }
    }

    /// <summary>
    /// Returns true for a bare constant, the one place a same-type cast is written on purpose (to
    /// pin a literal's width where inference would otherwise decide it).
    /// </summary>
    private static bool IsLiteral(IrExpr e)
    {
        return e is IrLitInt or IrLitFloat or IrLitChar or IrLitBool or IrLitString or IrLitNull or IrEnumConst;
    }

    /// <summary>
    /// Returns true when both expressions are comparable with == or !=.
    /// </summary>
    private static bool ComparableEq(IrExpr l, IrExpr r)
    {
        var a = l.Type; var b = r.Type;
        if (a.IsError || b.IsError) return true;
        if (l is IrLitNull || r is IrLitNull)
            return (l is IrLitNull ? b : a) is IrClassRef or IrPtrType or IrFuncPtrType;
        if (IsNum(a) && IsNum(b)) return true;
        if (a.IsString && b.IsString) return true;
        if (a is IrPtrType && b is IrPtrType) return true;
        if (a is IrClassRef ca && b is IrClassRef cb) return ca.ClassName == cb.ClassName;
        if (a is IrEnumType ea && b is IrEnumType eb) return ea.Name == eb.Name;
        if (a is IrUnionType ua && b is IrUnionType ub) return ua.Name == ub.Name;
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
    /// Returns true when the statement contains a 'break' that would exit the enclosing loop. Does
    /// not descend into nested loops, whose breaks target the inner loop instead. A catch handler
    /// is part of the enclosing loop, so a 'break' inside one does exit it.
    /// </summary>
    private static bool HasLoopBreak(IrStmt s)
    {
        return s switch
        {
            IrBreak => true,
            IrBlock b => b.Stmts.Any(HasLoopBreak),
            IrUnsafeBlock u => HasLoopBreak(u.Body),
            IrDeclVar { Init: not null } d => HasHandlerBreak(d.Init),
            IrExprStmt es => HasHandlerBreak(es.Expr),
            IrIf i => HasLoopBreak(i.Then) || (i.Else != null && HasLoopBreak(i.Else)),
            IrTryCatch t => HasLoopBreak(t.Try) || HasLoopBreak(t.Catch),
            IrSwitch sw => sw.Cases.Any(c => HasLoopBreak(c.Body)) || (sw.Default != null && HasLoopBreak(sw.Default)),
            IrMatch m => m.Cases.Any(c => HasLoopBreak(c.Body)) || (m.Default != null && HasLoopBreak(m.Default)),
            _ => false
        };
    }

    /// <summary>
    /// Returns true when a root-position expression carries a catch handler containing a 'break'.
    /// Handlers only ever sit at the root of a declaration or expression statement.
    /// </summary>
    private static bool HasHandlerBreak(IrExpr e) => e is IrCatchCall cc && HasLoopBreak(cc.Handler);

    /// <summary>
    /// Rejects 'throws' return types that have no valid Result_T typedef spelling. Pointer,
    /// fixed-array, and function-pointer inner types would produce an illegal C typedef name, so
    /// they are compile errors, not link surprises.
    /// </summary>
    private void CheckThrowsReturn(IrType ret, bool isThrows, string display, ResolveCtx ctx, TextSpan span)
    {
        if (isThrows && ret is IrPtrType or IrArrayType or IrFuncPtrType)
            diag.Error(Codes.BadThrowsReturnType, ctx.File, span,
                $"'{display}': a 'throws' function cannot return '{Describe(ret)}'; " +
                "supported 'throws' return types are void, primitives, enums, unions, String, and classes");
    }

    /// <summary>
    /// Reports MissingReturn when a non-void function body does not definitely return on every
    /// path.
    /// </summary>
    private void CheckMissingReturn(IrBlock? body, IrType ret, bool isThrows, TextSpan span, string display, ResolveCtx ctx)
    {
        if (body == null || isThrows || ret is IrVoidType || ret is IrResultType) return;
        if (!ReturnsList(body.Stmts))
            diag.Error(Codes.MissingReturn, ctx.File, span, $"'{display}' must return '{Describe(ret)}' on every path");
    }

    /// <summary>
    /// Checks for a redundant trailing 'return;' in a void function, and warns about unused local
    /// variables by walking the body.
    /// </summary>
    private void CheckBodyQuality(IrBlock body, IrType ret, TextSpan span, ResolveCtx ctx,
                                  Param[]? pars = null, TextSpan parSpan = default)
    {
        if (ret is IrVoidType && body.Stmts.Count > 0 && body.Stmts[^1] is IrReturn { Value: null })
            diag.Warn(Codes.RedundantReturn, ctx.File, span, "redundant trailing 'return;'");

        var visitor = new BodyQualityVisitor();
        visitor.Run(body);

        // A native body is opaque C: it can read anything by name, so neither locals nor
        // parameters can be proven unused.
        if (visitor.Native) return;
        var seen = new HashSet<string>();
        for (int i = 0; i < visitor.Decls.Count; i++)
        {
            var (name, sp) = visitor.Decls[i];
            if (seen.Add(name) && !DeliberatelyUnused(name) && !visitor.Used.Contains(name))
                diag.Warn(Codes.UnusedVariable, ctx.File, sp, $"unused variable '{name}'");
        }
        if (pars == null) return;
        for (int i = 0; i < pars.Length; i++)
        {
            var p = pars[i];
            if (DeliberatelyUnused(p.Name)) continue;
            // only warn when the name is never mentioned anywhere in the body at all
            if (visitor.Used.Contains(p.Name) || seen.Contains(p.Name)) continue;
            diag.Warn(Codes.UnusedParameter, ctx.File, p.Span.IsNone ? parSpan : p.Span,
                $"unused parameter '{p.Name}'",
                ["remove it, or prefix the name with '_' if it is deliberately ignored"]);
        }
    }

    /// <summary>
    /// A leading underscore is the conventional marker for a binding that exists only to satisfy a
    /// shape and is not meant to be read. Such names opt out of unused warnings.
    /// </summary>
    private static bool DeliberatelyUnused(string name) => name.Length > 0 && name[0] == '_';

    /// <summary>
    /// Collects local declarations and the names a function body reads, for CheckBodyQuality's
    /// unused-local and unused-parameter warnings. Built on IrWalker so a newly added node cannot
    /// silently hide a use and turn a correct program into a false warning.
    /// </summary>
    private sealed class BodyQualityVisitor : IrWalker
    {
        public readonly List<(string Name, TextSpan Span)> Decls = [];
        public readonly HashSet<string> Used = [];
        public bool Native;

        public void Run(IrBlock body) => WalkStmt(body);

        protected override void WalkStmt(IrStmt s)
        {
            switch (s)
            {
                case IrDeclVar d: Decls.Add((d.Name, d.Span)); break;
                case IrNativeStmt: Native = true; break;
            }
            base.WalkStmt(s);
        }

        protected override void WalkExpr(IrExpr e)
        {
            if (e is IrVar v) Used.Add(v.Name);
            base.WalkExpr(e);
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
    /// Reports PrivateMember when a private operator overload is invoked from outside its declaring
    /// class. Operators follow the same private-by-default rule as every other member.
    /// </summary>
    private void CheckOperatorAccess(string owner, string op, ResolveCtx ctx, TextSpan span)
    {
        if (sym.IsPrivateMember(owner, $"operator {op}") && ctx.CurClass != owner)
            diag.Error(Codes.PrivateMember, ctx.File, span,
                $"operator '{op}' on '{Mangler.DisplayName(owner)}' is private and cannot be used from outside '{Mangler.DisplayName(owner)}'");
    }

    /// <summary>
    /// Reports ThrowsOutsideTry when a throwing call appears outside a try block or throws
    /// function. A call carrying its own `catch` handler is the third handled form, alongside those
    /// two.
    /// </summary>
    private void CheckThrowsHandled(ResolveCtx ctx, TextSpan span)
    {
        if (!ctx.InTry && !ctx.InThrowsFunc && !ctx.CatchWrapped)
            diag.Error(Codes.ThrowsOutsideTry, ctx.File, span,
                "throwing call must be inside a 'try' block or a 'throws' function",
                ["or handle it in place: 'let T x = f() catch { assign <fallback>; };'"]);
    }

    /// <summary>
    /// Returns true when the statement list leaves its handler on every path - either by supplying
    /// a value with `assign`, or by transferring control out entirely.
    /// </summary>
    private static bool AssignsOrExitsList(IReadOnlyList<IrStmt> stmts)
    {
        return stmts.Any(AssignsOrExits);
    }

    /// <summary>
    /// Returns true when a statement definitely ends its enclosing `catch` handler.
    /// </summary>
    private static bool AssignsOrExits(IrStmt s)
    {
        return s switch
        {
            IrAssignValue => true,
            IrBreak or IrContinue => true,
            IrBlock b => AssignsOrExitsList(b.Stmts),
            IrUnsafeBlock u => AssignsOrExitsList(u.Body.Stmts),
            IrIf i => i.Else != null && AssignsOrExits(i.Then) && AssignsOrExits(i.Else),
            IrTryCatch t => AssignsOrExits(t.Try) && AssignsOrExits(t.Catch),
            IrSwitch sw => sw.Default != null && sw.Cases.All(c => AssignsOrExits(c.Body))
                           && AssignsOrExits(sw.Default),
            IrMatch ms => ms.Cases.All(c => AssignsOrExits(c.Body))
                          && (ms.Default == null || AssignsOrExits(ms.Default)),
            _ => DefinitelyReturns(s)
        };
    }

    /// <summary>
    /// Returns true when an `assign` appears anywhere inside the statement. Used to reject one in a
    /// handler that has no declaration to assign to.
    /// </summary>
    private static bool ContainsAssignValue(IrStmt s)
    {
        return s switch
        {
            IrAssignValue => true,
            IrBlock b => b.Stmts.Any(ContainsAssignValue),
            IrUnsafeBlock u => ContainsAssignValue(u.Body),
            IrIf i => ContainsAssignValue(i.Then) || (i.Else != null && ContainsAssignValue(i.Else)),
            IrWhile w => ContainsAssignValue(w.Body),
            IrFor f => ContainsAssignValue(f.Body),
            IrForIn fi => ContainsAssignValue(fi.Body),
            IrTryCatch t => ContainsAssignValue(t.Try) || ContainsAssignValue(t.Catch),
            IrSwitch sw => sw.Cases.Any(c => ContainsAssignValue(c.Body))
                           || (sw.Default != null && ContainsAssignValue(sw.Default)),
            IrMatch ms => ms.Cases.Any(c => ContainsAssignValue(c.Body))
                          || (ms.Default != null && ContainsAssignValue(ms.Default)),
            _ => false
        };
    }

    /// <summary>
    /// Whole-body backstop for throws placement. ForbidNestedThrows is opt-in, so a position nobody
    /// thought of lets an IrThrowsCall reach the emitter and die; this is opt-out, reporting
    /// anything outside the two positions the language permits.
    /// </summary>
    private void CheckThrowsPlacement(IrBlock body, ResolveCtx ctx)
    {
        new ThrowsPlacementCheck(diag, ctx.File).Check(body);
    }

    /// <summary>
    /// The walker behind CheckThrowsPlacement. WalkStmt routes the one legal root slot through
    /// WalkRoot, which permits a throwing call and keeps checking below it; every other path lands
    /// in WalkExpr, where a throwing call is by definition nested.
    /// </summary>
    private sealed class ThrowsPlacementCheck(DiagnosticBag diag, string file) : IrWalker
    {
        /// <summary>
        /// Walks a resolved function body, reporting every misplaced throwing call.
        /// </summary>
        public void Check(IrBlock body) => WalkStmt(body);

        protected override void WalkStmt(IrStmt s)
        {
            switch (s)
            {
                case IrDeclVar { Init: not null } d: WalkRoot(d.Init); break;
                case IrExprStmt e: WalkRoot(e.Expr); break;
                case IrAssign { Op: AssignOp.Assign } a: WalkExpr(a.Target); WalkRoot(a.Value); break;
                default: base.WalkStmt(s); break;
            }
        }

        /// <summary>
        /// Visits an expression in a position where a throwing call is legal, then keeps walking
        /// its children, where one no longer is.
        /// </summary>
        private void WalkRoot(IrExpr e)
        {
            switch (e)
            {
                case IrCatchCall cc: WalkRoot(cc.Call); WalkStmt(cc.Handler); break;
                case IrThrowsCall tc: foreach (var a in tc.Args) WalkExpr(a); break;
                case IrThrowsInstanceCall ti:
                    WalkExpr(ti.Recv);
                    foreach (var a in ti.Args) WalkExpr(a);
                    break;
                default: WalkExpr(e); break;
            }
        }

        protected override void WalkExpr(IrExpr e)
        {
            switch (e)
            {
                case IrThrowsCall or IrThrowsInstanceCall:
                    Report(e.Span, "throwing call cannot appear inside a larger expression", null);
                    break;
                case IrCatchCall cc:
                    Report(e.Span, CatchNotAtRoot, CatchNotAtRootHints);
                    WalkStmt(cc.Handler);
                    return;
            }
            base.WalkExpr(e);
        }

        /// <summary>
        /// Reports the error unless the per-site check already produced one at this span, which
        /// would otherwise print the same complaint twice.
        /// </summary>
        private void Report(TextSpan span, string message, string[]? hints)
        {
            foreach (var d in diag.All)
                if (d.Code == Codes.ThrowsOutsideTry && d.Loc.Span == span) return;
            diag.Error(Codes.ThrowsOutsideTry, file, span, message, hints);
        }
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
            case IrCatchCall cc:
                if (!allowRoot)
                    ReportPlacementOnce(cc.Span, CatchNotAtRoot, CatchNotAtRootHints, ctx);
                else
                    ForbidNestedThrows(cc.Call, ctx, allowRoot: true);
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

    // A catch handler supplies the value for the storage it is attached to, so a declaration or a
    // plain assignment are the only places one can sit. Spelled once: two sites report it.
    private const string CatchNotAtRoot =
        "a 'catch' handler must cover a whole declaration or assignment, not a call nested inside a larger expression";

    private static readonly string[] CatchNotAtRootHints =
    [
        "a handler supplies the value for one target, so it can only sit at one: " +
            "'let T x = f() catch { ... };' or 'x = f() catch { ... };'",
        "bind the call to its own local first, then use that local here",
    ];

    /// <summary>
    /// Checks a value in a position that may hold a throwing call: a declaration initializer or an
    /// assignment right-hand side. Both name storage the result lands in, which is what a handler's
    /// 'assign' needs and what makes propagation well-defined.
    /// </summary>
    private IrExpr CheckRootThrowsValue(IrExpr value, IrType targetType, string what,
                                        ResolveCtx ctx, TextSpan span)
    {
        ForbidNestedThrows(value, ctx, allowRoot: true);

        if (value is IrCatchCall cc && !AssignsOrExits(cc.Handler))
            diag.Error(Codes.CatchHandlerNoAssign, ctx.File, cc.Handler.Span,
                $"this 'catch' handler can finish without supplying a value for {what}",
                ["end every path with 'assign <value>;'",
                 "or leave the handler through 'return', 'throw', 'break', or 'continue'"]);

        if (value.Type is IrResultType rt)
        {
            if (!Assignable(new IrVar("_v", rt.Inner), targetType))
                diag.Error(Codes.TypeMismatch, ctx.File, span,
                    $"this throwing call produces '{Describe(rt.Inner)}', " +
                    $"which cannot be assigned to {what} of type '{Describe(targetType)}'");
            return value;
        }

        value = Coerce(value, targetType, ctx);
        CheckAssign(value, targetType, what, ctx, Codes.TypeMismatch);
        return value;
    }

    /// <summary>
    /// Reports a misplaced throwing call unless something already complained about the same span,
    /// so a form-specific message and this general one never both fire.
    /// </summary>
    private void ReportPlacementOnce(TextSpan span, string message, string[]? hints, ResolveCtx ctx)
    {
        foreach (var d in diag.All)
            if (d.Code == Codes.ThrowsOutsideTry && d.Loc.Span == span) return;
        diag.Error(Codes.ThrowsOutsideTry, ctx.File, span, message, hints);
    }

    /// <summary>
    /// Rejects a throwing call in an assignment form that has nowhere to put the result: a compound
    /// assignment, whose target is read as well as written, and an index setter, which is a call.
    /// </summary>
    private void ForbidThrowsInAssignForm(IrExpr value, string form, ResolveCtx ctx)
    {
        if (value is not (IrCatchCall or IrThrowsCall or IrThrowsInstanceCall)) return;
        // Reported at the value's own span, which is where the per-body backstop would report it
        // too - that is what stops the two from both firing.
        diag.Error(Codes.ThrowsOutsideTry, ctx.File, value.Span,
            $"a throwing call cannot be the value of {form}",
            ["bind it first: 'let T tmp = f() catch { assign <fallback>; };', then use 'tmp' here"]);
    }

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

            // Calls are assumed impure, but this one is compiler-generated and only reads its
            // two by-value arguments - so 'u == v;' written where 'u = v;' was meant is still
            // reported as a statement with no effect, exactly as 'i == j;' is.
            IrStaticCall sc when IsUnionEqCall(sc) => sc.Args.All(IsPure),

            _ => false
        };
    }

    /// <summary>
    /// Warns when an 'unsafe' block builds a managed value
    /// </summary>
    private void WarnUnsafeManagedTemporary(IrBlock body, ResolveCtx ctx)
    {
        var finder = new UnsafeAllocFinder(IsManagedRef,
            sym.IntrinsicOrNull(Roles.Retain), sym.IntrinsicOrNull(Roles.Release));
        finder.Run(body);
        if (finder.HandManaged || finder.Found is not { } site) return;

        diag.Warn(Codes.UnsafeAllocatingTemporary, ctx.File, site.Span,
            $"this builds a '{Describe(site.Type)}' inside an 'unsafe' block, where it is never released",
            ["'unsafe' turns off reference counting for the whole block, including values like this " +
             "one that it did not need to be turned off for",
             "move it out of the block, or bind it outside and use the binding here"]);
    }

    /// <summary>
    /// Finds the first expression in an unsafe block that allocates a managed value: an
    /// interpolation, a 'new', or a call handing one back. Reading an existing managed binding is
    /// fine - nothing was allocated, so nothing leaks.
    /// </summary>
    private sealed class UnsafeAllocFinder(Func<IrType, bool> isManaged, string? retain, string? release)
        : IrWalker
    {
        public IrExpr? Found { get; private set; }

        /// <summary>
        /// True once the block names retain or release, meaning the author is counting by hand.
        /// </summary>
        public bool HandManaged { get; private set; }

        public void Run(IrBlock body) => WalkStmt(body);

        private IrExpr? _owned;

        protected override void WalkStmt(IrStmt s)
        {
            if (s is IrUnsafeBlock) return;
            _owned = s switch
            {
                IrDeclVar { Init: not null } d => d.Init,
                IrReturn { Value: not null } r => r.Value,
                _ => null
            };
            base.WalkStmt(s);
        }

        protected override void WalkExpr(IrExpr e)
        {
            if (e is IrStaticCall sc && (sc.CName == retain || sc.CName == release)) HandManaged = true;
            if (Found == null && !ReferenceEquals(e, _owned) && Allocates(e) && isManaged(e.Type)) Found = e;
            base.WalkExpr(e);
        }

        private static bool Allocates(IrExpr e) =>
            e is IrInterp or IrNew or IrNewInit or IrStaticCall or IrInstanceCall
                or IrThrowsCall or IrThrowsInstanceCall or IrIndirectCall;
    }

    /// <summary>
    /// Warns that a fixed array of a managed element type never releases what it holds
    /// </summary>
    private void WarnManagedFixedArray(IrType type, string what, ResolveCtx ctx, TextSpan span)
    {
        if (type is not IrArrayType at || !IsManagedRef(at.Elem)) return;
        diag.Warn(Codes.ManagedFixedArray, ctx.File, span,
            $"{what} is a fixed array of '{Describe(at.Elem)}', whose elements are never released",
            ["a fixed array is raw storage with no destructor, so whatever it still holds when it " +
             "goes out of scope is leaked; stores into it are counted correctly, so nothing dangles",
             $"use 'List[{Describe(at.Elem)}]' for owned elements, or clear the slots by hand before it dies"]);
    }

    // The four relational operators, which - unlike '==' and '!=' - never derive from one another.
    private static readonly string[] Relational = ["<", ">", "<=", ">="];

    /// <summary>
    /// Warns when a class overloads some relational operators but not their mirrors
    /// </summary>
    private void WarnPartialRelationalSet(ClassDecl cd, ResolveCtx ctx)
    {
        var declared = new HashSet<string>();
        foreach (var m in cd.Members)
            if (m is OperatorDecl { Params.Length: 1 } od && Relational.Contains(od.Op)) declared.Add(od.Op);
        if (declared.Count == 0) return;

        var missing = new List<string>();
        foreach (var (a, b) in (ReadOnlySpan<(string, string)>)[("<", ">"), ("<=", ">=")])
            if (declared.Contains(a) != declared.Contains(b))
                missing.Add(declared.Contains(a) ? b : a);
        if (missing.Count == 0) return;

        string shown = Mangler.DisplayName(cd.Name);
        diag.Warn(Codes.PartialOperatorSet, ctx.File, cd.Span,
            $"'{shown}' overloads {string.Join(" and ", declared.OrderBy(o => o).Select(o => $"'{o}'"))} " +
            $"but not {string.Join(" or ", missing.Select(o => $"'{o}'"))}",
            ["relational operators do not derive from one another the way '!=' derives from '==', " +
             $"so '{missing[0]}' on two '{shown}' values is a type error at every call site",
             $"declare the mirror, e.g. 'public operator bool func {missing[0]}({shown} other) {{ ... }}'"]);
    }

    /// <summary>
    /// Explains a relational operator rejected on a class that overloads some of the family but not
    /// this one, which otherwise reads as "not numeric" with no mention of the operators it has.
    /// </summary>
    private string[]? MissingRelationalHint(string? lhsClass, string op)
    {
        if (lhsClass == null) return null;
        var have = Relational.Where(o => sym.LookupOperator(lhsClass, o, 1) != null).ToList();
        if (have.Count == 0) return null;
        string shown = Mangler.DisplayName(lhsClass);
        return [$"'{shown}' overloads {string.Join(" and ", have.Select(o => $"'{o}'"))}, but not '{op}' - " +
                "relational operators are each declared separately, none derives from another",
                $"add 'public operator bool func {op}({shown} other) {{ ... }}'"];
    }

    /// <summary>
    /// Returns true if the call is a union's generated structural equality: two arguments of the
    /// same union type, dispatched to exactly that union's mangled equality name.
    /// </summary>
    private static bool IsUnionEqCall(IrStaticCall call)
    {
        return call.Args.Count == 2
               && call.Args[0].Type is IrUnionType ut
               && call.Args[1].Type is IrUnionType ut2
               && ut.Name == ut2.Name
               && call.CName == Mangler.UnionEq(ut.Name);
    }

    /// <summary>
    /// Structural equality for the pure expression forms that can name the same storage twice:
    /// locals, self, fields, constant-indexed elements, and derefs.
    /// </summary>
    private static bool SameStorage(IrExpr a, IrExpr b)
    {
        return (a, b) switch
        {
            (IrVar x, IrVar y) => x.Name == y.Name,
            (IrSelfExpr x, IrSelfExpr y) => x.ClassName == y.ClassName,
            (IrFieldLoad x, IrFieldLoad y) => x.Field == y.Field && SameStorage(x.Obj, y.Obj),
            (IrDeref x, IrDeref y) => SameStorage(x.Ptr, y.Ptr),
            (IrUnionField x, IrUnionField y) => x.Field == y.Field && x.VariantIndex == y.VariantIndex
                                                && SameStorage(x.Union, y.Union),
            
            // Only a literal index is safe to compare: a[i()] == a[i()] need not match.
            (IrIndex x, IrIndex y) => SameStorage(x.Obj, y.Obj)
                                      && x.Idx is IrLitInt xi && y.Idx is IrLitInt yi && xi.Value == yi.Value,
            _ => false
        };
    }

    /// <summary>
    /// Returns the expression unchanged when it is pure, or hoists it into a fresh declared
    /// temporary and returns a reference to that temp.
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
    /// Coerces an expression to the expected type, currently narrowing fixed-array literal element
    /// types when the destination declares a specific element type.
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
        diag.Error(Codes.MissingIntrinsic, ctx.File, span,
            $"nothing in the build binds @intrinsic({role}), which this expression needs",
            ["the binding lives in libgata; import the module that provides it, " +
             "or update libgata if this compiler is newer than it"]);
        return $"appa_MISSING_{role}";
    }

    /// <summary>
    /// Coerces an expression to string by dispatching to the appropriate stringify intrinsic or the
    /// class's ToString method. Reports a diagnostic when no conversion is available.
    /// </summary>
    private IrExpr EnsureString(IrExpr e, ResolveCtx ctx)
    {
        if (e.Type.IsString) return e;
        if (e.Type.IsFloat)
            return new IrStaticCall(Intrinsic(Roles.StringifyFloat, ctx, e.Span), IrType.String, [e]) { Span = e.Span };
        if (e.Type.IsChar)
            return new IrStaticCall(Intrinsic(Roles.StringifyChar, ctx, e.Span), IrType.String, [e]) { Span = e.Span };
        if (e.Type.IsUnsigned)
            return new IrStaticCall(Intrinsic(Roles.StringifyUint, ctx, e.Span), IrType.String, [e]) { Span = e.Span };
        if (e.Type.IsNumeric)
            return new IrStaticCall(Intrinsic(Roles.StringifyInt, ctx, e.Span), IrType.String, [e]) { Span = e.Span };
        var cls = ClassNameOf(e.Type);
        if (cls != null && sym.LookupMethod(cls, "ToString") is { } ts)
            return new IrInstanceCall(e, ts.CName, IrType.String, []) { Span = e.Span };
        if (e.Type.IsError) return new IrLitString("\"\"") { Span = e.Span };
        diag.Error(Codes.TypeMismatch, ctx.File, e.Span,
            cls != null
                ? $"'{Mangler.DisplayName(cls)}' has no 'String func ToString()' to convert it to a String"
                : $"'{Describe(e.Type)}' cannot be converted to a String");
        return new IrLitString("\"\"") { Span = e.Span };
    }

    /// <summary>
    /// Extracts the class name from a class-reference type, or null for non-class types. Follows
    /// one level of pointer indirection for pointer-to-class patterns.
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

        // The one scope with no matching braces in the emitted C: parameters and top-level
        // locals share the function's compound statement, so a local shadowing a parameter is
        // a redeclaration there rather than a new binding.
        private readonly bool _isParams;

        /// <summary>
        /// Constructs a root scope with no parent.
        /// </summary>
        public ScopeStack() { _parent = null; _vars = []; _refs = []; }

        private ScopeStack(ScopeStack parent, bool isParams)
        {
            _parent = parent; _vars = []; _refs = []; _isParams = isParams;
        }

        /// <summary>
        /// Creates a child scope nested inside this one. Set isParams for the scope holding a
        /// function's parameters.
        /// </summary>
        public ScopeStack Push(bool isParams = false)
        {
            return new(this, isParams);
        }

        /// <summary>
        /// Returns true when declaring the name here would collide with a parameter of the
        /// enclosing function rather than shadow it, because both land in the same C scope.
        /// </summary>
        public bool CollidesWithParam(string name)
        {
            return _parent is { _isParams: true } p && p._vars.ContainsKey(name);
        }

        /// <summary>
        /// Declares a variable with the given name and type in the current scope. When isRef is
        /// true, the variable is a ref parameter and emits pointer indirection.
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
        /// Returns true when the name is declared in an enclosing scope but not in this one, i.e.
        /// declaring it here would shadow the outer binding.
        /// </summary>
        public bool ShadowsOuter(string name)
        {
            if (_vars.ContainsKey(name)) return false;
            for (var s = _parent; s != null; s = s._parent)
                if (s._vars.ContainsKey(name)) return true;
            return false;
        }

        /// <summary>
        /// Searches this scope and all parent scopes for the named variable. Returns its type, or
        /// null when not found.
        /// </summary>
        public IrType? Lookup(string name)
        {
            for (var s = this; s != null; s = s._parent)
                if (s._vars.TryGetValue(name, out var t)) return t;
            return null;
        }

        /// <summary>
        /// Returns true when the named variable resolves to a ref parameter in this or an enclosing
        /// scope.
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
        Realm Realm,
        string CurClass,
        string? CurFunc,
        bool InStatic,
        bool InUnsafe,
        bool InTry,
        bool InThrowsFunc,
        string CatchLabel,
        int LoopDepth,
        ScopeStack Locals,
        bool InDefer = false,
        IrType? AssignType = null,
        // The type the enclosing construct wants this expression to have, when there is one:
        // a let with a declared type, or a return. Only consulted where a value is otherwise
        // under-determined - naming a generic union's variant without its type arguments.
        IrType? Expected = null,
        bool CatchWrapped = false,
        IrType? RetType = null)
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
        /// Returns a context with the realm updated.
        /// </summary>
        public ResolveCtx WithRealm(Realm r)
        {
            return this with { Realm = r };
        }

        /// <summary>
        /// Returns a context that marks entry into a `catch` handler attached to a call.
        /// </summary>
        public ResolveCtx WithCatchHandler(IrType assignType)
        {
            return this with { AssignType = assignType, CatchWrapped = false };
        }

        /// <summary>
        /// Returns a context marking the call about to be resolved as carrying its own `catch`
        /// handler, so CheckThrowsHandled accepts it. Cleared again for the call's arguments, which
        /// are ordinary sub-expressions and get no such dispensation.
        /// </summary>
        public ResolveCtx WithCatchWrapped()
        {
            return this with { CatchWrapped = true };
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
        public ResolveCtx PushScope(bool isParams = false)
        {
            return this with { Locals = Locals.Push(isParams) };
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
    /// Resolves all programs in the compilation unit and returns the fully typed IrModule. Generic
    /// template instances discovered during resolution are stamped after the main pass.
    /// </summary>
    public IrModule Resolve(List<(Program prog, string file)> programs)
    {
        var module = new IrModule([], [], [], [], [], _arrays, [], sym, _funcPtrTypes, []);
        foreach (var (prog, file) in programs)
            CollectFuncTemplates(prog.Items, Realm.None, file);
        foreach (var (prog, file) in programs)
        {
            _fileScope = visible.GetValueOrDefault(file, [file]);
            var ctx = new ResolveCtx(file, Realm.None, "", null, false, false, false, false, "", 0, new ScopeStack());
            foreach (var item in prog.Items)
            {
                _scope = ScopeFor(item, file);
                ResolveTop(item, ctx, module);
            }
            _scope = _fileScope;
        }
        DrainGenericInstances(module);
        return module;
    }

    /// <summary>
    /// Returns the module scope a top-level item resolves under - the enclosing file's, except for
    /// a stamped generic instance, which the Monomorphizer splices into the template's file though
    /// its type arguments were named at the use site.
    /// </summary>
    private HashSet<string> ScopeFor(TopLevel item, string file)
    {
        string? name = item switch
        {
            ClassDecl cd => cd.Name,
            UnionDecl ud => ud.Name,
            _ => null,
        };
        if (name == null) return _fileScope;
        if (!genericRequestFile.TryGetValue(name, out var requester)) return _fileScope;
        if (requester == file) return _fileScope;
        if (!visible.TryGetValue(requester, out var requesterScope)) return _fileScope;

        var widened = new HashSet<string>(_fileScope, StringComparer.OrdinalIgnoreCase);
        widened.UnionWith(requesterScope);
        return widened;
    }

    /// <summary>
    /// Scans top-level items for generic function/method templates and registers them for on-demand
    /// instantiation.
    /// </summary>
    private void CollectFuncTemplates(TopLevel[] items, Realm realm, string file)
    {
        foreach (var item in items)
            switch (item)
            {
                case FuncDecl fd when fd.GenericParams.Length > 0:
                    if (!_funcTemplates.TryGetValue(fd.Name, out var bucket))
                        _funcTemplates[fd.Name] = bucket = [];
                    bucket.Add((fd, file, realm, (fd.Modifiers & Modifiers.Private) != 0));
                    break;
                case ContextDecl cd:
                    CollectFuncTemplates(cd.Items, cd.Kind, file);
                    break;
                case ProcessDecl pd:
                    CollectFuncTemplates(pd.Items, realm, file);
                    break;
                case ClassDecl cls:
                    foreach (var m in cls.Members)
                        if (m is MethodDecl md && md.GenericParams.Length > 0)
                            _methodTemplates[new MemberKey(cls.Name, md.Name)] = (md, file, realm);
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
                if (ctx.Realm != Realm.None)
                    diag.Error(Codes.MisplacedEnvironment, ctx.File, ed.Span,
                        "an '@environment' declaration is only valid at the top level of a file, not inside a context block");
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
                    ? (NativeSection.Types, VisOf(ctx.Realm))
                    : pre.Target switch
                    {
                        "boot" => (NativeSection.Boot, Visibility.Kernel),
                        "kernel" => (NativeSection.Preamble, Visibility.Kernel),
                        "user" => (NativeSection.Preamble, Visibility.User),
                        _ => Unknown(pre.Target, ctx, nb.Span),
                    };
                module.NativeBlocks.Add(new IrNativeBlock(nb.Body.C, vis, section));
                break;
            }
            case ClassDecl cd:
                module.Classes.Add(ResolveClass(cd, ctx));
                break;
            case ContextDecl cdecl:
                var inner = ctx.WithRealm(cdecl.Kind);
                foreach (var i in cdecl.Items) ResolveTop(i, inner, module);
                break;
            case FuncDecl fd:
                if (fd.GenericParams.Length > 0) break;
                module.FreeFunctions.Add(ResolveFreeFunc(fd, ctx));
                break;
            case NativeTypeDecl nd:
                module.NativeTypes.Add(new IrNativeType(nd.Name, Mangler.Class(nd.Name), nd.CBody, VisOf(ctx.Realm)));
                break;
            case EnumDecl ed:
                module.Enums.Add(ResolveEnum(ed, ctx));
                break;
            case UnionDecl ud:
                module.Unions.Add(ResolveUnion(ud, ctx));
                break;
            case ProcessDecl pd:
                module.Processes.Add(ResolveProcess(pd, ctx, module));
                break;
        }
    }

    // Names already reported as scope-invisible, per file
    private readonly HashSet<(string File, string Name)> _notVisible = [];

    // Names already reported as the wrong kind, per file
    private readonly HashSet<(string File, string Name)> _wrongKind = [];

    /// <summary>
    /// Reports a name that a scope does declare, but as something else - the function 'Kernel.X'
    /// where a type was wanted. A scope holds one meaning per name, so the outer declaration this
    /// one displaced is unreachable, and "unknown type" would be the one answer that is untrue.
    /// </summary>
    private bool ReportWrongKind(string code, string wanted, string qualified, string file, TextSpan span)
    {
        if (Mangler.ScopedKind(qualified) is not { } have || have == wanted) return false;
        if (!_wrongKind.Add((file, qualified))) return true;

        string shown = Mangler.DisplayName(qualified);
        diag.Error(code, file, span, $"'{shown}' is {have} here, not {wanted}",
            [$"the declaration of '{shown}' takes over the name in its scope, so " +
             $"{wanted} of that name outside it cannot be reached from here"]);
        return true;
    }

    /// <summary>
    /// Reports a name that does exist, but only inside a scope this code is not in. Returns false
    /// when nothing scoped declares it, leaving the caller's own "no such name" error to fire.
    /// </summary>
    private bool ReportNotVisible(string kind, string bare, string file, TextSpan span)
    {
        var paths = Mangler.ScopedCandidates(bare);
        if (paths.Count == 0) return false;
        if (!_notVisible.Add((file, bare))) return true;

        var owners = paths.Select(p => p[..p.LastIndexOf('.')]).ToList();
        diag.Error(Codes.ScopedNameNotVisible, file, span,
            $"{kind} '{bare}' is declared inside {string.Join(" and ", owners.Select(o => $"'{o}'"))} " +
            "and is not visible here",
            [owners.Count == 1
                ? $"reference it from inside '{owners[0]}', or move it out to the enclosing realm"
                : "reference it from inside the declaring scope, or move it out to the enclosing realm"]);
        return true;
    }

    /// <summary>
    /// A stand in for an expression whose real type could not be determined, after the reason was
    /// already reported. Carries the poison type, so every enclosing rule accepts it in silence.
    /// </summary>
    private static IrExpr Poison(TextSpan span) => new IrDefault(IrType.Error) { Span = span };

    /// <summary>
    /// A type as the user would have written it: 'Box[int]', not the mangled 'Box_int'. Structural,
    /// so it works for an instantiation that was never stamped and so has no registered display.
    /// </summary>
    private static string Written(NamedSpec nm) =>
        nm.Args.Length == 0
            ? Mangler.DisplayName(nm.Name)
            : $"{Mangler.DisplayName(nm.Name)}[{string.Join(", ", nm.Args.Select(Written))}]";

    /// <summary>
    /// Maps a realm to its IR visibility.
    /// </summary>
    private static Visibility VisOf(Realm r)
    {
        return r switch
        {
            Realm.Kernel => Visibility.Kernel,
            Realm.User => Visibility.User,
            _ => Visibility.Shared
        };
    }

    #endregion

    #region Type conversion
    /// <summary>
    /// Converts a type spec to its IR type. Null means void (an omitted type).
    /// </summary>
    public IrType ResolveType(TypeSpec? t)
    {
        switch (t)
        {
            case null:
                return IrType.Void;
            case FuncSpec f:
            {
                var ps = new List<IrType>(f.Params.Length);
                foreach (var p in f.Params) ps.Add(ResolveType(p));
                return FnPtr(ResolveType(f.Ret), ps);
            }
            case ArraySpec a:
                // CheckType reports an invalid size; resolve defensively to size 0.
                return Arr(ResolveType(a.Elem), TryParseIntLit(a.SizeText, out var v, out _, out _) ? (int)v : 0);
            case PtrSpec p2:
                return new IrPtrType(ResolveType(p2.Inner));
            case NamedSpec { Name: NamedSpec.Poison }:
                return IrType.Error;
            case NamedSpec nm:
            {
                string name = nm.Mangled;
                if (name == "void") return IrType.Void;
                if (BuiltinTypes.All.Contains(name))
                    return sym.ResolveBuiltinType(name)
                        ?? (name == BuiltinTypes.String ? IrType.String
                          : name == BuiltinTypes.StringBuilder ? new IrClassRef(name)
                          : new IrPtrType(IrType.Void));
                if (PrimTypes.IsPrim(name)) return new IrPrimType(name);
                if (sym.IsEnum(name)) return new IrEnumType(name);
                if (sym.IsUnion(name)) return new IrUnionType(name);
                return new IrClassRef(name);
            }
            default:
                throw new System.Diagnostics.UnreachableException($"[TypeResolver] unhandled TypeSpec: {t.GetType().Name}");
        }
    }

        #endregion

    #region Declaration resolvers

    /// <summary>
    /// Resolves a class declaration, including all fields, methods, and operator overloads.
    /// </summary>
    private IrClass ResolveClass(ClassDecl cd, ResolveCtx ctx)
    {
        bool lib = ctx.Realm == Realm.None;
        var vis = VisOf(ctx.Realm);
        var classCtx = ctx.WithClass(cd.Name);

        // A stamped generic instance is a machine-generated copy, so one bad type argument is
        // reported once rather than once per line of the template that happens to touch it.
        using var instanceScope = diag.InstanceScope(
            Mangler.TryGetGenericInstance(cd.Name, out _, out _) ? cd.Name : null);
        using var instanceName = TrackInstance(cd.Name);

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
                    rawFields.Add(new RawFieldBlock(fb.Body.C));
                    break;
                case FieldDecl fd:
                    TypeSpec? fspec = fd.Type ?? InferFieldTypeSpec(fd.Init);
                    if (fspec == null)
                    {
                        diag.Error(Codes.CannotInfer, ctx.File, fd.Span,
                            $"cannot infer a type for field '{fd.Name}'; only literal initializers " +
                            $"can infer a field's type - give it an explicit type");
                        fspec = new NamedSpec("int", fd.Span);
                    }
                    CheckType(fspec, classCtx, fd.Span);
                    var ft = ResolveType(fspec);
                    IrExpr? init = null;
                    if (fd.Init != null)
                    {
                        init = Coerce(ResolveExpr(fd.Init, classCtx.WithStatic(false)), ft, classCtx);
                        CheckAssign(init, ft, $"field '{fd.Name}'", classCtx, Codes.TypeMismatch);
                        ForbidNestedThrows(init, classCtx, allowRoot: false);
                        fieldInits[fd.Name] = init;
                    }
                    WarnManagedFixedArray(ft, $"field '{fd.Name}'", classCtx, fd.Span);
                    fields.Add(new IrField(fd.Name, ft, init));
                    break;
                case MethodDecl md when md.GenericParams.Length > 0:
                    break; // stamped on demand per call site
                case MethodDecl md:
                    methods.Add(ResolveMethod(cd.Name, md, classCtx, lib, vis, cd.IsModule));
                    break;
                case OperatorDecl od:
                    operators.Add(ResolveOperator(cd.Name, od, classCtx, lib, vis));
                    break;
            }
        }

        WarnPartialRelationalSet(cd, ctx);

        return new IrClass(
            cd.Name, Mangler.Class(cd.Name), lib, vis,
            rawFields, fields, methods, operators,
            hasInit.Contains(cd.Name), fieldInits, cd.IsModule,
            Keep: cd.Annotations.Any(a => a is KeepAnnotation));
    }

    /// <summary>
    /// Resolves a method declaration, type-checking its signature and body, and declaring
    /// parameters and optionally 'self' in the method's scope.
    /// </summary>
    private IrFunction ResolveMethod(string cls, MethodDecl md, ResolveCtx ctx, bool lib, Visibility vis, bool isModule)
    {
        bool isStatic = (md.Modifiers & Modifiers.Static) != 0 || isModule;
        if (!md.Throws) CheckType(md.ReturnType, ctx, md.Span, allowVoid: true);
        foreach (var p in md.Params) CheckType(p.Type, ctx, p.Span);
        CheckParams(md.Params, ctx);
        var ret = md.Throws && md.ReturnType is null ? IrType.Int : ResolveType(md.ReturnType);
        CheckThrowsReturn(ret, md.Throws, $"{Mangler.DisplayName(cls)}.{md.Name}", ctx, md.Span);
        
        var pars = new List<IrParam>(md.Params.Length);
        for (int i = 0; i < md.Params.Length; i++)
        {
            var p = md.Params[i];
            pars.Add(new IrParam(p.Name, ResolveType(p.Type), p.IsRef));
        }

        string cname = Mangler.Method(cls, md.Name, md.Params, sym.IsOverloadedMethod(cls, md.Name));
        var mctx = ctx.WithClass(cls).WithFunc(md.Name).WithStatic(isStatic)
            .WithThrowsFunc(md.Throws).PushScope(isParams: true);
        if (!isStatic) mctx.Locals.Declare("self", new IrClassRef(cls));
        foreach (var p in md.Params) mctx.Locals.Declare(p.Name, ResolveType(p.Type), p.IsRef);
        var (body, native) = ResolveBodyOrNative(md.Body, mctx, ret);
        CheckMissingReturn(body, ret, md.Throws, md.Span, $"{Mangler.DisplayName(cls)}.{md.Name}", ctx);
        if (body != null) { CheckBodyQuality(body, ret, md.Span, ctx, md.Params, md.Span); CheckThrowsPlacement(body, mctx); }
        return new IrFunction(md.Name, cname, ret, pars, isStatic, md.IsEntry, md.Throws, lib, vis,
            cls, body, native, [..md.Annotations]);
    }

    /// <summary>
    /// Resolves an operator declaration, type-checking its signature and body and registering
    /// 'self' and all parameters in the operator's scope.
    /// </summary>
    private IrOperator ResolveOperator(string cls, OperatorDecl od, ResolveCtx ctx, bool lib, Visibility vis)
    {
        bool isAs = od.Op == "as";

        // Arity, comparison/mutator classification, and default return all come from the
        // shared OperatorRules table, the same source SymbolCollector keys declarations by

        int want = OperatorRules.RequiredArity(od.Op, od.Params.Length);
        if (od.Params.Length != want)
            diag.Error(Codes.WrongArgCount, ctx.File, od.Span,
                $"operator '{od.Op}' must take exactly {want} parameter(s), got {od.Params.Length}");
        CheckType(od.ReturnType, ctx, od.Span, allowVoid: true);
        foreach (var p in od.Params) CheckType(p.Type, ctx, p.Span);
        CheckParams(od.Params, ctx);
        bool isCmp = OperatorRules.IsComparison(od.Op);
        bool isMutator = OperatorRules.IsMutator(od.Op);
        TypeSpec retSpec = od.ReturnType ?? new NamedSpec(OperatorRules.DefaultReturn(od.Op, cls), od.Span);
        var ret = ResolveType(retSpec);
        if (isAs && od.ReturnType != null && !SameType(ret, new IrClassRef(cls)))
            diag.Error(Codes.TypeMismatch, ctx.File, od.Span,
                $"'as' converts its parameter to '{Mangler.DisplayName(cls)}' " +
                $"and must return '{Mangler.DisplayName(cls)}', not '{Describe(ret)}'");
        
        // Comparisons and logical not produce truth values, and '!=' is derived from '==' (and
        // vice versa) by negation when only one of the pair is declared - both only work if
        // these return bool.
        if ((isCmp || od.Op == "!") && ret is not IrPrimType { CName: "bool" })
            diag.Error(Codes.TypeMismatch, ctx.File, od.Span,
                $"operator '{od.Op}' must return 'bool', not '{Describe(ret)}'");
        
        // ++/-- mutate self in place. A value-producing form would be ambiguous about
        // pre/post semantics, so they are statements, never expressions.
        if (isMutator && ret is not IrVoidType)
            diag.Error(Codes.TypeMismatch, ctx.File, od.Span,
                $"operator '{od.Op}' mutates in place and must return 'void', not '{Describe(ret)}'");

        var pars = new List<IrParam>(od.Params.Length);
        for (int i = 0; i < od.Params.Length; i++)
        {
            var p = od.Params[i];
            pars.Add(new IrParam(p.Name, ResolveType(p.Type), p.IsRef));
        }

        string cname = Mangler.Operator(cls, od.Op, od.Params, sym.IsOverloadedOperator(cls, od.Op));
        var octx = ctx.WithClass(cls).WithFunc($"op_{Mangler.OpSuffix(od.Op)}").WithStatic(isAs).PushScope(isParams: true);
        if (!isAs) octx.Locals.Declare("self", new IrClassRef(cls));
        foreach (var p in od.Params) octx.Locals.Declare(p.Name, ResolveType(p.Type), p.IsRef);
        var (body, native) = ResolveBodyOrNative(od.Body, octx, ret);
        CheckMissingReturn(body, ret, false, od.Span, $"operator {od.Op} on {Mangler.DisplayName(cls)}", ctx);
        if (body != null) { CheckBodyQuality(body, ret, od.Span, ctx, od.Params, od.Span); CheckThrowsPlacement(body, octx); }

        return new IrOperator(od.Op, cname, ret, pars, cls, lib, vis, body, native, IsStatic: isAs);
    }

    /// <summary>
    /// Resolves a free function declaration, type-checking its signature and body, and producing a
    /// fully typed IR function node.
    /// </summary>
    private IrFunction ResolveFreeFunc(FuncDecl fd, ResolveCtx ctx)
    {
        bool lib = ctx.Realm == Realm.None;
        var vis = VisOf(ctx.Realm);
        if (fd.IsEntry)
        {
            if (fd.Params.Length > 0)
                diag.Error(Codes.BadEntrySignature, ctx.File, fd.Span,
                    $"'{fd.Name}': an 'entry func' takes no parameters (it is invoked by the runtime, never called with arguments)");
            if (fd.ReturnType != null)
                diag.Error(Codes.BadEntrySignature, ctx.File, fd.Span,
                    $"'{fd.Name}': an 'entry func' has no return value; remove the return type");
            if (fd.Throws)
                diag.Error(Codes.BadEntrySignature, ctx.File, fd.Span,
                    $"'{fd.Name}': an 'entry func' cannot be 'throws' - there is no caller to receive the error");
        }
        if (!fd.Throws) CheckType(fd.ReturnType, ctx, fd.Span, allowVoid: true);
        foreach (var p in fd.Params) CheckType(p.Type, ctx, p.Span);
        CheckParams(fd.Params, ctx);
        var ret = fd.Throws && fd.ReturnType is null ? IrType.Int : ResolveType(fd.ReturnType);
        CheckThrowsReturn(ret, fd.Throws, fd.Name, ctx, fd.Span);
        
        var pars = new List<IrParam>(fd.Params.Length);
        for (int i = 0; i < fd.Params.Length; i++)
        {
            var p = fd.Params[i];
            pars.Add(new IrParam(p.Name, ResolveType(p.Type), p.IsRef));
        }

        string cname = (fd.Modifiers & Modifiers.Private) != 0
            ? Mangler.PrivateFreeFunc(Mangler.FileToken(ctx.File), fd.Name, fd.Params,
                sym.PrivateFuncOverloads(ctx.File, fd.Name).Count > 1)
            : Mangler.FreeFunc(fd.Name, fd.Params, sym.IsOverloadedFunc(fd.Name), fd.IsEntry, isExtern: false);
        var fctx = ctx.WithFunc(fd.Name).WithStatic(true).WithThrowsFunc(fd.Throws).PushScope(isParams: true);
        foreach (var p in fd.Params) fctx.Locals.Declare(p.Name, ResolveType(p.Type), p.IsRef);
        var (body, native) = ResolveBodyOrNative(fd.Body, fctx, ret);
        CheckMissingReturn(body, ret, fd.Throws, fd.Span, fd.Name, ctx);
        if (body != null) { CheckBodyQuality(body, ret, fd.Span, ctx, fd.Params, fd.Span); CheckThrowsPlacement(body, fctx); }

        return new IrFunction(fd.Name, cname, ret, pars, true, fd.IsEntry, fd.Throws, lib, vis,
            null, body, native, [..fd.Annotations]);
    }

    /// <summary>
    /// Resolves a method body or native block, returning the IR block and raw C string.
    /// </summary>
    private (IrBlock? Body, string? Native) ResolveBodyOrNative(MethodBody b, ResolveCtx ctx, IrType ret)
    {
        return b switch
        {
            NativeMethodBody nmb => (null, nmb.Native.C),
            BlockBody bb => (ResolveBlock(bb.Block, ctx with { RetType = ret }, ret), null),
            _ => (null, null)
        };
    }

    /// <summary>
    /// Resolves a process declaration to its IR form, resolving each thread's entry function.
    /// </summary>
    private IrProcess ResolveProcess(ProcessDecl pd, ResolveCtx ctx, IrModule module)
    {
        var vis = VisOf(ctx.Realm);

        var threads = new List<IrThread>(pd.Threads.Length);
        var seenThreads = new HashSet<string>();
        for (int i = 0; i < pd.Threads.Length; i++)
        {
            var td = pd.Threads[i];
            if (td.Mode != null)
                diag.Error(Codes.ThreadModeNotAllowed, ctx.File, td.Span,
                    $"thread '{td.Name}' has explicit mode '{td.Mode}'; threads do not support 'foreground' or 'background' modifiers");
            if (!seenThreads.Add(td.Name))
                diag.Error(Codes.DuplicateName, ctx.File, td.Span,
                    $"thread '{td.Name}' is already declared in process '{pd.Name}'");
            string tFull = $"{ScopeBinder.NameOf(ctx.Realm)}_{pd.Name}_{td.Name}";
            threads.Add(new IrThread(td.Name, tFull, ResolveThreadEntry(tFull, td.Entry, ctx, vis)));
        }
        foreach (var item in pd.Items)
        {
            if (item is FuncDecl { IsEntry: true } ef)
            {
                diag.Error(Codes.EntryOutsideKernel, ctx.File, ef.Span,
                    $"'{ef.Name}' is declared 'entry' inside process '{pd.Name}'",
                    ["a process's entry points are its threads; declare a 'thread' instead, " +
                     "or move the function out of the process"]);
                continue;
            }
            ResolveTop(item, ctx, module);
        }

        return new IrProcess(pd.Name, pd.Mode, threads);
    }

    /// <summary>
    /// Resolves a thread entry function declaration, checking parameter types and building the IR
    /// body. Applies CheckBodyQuality so unused-variable warnings are emitted for entry code.
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

        var fctx = ctx.WithStatic(true).PushScope(isParams: true);
        foreach (var p in ef.Params) fctx.Locals.Declare(p.Name, ResolveType(p.Type));
        var body = ResolveBlock(ef.Body, fctx, IrType.Void);
        CheckBodyQuality(body, IrType.Void, ef.Span, ctx, ef.Params, ef.Span);
        CheckThrowsPlacement(body, fctx);
        return new IrFunction(fullName, Mangler.ThreadEntry(fullName), IrType.Void, pars, true, true, false,
            false, vis, null, body, null, []);
    }

    /// <summary>
    /// Resolves a call to a generic free function by inferring type arguments from the supplied
    /// argument types, mangling the name, and queuing the instantiation for resolution after the
    /// main pass completes.
    /// </summary>
    private IrExpr ResolveGenericCall(
        (FuncDecl Decl, string File, Realm Realm) t,
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

        var binds = new Dictionary<string, TypeSpec>();
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

        string mangled = Mangler.GenericInstance(fd.Name, fd.GenericParams.Select(p => Monomorphizer.SanitizeTypeName(binds[p].ToSpecString())));
        _usedFuncTemplates.Add((t.File, fd.Name));
        if (_genericSeen.Add(mangled))
            _genericQueue.Enqueue((fd, t.File, t.Realm, binds, mangled));

        var concreteParams = Monomorphizer.SubParams(fd.Params, binds);

        string cname = (fd.Modifiers & Modifiers.Private) != 0
            ? Mangler.PrivateFreeFunc(Mangler.FileToken(t.File), mangled, concreteParams,
                sym.PrivateFuncOverloads(t.File, mangled).Count > 1)
            : Mangler.FreeFunc(mangled, concreteParams, overloaded: false, isEntry: false, isExtern: false);
        var ret = fd.ReturnType is null
            ? (fd.Throws ? IrType.Int : IrType.Void)
            : ResolveType(Monomorphizer.SubType(fd.ReturnType, binds));
        CoerceArgs(args, new MethodSig(fd.ReturnType, [..concreteParams], true, fd.Throws, false, [..fd.Annotations]), ctx, astArgs);

        if (fd.Throws) { CheckThrowsHandled(ctx, span); return new IrThrowsCall(cname, ret, args); }
        return new IrStaticCall(cname, ret, args);
    }

    /// <summary>
    /// Resolves a call to a generic method (on a module or a class, static or instance) by
    /// inferring type arguments from the supplied argument types, mangling the name, and queuing
    /// the instantiation for resolution after the main pass completes.
    /// </summary>
    private IrExpr ResolveGenericMethodCall(
        (MethodDecl Decl, string File, Realm Realm) t, string owner, bool isStatic,
        List<IrExpr> args, ResolveCtx ctx, TextSpan span, IrExpr? recv = null, Expr[]? astArgs = null)
    {
        var md = t.Decl;
        string fallback = Mangler.Method(owner, md.Name, [], false);
        IrExpr FallbackCall() => recv != null
            ? new IrInstanceCall(recv, fallback, IrType.Void, args)
            : new IrStaticCall(fallback, IrType.Void, args);

        if (md.Params.Length != args.Count)
        {
            diag.Error(Codes.WrongArgCount, ctx.File, span,
                $"generic '{Mangler.DisplayName(owner)}.{md.Name}' expects {md.Params.Length} argument(s), got {args.Count}");
            return FallbackCall();
        }

        var binds = new Dictionary<string, TypeSpec>();
        for (int i = 0; i < md.Params.Length; i++)
            if (!Monomorphizer.UnifyParam(md.Params[i].Type, args[i].Type, md.GenericParams, binds))
                diag.Error(Codes.ArgTypeMismatch, ctx.File, span,
                    $"in call to generic '{Mangler.DisplayName(owner)}.{md.Name}', argument {i + 1} ('{Describe(args[i].Type)}') conflicts with an earlier binding of the same type parameter");

        var missing = md.GenericParams.Where(p => !binds.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            diag.Error(Codes.UndefinedType, ctx.File, span,
                $"cannot infer type argument {string.Join(", ", missing.Select(m => $"'{m}'"))} for generic '{Mangler.DisplayName(owner)}.{md.Name}' from its arguments");
            return FallbackCall();
        }

        string mangled = Mangler.GenericInstance(md.Name, md.GenericParams.Select(p => Monomorphizer.SanitizeTypeName(binds[p].ToSpecString())));
        string seenKey = owner + "::" + mangled;
        _usedMethodTemplates.Add(new MemberKey(owner, md.Name));
        if (_genericSeen.Add(seenKey))
            _genericMethodQueue.Enqueue((md, owner, t.File, t.Realm, binds, mangled));

        var concreteParams = Monomorphizer.SubParams(md.Params, binds);
        string cname = Mangler.Method(owner, mangled, concreteParams, overloaded: false);
        var ret = md.ReturnType is null
            ? (md.Throws ? IrType.Int : IrType.Void)
            : ResolveType(Monomorphizer.SubType(md.ReturnType, binds));
        CoerceArgs(args, new MethodSig(md.ReturnType, [..concreteParams], isStatic, md.Throws, false, [..md.Annotations]), ctx, astArgs);

        if (md.Throws)
        {
            CheckThrowsHandled(ctx, span);
            return recv != null ? new IrThrowsInstanceCall(recv, cname, ret, args) : new IrThrowsCall(cname, ret, args);
        }
        return recv != null ? new IrInstanceCall(recv, cname, ret, args) : new IrStaticCall(cname, ret, args);
    }

    /// <summary>
    /// Resolves every generic free-function instantiation queued during the main pass, substituting
    /// concrete type bindings and registering the result in the module.
    /// </summary>
    private void DrainGenericInstances(IrModule module)
    {
        DrainGenericInstancesCore(module);
    }

    /// <summary>
    /// The body of DrainGenericInstances, run with instantiation collection suppressed.
    /// </summary>
    private void DrainGenericInstancesCore(IrModule module)
    {
        while (_genericQueue.Count > 0)
        {
            var (fd, file, realm, binds, mangled) = _genericQueue.Dequeue();
            var cMap = binds.ToDictionary(kv => kv.Key, kv => Monomorphizer.CTypeOf(kv.Value));
            var instRet = Monomorphizer.SubType(fd.ReturnType, binds);
            if (fd.Throws) sym.RegisterThrows(instRet);
            var inst = new FuncDecl(fd.Modifiers, fd.Annotations,
                instRet, mangled, [],
                [..Monomorphizer.SubParams(fd.Params, binds)], fd.IsEntry, fd.Throws,
                Monomorphizer.SubBody(fd.Body, binds, cMap), fd.Span);
            _scope = visible.GetValueOrDefault(file, [file]);
            var ctx = new ResolveCtx(file, realm, "", null, false, false, false, false, "", 0, new ScopeStack());
            module.FreeFunctions.Add(ResolveFreeFunc(inst, ctx));
        }

        while (_genericMethodQueue.Count > 0)
        {
            var (md, owner, file, realm, binds, mangled) = _genericMethodQueue.Dequeue();
            var cMap = binds.ToDictionary(kv => kv.Key, kv => Monomorphizer.CTypeOf(kv.Value));
            var instMethodRet = Monomorphizer.SubType(md.ReturnType, binds);
            if (md.Throws) sym.RegisterThrows(instMethodRet);
            var inst = new MethodDecl(md.Modifiers, md.Annotations,
                instMethodRet, mangled, [],
                [..Monomorphizer.SubParams(md.Params, binds)], md.IsEntry, md.Throws,
                Monomorphizer.SubBody(md.Body, binds, cMap), md.Span);
            _scope = visible.GetValueOrDefault(file, [file]);
            bool isModule = sym.Modules.Contains(owner);
            var ctx = new ResolveCtx(file, realm, "", null, false, false, false, false, "", 0, new ScopeStack());
            var lib = realm == Realm.None;
            var fn = ResolveMethod(owner, inst, ctx.WithClass(owner), lib, VisOf(realm), isModule);
            var cls = module.Classes.Find(c => c.Name == owner);
            cls?.Methods.Add(fn);
        }
    }

    /// <summary>
    /// Checks whether a bare call is a retain/release ARC intrinsic and returns the appropriate IR
    /// node. Returns null for all other names.
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

        // An unmanaged argument makes both calls vanish - retain to the value itself, release
        // to a void cast that keeps the argument evaluated and silences an unused warning.
        // That is what lets one generic body serve List[int] and List[String] alike.
        if (!IsManagedRef(a.Type))
            return isRetain ? a : new IrCast(IrType.Void, a);

        // A union counts its live variant's payload through its own generated pair, not
        // through the runtime intrinsic - which takes void* and would not accept an aggregate.
        string cname = a.Type is IrUnionType ut
            ? isRetain ? Mangler.UnionRetain(ut.Name) : Mangler.UnionRelease(ut.Name)
            : fsym.CName;

        return new IrStaticCall(cname, isRetain ? a.Type : IrType.Void, [a]);
    }

    /// <summary>
    /// Reports the two ways a union comparison can mean something other than "these hold the same
    /// value" - worth saying only because the comparison is generated. Reported at the comparison,
    /// so a union nobody compares stays silent.
    /// </summary>
    private void WarnOnUnionComparison(string unionName, ResolveCtx ctx, TextSpan span)
    {
        var identity = new List<string>();
        var imprecise = new List<string>();
        CollectComparisonHazards(unionName, "", [], identity, imprecise);

        if (identity.Count > 0)
            diag.Warn(Codes.IdentityPayloadComparison, ctx.File, span,
                $"comparing '{unionName}' compares {Describe(identity)} by identity, not by value",
                [
                    "two separately built payloads will differ even when they hold the same data",
                    "declare an '==' operator on the payload class to compare it by value",
                ]);

        if (imprecise.Count > 0)
            diag.Warn(Codes.ImprecisePayloadComparison, ctx.File, span,
                $"comparing '{unionName}' compares {Describe(imprecise)} with floating-point '=='",
                ["values produced by different arithmetic rarely compare equal; compare with a tolerance instead"]);

        static string Describe(List<string> fields) =>
            fields.Count == 1
                ? $"variant field {fields[0]}"
                : $"variant fields {string.Join(", ", fields.Take(3))}" +
                  (fields.Count > 3 ? $" and {fields.Count - 3} more" : "");
    }

    /// <summary>
    /// Collects the fields whose generated comparison is by identity or floating point, through
    /// nested unions and arrays. <paramref name="qualifier"/> reports a nested field as
    /// 'Mixed.Ident.p', since 'Ident.p' would point at the wrong declaration.
    /// </summary>
    private void CollectComparisonHazards(
        string unionName, string qualifier, HashSet<string> visiting,
        List<string> identity, List<string> imprecise)
    {
        if (!visiting.Add(unionName)) return;
        if (sym.UnionDef(unionName) is not { } variants) { visiting.Remove(unionName); return; }

        foreach (var v in variants)
        {
            foreach (var f in v.Fields)
            {
                Inspect(ResolveType(f.Type), $"'{qualifier}{v.Name}.{f.Name}'");
            }
        }
        visiting.Remove(unionName);

        void Inspect(IrType t, string label)
        {
            switch (t)
            {
                case IrArrayType a:
                    // A fixed array compares element-wise, so a hazard in the element type is a
                    // hazard in the field.
                    Inspect(a.Elem, label);
                    break;

                case IrUnionType nested:
                    CollectComparisonHazards(
                        nested.Name, $"{qualifier}{nested.Name}.", visiting, identity, imprecise);
                    break;

                case IrClassRef cr when sym.IsClass(cr.ClassName) && !sym.Modules.Contains(cr.ClassName):
                    if (sym.LookupOperator(cr.ClassName, "==", 1) == null
                        && !Mangler.TryGetGenericInstance(cr.ClassName, out _, out _))
                        identity.Add($"{label} ({Mangler.DisplayName(cr.ClassName)})");
                    break;

                case IrPrimType p when p.IsFloat:
                    imprecise.Add($"{label} ({p.CName})");
                    break;
            }
        }
    }

    /// <summary>
    /// True for values that participate in ARC: a class reference, or a union with a managed
    /// payload. What <see cref="ManagedTypes"/> answers for the back end, asked before the IR
    /// exists - and the two disagreeing is a leak or a link error.
    /// </summary>
    private bool IsManagedRef(IrType t)
    {
        return t switch
        {
            IrClassRef cr => sym.IsClass(cr.ClassName) && !sym.Modules.Contains(cr.ClassName),
            IrUnionType ut => IsManagedUnion(ut.Name, []),
            _ => false
        };
    }

    private readonly Dictionary<string, bool> _managedUnionCache = [];

    // Set while a recursive managed-union walk is standing on a cycle, so the partial answer
    // that produced is not committed to the cache.
    private bool _cycleCut;

    /// <summary>
    /// True if the named union stores a managed value in any variant, directly or nested. <paramref
    /// name="visiting"/> guards the recursion, and an answer it cut short is conditioned on where
    /// the walk started, so it is not cached.
    /// </summary>
    private bool IsManagedUnion(string name, HashSet<string> visiting)
    {
        if (_managedUnionCache.TryGetValue(name, out bool cached)) return cached;
        if (!visiting.Add(name)) { _cycleCut = true; return false; }

        bool outerCut = _cycleCut;
        _cycleCut = false;
        bool managed = false;

        if (sym.UnionDef(name) is { } variants)
            foreach (var v in variants)
            {
                foreach (var f in v.Fields)
                {
                    if (f.Type is not NamedSpec ns) continue;
                    string fieldName = ns.Mangled;
                    if (sym.IsUnion(fieldName) ? IsManagedUnion(fieldName, visiting)
                                               : sym.IsClass(fieldName) && !sym.Modules.Contains(fieldName))
                    {
                        managed = true;
                        break;
                    }
                }
                if (managed) break;
            }

        visiting.Remove(name);
        if (!_cycleCut) _managedUnionCache[name] = managed;
        _cycleCut |= outerCut;
        return managed;
    }

    /// <summary>
    /// Resolves an enum declaration to its IR form. Members may carry optional explicit integer
    /// values parsed from integer literals.
    /// </summary>
    private IrEnum ResolveEnum(EnumDecl ed, ResolveCtx ctx)
    {
        // "typedef enum { } E;" is a constraint violation in C, so an enum with no
        // members has to be rejected here rather than surfacing as a gcc error later.
        if (ed.Members.Length == 0)
            diag.Error(Codes.BadDeclHeader, ctx.File, ed.Span,
                $"enum '{ed.Name}' declares no members",
                ["an enum needs at least one member, e.g. 'enum " + ed.Name + " { First }'"]);

        var members = new List<(string, string?)>();
        var seen = new HashSet<string>();
        var values = new Dictionary<string, long>();
        long next = 0;
        foreach (var m in ed.Members)
        {
            if (!seen.Add(m.Name))
                diag.Error(Codes.DuplicateName, ctx.File, m.Span,
                    $"enum '{ed.Name}' already declares a member '{m.Name}'");
            string? cval = null;
            if (m.Value != null)
            {
                if (TryConstEval(m.Value, ed.Name, values, out long v))
                {
                    cval = v.ToString();
                    next = v;
                }
                else
                    diag.Error(Codes.TypeMismatch, ctx.File, m.Span,
                        $"enum '{ed.Name}' member '{m.Name}' must be a constant integer expression " +
                        "(integer/char literals, earlier members, and + - * / % << >> & | ^ ~ -)");
            }
            if (next is < int.MinValue or > int.MaxValue)
            {
                diag.Error(Codes.TypeMismatch, ctx.File, m.Span,
                    $"enum '{Mangler.DisplayName(ed.Name)}' member '{m.Name}' is {next}, which does not fit in 'int'",
                    [$"enum members are read as 'int', so this would be used as {Truncate(next, 32, false)}",
                     "pick a value in -2147483648 to 2147483647"]);
                next = 0;
            }
            values[m.Name] = next;
            members.Add((m.Name, cval));
            next++;
        }
        return new IrEnum(ed.Name, Mangler.Enum(ed.Name), members);
    }

    /// <summary>
    /// Folds a constant integer expression: literals, unary negate/complement, the arithmetic and
    /// bitwise operators, and earlier members of the enclosing enum. False on any non-constant
    /// subexpression or a division by zero.
    /// </summary>
    private static bool TryConstEval(Expr e, string enumName, Dictionary<string, long> members, out long v)
    {
        v = 0;
        switch (e)
        {
            case IntLitExpr il:
                return TryParseIntLit(il.Value.AsSpan(), out v, out _, out _);
            case CharLitExpr cl:
                v = cl.Value;
                return true;
            case IdentExpr ie:
                return members.TryGetValue(ie.Name, out v);
            case MemberAccessExpr { Object: IdentExpr oid } ma when oid.Name == enumName:
                return members.TryGetValue(ma.Member, out v);
            case UnaryExpr un:
            {
                if (!TryConstEval(un.Operand, enumName, members, out long o)) return false;
                switch (un.Op)
                {
                    case UnOp.Neg: v = -o; return true;
                    case UnOp.BitNot: v = ~o; return true;
                    default: return false;
                }
            }
            case BinExpr be:
            {
                if (!TryConstEval(be.Left, enumName, members, out long l)) return false;
                if (!TryConstEval(be.Right, enumName, members, out long r)) return false;
                switch (be.Op)
                {
                    case BinOp.Add: v = l + r; return true;
                    case BinOp.Sub: v = l - r; return true;
                    case BinOp.Mul: v = l * r; return true;
                    case BinOp.Div: if (r == 0) return false; v = l / r; return true;
                    case BinOp.Mod: if (r == 0) return false; v = l % r; return true;
                    case BinOp.Shl: v = l << (int)(r & 63); return true;
                    case BinOp.Shr: v = l >> (int)(r & 63); return true;
                    case BinOp.BitAnd: v = l & r; return true;
                    case BinOp.BitOr: v = l | r; return true;
                    case BinOp.BitXor: v = l ^ r; return true;
                    default: return false;
                }
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// True if union <paramref name="from"/> stores <paramref name="target"/> by value, directly or
    /// through other unions - a pointer is a fixed-size field and breaks the cycle. <paramref
    /// name="visited"/> stops an already-cyclic graph looping forever.
    /// </summary>
    private bool UnionContains(string from, string target, HashSet<string> visited)
    {
        if (from == target) return true;
        if (!visited.Add(from)) return false;
        if (sym.UnionDef(from) is not { } variants) return false;
        foreach (var v in variants)
            foreach (var f in v.Fields)
                if (f.Type is NamedSpec ns && UnionContains(ns.Mangled, target, visited))
                    return true;
        return false;
    }

    private IrUnion ResolveUnion(UnionDecl ud, ResolveCtx ctx)
    {
        using var instanceScope = diag.InstanceScope(
            Mangler.TryGetGenericInstance(ud.Name, out _, out _) ? ud.Name : null);
        using var instanceName = TrackInstance(ud.Name);

        if (ud.Variants.Length == 0)
            diag.Error(Codes.BadDeclHeader, ctx.File, ud.Span,
                $"union '{ud.Name}' declares no variants",
                ["a union needs at least one variant, e.g. 'union " + ud.Name + " { First }'"]);

        var variants = new List<IrUnionVariant>();
        var seen = new HashSet<string>();
        foreach (var v in ud.Variants)
        {
            if (!seen.Add(v.Name))
                diag.Error(Codes.DuplicateName, ctx.File, v.Span,
                    $"union '{ud.Name}' already declares a variant '{v.Name}'");
            var fields = new List<IrParam>();
            // A variant's fields become one C struct, so two of the same name would emit
            // two members with one name - rejected by the C compiler, not by us, unless
            // this catches it first.
            var seenFields = new HashSet<string>();
            foreach (var f in v.Fields)
            {
                CheckType(f.Type, ctx, f.Span);
                var ft = ResolveType(f.Type);
                if (ft is IrClassRef mcr && sym.Modules.Contains(mcr.ClassName))
                    diag.Error(Codes.TypeMismatch, ctx.File, f.Span,
                        $"union variant field '{f.Name}' has type '{Describe(ft)}', which is a module",
                        ["a module is a namespace for functions, not a value; it cannot be stored"]);
                if (!seenFields.Add(f.Name))
                    diag.Error(Codes.DuplicateName, ctx.File, f.Span,
                        $"variant '{v.Name}' already declares a field '{f.Name}'");
                if (f.Type is NamedSpec ns && UnionContains(ns.Mangled, ud.Name, []))
                    diag.Error(Codes.TypeMismatch, ctx.File, f.Span,
                        ns.Mangled == ud.Name
                            ? $"variant field '{f.Name}' has type '{Mangler.DisplayName(ud.Name)}', the union being " +
                              $"declared; a union cannot contain itself by value"
                            : $"variant field '{f.Name}' has type '{Mangler.DisplayName(ns.Mangled)}', which contains " +
                              $"'{Mangler.DisplayName(ud.Name)}' by value; the two would have no size",
                        ["store a pointer, or hold it through a container such as List"]);
                fields.Add(new IrParam(f.Name, ft));
            }
            variants.Add(new IrUnionVariant(v.Name, Mangler.UnionTag(ud.Name, v.Name), fields));
        }
        return new IrUnion(ud.Name, Mangler.Union(ud.Name), variants);
    }

    #endregion

    #region Statement resolvers

    /// <summary>
    /// Resolves a block by pushing a new scope, resolving all statements, and warning on
    /// unreachable code.
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
    /// Resolves a statement, propagating the source span to the result when the resolver did not
    /// set one.
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
            case NativeStmt ns: return new IrNativeStmt(ns.Body.C);
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
                    if (SameStorage(target, value))
                        diag.Warn(Codes.SelfAssignment, ctx.File, asgn.Span,
                            "this assignment stores a value into itself and has no effect",
                            ["did you mean to assign a different value, or to write 'self." +
                             $"{(target is IrVar tv ? tv.Name : "field")}' on one side?"]);
                    value = CheckRootThrowsValue(value, target.Type, "the assignment target", ctx, asgn.Span);
                    return new IrAssign(target, AssignOp.Assign, value);
                }
                ForbidThrowsInAssignForm(value, $"a '{asgn.Op.Sym()}' compound assignment", ctx);
                string baseOp = asgn.Op.BaseOp()!.Value.Sym();
                string? lhsClass = ClassNameOf(target.Type);
                if (lhsClass != null && sym.LookupOperator(lhsClass, baseOp, 1) is { } opSym)
                {
                    CheckOperatorAccess(lhsClass, baseOp, ctx, asgn.Span);
                    value = CheckOpArg(opSym, value, ctx);
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
                if (e is IrCatchCall sc && ContainsAssignValue(sc.Handler))
                    diag.Error(Codes.AssignOutsideCatch, ctx.File, sc.Handler.Span,
                        "'assign' needs a declaration to supply a value for, and this call's result is discarded",
                        [$"bind the call first: 'let T x = ... catch {{ assign <value>; }};'"]);
                WarnIfNoEffect(es.E, e, ctx);
                return new IrExprStmt(e);
            }

            case ReturnStmt rs:
            {
                if (ctx.InDefer)
                    diag.Error(Codes.DeferTransfer, ctx.File, rs.Span, "a 'defer' body cannot 'return'");
                if (rs.Value == null)
                {
                    if (retType is not IrVoidType && retType is not IrResultType)
                        diag.Error(Codes.ReturnTypeMismatch, ctx.File, rs.Span,
                            $"function must return '{Describe(retType)}'");
                    return new IrReturn(null);
                }
                // The declared return type is what the value is expected to be, which is what
                // lets 'return Maybe.Missing();' pick an instantiation its arguments cannot.
                var retCtx = retType is IrResultType rrt
                    ? ctx with { Expected = rrt.Inner }
                    : ctx with { Expected = retType };
                var v = Coerce(ResolveExpr(rs.Value, retCtx), retType, ctx);
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
                CheckCondition(cond, ctx, allowConst: true);
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
                var body = new IrBlock(stmts) { Span = ub.Span };
                WarnUnsafeManagedTemporary(body, ctx);
                return new IrUnsafeBlock(body);
            }

            case SwitchStmt sw:
                return ResolveSwitch(sw, ctx, retType);

            case MatchStmt ms:
                return ResolveMatch(ms, ctx, retType);

            case BreakStmt:
                if (ctx.LoopDepth == 0)
                    diag.Error(Codes.BreakOutsideLoop, ctx.File, s.Span, "'break' is only valid inside a loop");
                if (ctx.InDefer)
                    diag.Error(Codes.DeferTransfer, ctx.File, s.Span, "a 'defer' body cannot 'break'");
                return new IrBreak();

            case ContinueStmt:
                if (ctx.LoopDepth == 0)
                    diag.Error(Codes.BreakOutsideLoop, ctx.File, s.Span, "'continue' is only valid inside a loop");
                if (ctx.InDefer)
                    diag.Error(Codes.DeferTransfer, ctx.File, s.Span, "a 'defer' body cannot 'continue'");
                return new IrContinue();

            case TryCatchStmt tc:
                return ResolveTryCatch(tc, ctx, retType);

            case DeferStmt ds:
            {
                if (ctx.InDefer)
                    diag.Error(Codes.DeferTransfer, ctx.File, ds.Span, "a 'defer' body cannot itself 'defer'");
                if (ds.Action is LetStmt dlet)
                    diag.Error(Codes.NoEffect, ctx.File, ds.Span,
                        $"a 'defer' body cannot be a declaration; '{dlet.Name}' would go out of scope immediately",
                        ["declare the variable before the 'defer' and use it in the deferred action",
                         "or wrap the action in a block: 'defer { ... }'"]);
                var dctx = ctx.WithDefer().PushScope();
                return new IrDefer(ResolveStmt(ds.Action, dctx, retType));
            }

            case ThrowStmt:
                if (ctx.InDefer)
                    diag.Error(Codes.DeferTransfer, ctx.File, s.Span, "a 'defer' body cannot 'throw'");
                CheckThrowsHandled(ctx, s.Span);
                return new IrThrow();

            case AssignValueStmt av:
            {
                if (ctx.AssignType == null)
                {
                    diag.Error(Codes.AssignOutsideCatch, ctx.File, s.Span,
                        "'assign' is only valid inside a 'catch' handler attached to a call",
                        ["it supplies the value for the declaration the handler belongs to",
                         "to return from the enclosing function, use 'return'"]);
                    return new IrAssignValue(ResolveExpr(av.Value, ctx));
                }
                if (ctx.InDefer)
                    diag.Error(Codes.DeferTransfer, ctx.File, s.Span, "a 'defer' body cannot 'assign'");

                var value = ResolveExpr(av.Value, ctx);
                ForbidNestedThrows(value, ctx, allowRoot: false);
                value = Coerce(value, ctx.AssignType, ctx);
                CheckAssign(value, ctx.AssignType, "'assign'", ctx, Codes.TypeMismatch);
                return new IrAssignValue(value);
            }

            case DebugStmt d:
                if (releaseMode)
                    diag.Error(Codes.DiagInRelease, ctx.File, s.Span,
                        "'debug' is not allowed in a release build", ["remove it before shipping"]);
                return new IrDebug(d.Raw) { Span = s.Span };

            case PanicStmt p:
                if (releaseMode)
                    diag.Error(Codes.DiagInRelease, ctx.File, s.Span,
                        "'panic' is not allowed in a release build", ["remove it before shipping"]);
                if (ctx.Realm != Realm.Kernel)
                    diag.Error(Codes.PanicOutsideKernel, ctx.File, s.Span,
                        "'panic' is only valid in the kernel realm");
                return new IrPanic(p.Raw) { Span = s.Span };

            default:
                throw new System.Diagnostics.UnreachableException($"[TypeResolver] unhandled Stmt: {s.GetType().Name}");
        }
    }

    /// <summary>
    /// Wraps a single statement in an IrBlock, pushing a new scope. When the statement is already a
    /// Block, resolves it directly without double-wrapping.
    /// </summary>
    private IrBlock WrapBlock(Stmt s, ResolveCtx ctx, IrType retType)
    {
        if (s is Block b) return ResolveBlock(b, ctx, retType);
        var inner = ctx.PushScope();
        return new IrBlock([ResolveStmt(s, inner, retType)]) { Span = s.Span };
    }

    /// <summary>
    /// Resolves a for statement, handling let, assignment, and expression init and step clauses in
    /// a new scope with the loop depth incremented. Both clauses go through the full statement
    /// resolver so assignments get lvalue, type, and throws checking.
    /// </summary>
    private IrFor ResolveFor(ForStmt fs, ResolveCtx ctx, IrType retType)
    {
        var fctx = ctx.PushScope() with { LoopDepth = ctx.LoopDepth + 1 };
        IrStmt? init = fs.Init != null ? ResolveStmt(fs.Init, fctx, retType) : null;
        IrExpr? cond = fs.Cond != null ? ResolveExpr(fs.Cond, fctx) : null;
        ForbidNestedThrows(cond, fctx, allowRoot: false);
        if (cond != null) CheckCondition(cond, fctx, allowConst: true);
        IrStmt? step = fs.Step != null ? ResolveStmt(fs.Step, fctx, retType) : null;
        var body = ResolveBlock(fs.Body, fctx, retType);
        WarnIfEmpty(body, "for", fctx, fs.Span);
        return new IrFor(init, cond, step, body);
    }

    /// <summary>
    /// Resolves a for-in statement over a fixed array or any class with Length and Get methods.
    /// </summary>
    private IrForIn ResolveForIn(ForInStmt fi, ResolveCtx ctx, IrType retType)
    {
        var collection = ResolveExpr(fi.Collection, ctx);
        ForbidNestedThrows(collection, ctx, allowRoot: false);

        if (collection.Type is IrArrayType at)
        {
            var ainner = ctx.PushScope() with { LoopDepth = ctx.LoopDepth + 1 };
            ainner.Locals.Declare(fi.Var, at.Elem);
            var abody = ResolveBlock(fi.Body, ainner, retType);
            WarnIfEmpty(abody, "for..in", ctx, fi.Span);
            return new IrForIn(fi.Var, at.Elem, "", "", collection, abody, at.Size);
        }

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
        else if (collection.Type.IsError)
        {
            elemType = IrType.Error;
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
        inner.Locals.Declare(fi.Var, elemType);
        var body = ResolveBlock(fi.Body, inner, retType);
        WarnIfEmpty(body, "for..in", ctx, fi.Span);
        return new IrForIn(fi.Var, elemType, lenCName, getCName, collection, body);
    }

    /// <summary>
    /// Resolves a switch statement on an integer or enum scrutinee, validating that each case label
    /// is comparable to the scrutinee type.
    /// </summary>
    private IrSwitch ResolveSwitch(SwitchStmt sw, ResolveCtx ctx, IrType retType)
    {
        var scrut = ResolveExpr(sw.Scrutinee, ctx);
        ForbidNestedThrows(scrut, ctx, allowRoot: false);
        if (!(IsInteger(scrut.Type) || scrut.Type is IrEnumType || scrut.Type.IsError))
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
    /// Returns a duplicate-detection key for a constant case label, or null for non-constant labels
    /// that cannot be checked at compile time. Int and char labels share a key space since C
    /// compares them as integers.
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
    /// Resolves a match statement on a union scrutinee, binding each variant's fields into scope
    /// and checking exhaustiveness unless a default case is present.
    /// </summary>
    private IrMatch ResolveMatch(MatchStmt ms, ResolveCtx ctx, IrType retType)
    {
        var scrut = ResolveExpr(ms.Scrutinee, ctx);
        ForbidNestedThrows(scrut, ctx, allowRoot: false);
        if (scrut.Type is not IrUnionType ut)
        {
            if (!scrut.Type.IsError)
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
                caseCtx.Locals.Declare(c.Bindings[i], ft);
                binds.Add(new IrMatchBind(fields[i].Name, c.Bindings[i], ft));
            }
            cases.Add(new IrMatchCase(idx, binds, ResolveBlock(c.Body, caseCtx, retType)));
        }
        var def = ms.Default == null ? null : ResolveBlock(ms.Default, ctx, retType);

        if (def != null && covered.Count == variants.Count && variants.Count > 0)
            diag.Warn(Codes.UnreachableCase, ctx.File, ms.Span,
                $"this 'default' can never run: all {variants.Count} variant(s) of '{ut.Name}' are already matched",
                ["remove the 'default' so a new variant becomes a compile error instead of silently falling through"]);
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
    /// Resolves a try/catch statement, giving the try block a catch label so throwing calls inside
    /// it know where to jump on failure.
    /// </summary>
    private IrTryCatch ResolveTryCatch(TryCatchStmt tc, ResolveCtx ctx, IrType retType)
    {
        int seq = _labelSeq++;
        var tctx = ctx.WithTry($"_catch_{seq}");
        var tryBlock = ResolveBlock(tc.Try, tctx, retType);
        var catchBlock = ResolveBlock(tc.Catch, ctx, retType);
        return new IrTryCatch(tryBlock, catchBlock, seq);
    }

    /// <summary>
    /// Resolves `f() catch { ... }`: a throwing call that handles its own failure in place.
    /// </summary>
    private IrExpr ResolveCatchCall(CatchCallExpr cce, ResolveCtx ctx)
    {
        var call = ResolveExpr(cce.Call, ctx.WithCatchWrapped());

        if (call.Type is not IrResultType rt)
        {
            // Not a throwing call, so there is no failure to handle. Resolve the handler anyway
            // (against the value's own type) so names inside it still get checked and reported.
            diag.Error(Codes.ThrowsOutsideTry, ctx.File, cce.Call.Span,
                "'catch' here needs a call to a 'throws' function; this call cannot fail",
                ["remove the 'catch' block"]);
            ResolveBlock(cce.Handler, ctx.WithCatchHandler(call.Type), ctx.RetType ?? IrType.Void);
            return call;
        }

        var handler = ResolveBlock(cce.Handler, ctx.WithCatchHandler(rt.Inner), ctx.RetType ?? IrType.Void);
        return new IrCatchCall(call, handler, rt.Inner) { Span = cce.Span };
    }

    /// <summary>
    /// Resolves a let declaration: infers or checks its type, resolves the initializer, checks
    /// assignability, and declares the variable in the current scope.
    /// </summary>
    private IrDeclVar ResolveLet(LetStmt ls, ResolveCtx ctx)
    {
        IrType type;
        IrType? declared = null;
        if (ls.Type != null)
        {
            CheckType(ls.Type, ctx, ls.Span);
            declared = ResolveType(ls.Type);
        }

        IrExpr? init = ls.Init != null
            ? ResolveExpr(ls.Init, declared == null ? ctx : ctx with { Expected = declared })
            : null;

        if (declared != null)
        {
            type = declared;
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
       
        else if (init?.Type is IrResultType irt && ls.Type != null
                 && !Assignable(new IrVar(ls.Name, irt.Inner), type))
            diag.Error(Codes.TypeMismatch, ctx.File, init.Span,
                $"this throwing call produces '{Describe(irt.Inner)}', which cannot initialize '{ls.Name}' of type '{Describe(type)}'");

        if (init != null) ForbidNestedThrows(init, ctx, allowRoot: true);

        if (init is IrCatchCall cc && !AssignsOrExits(cc.Handler))
            diag.Error(Codes.CatchHandlerNoAssign, ctx.File, cc.Handler.Span,
                $"this 'catch' handler can finish without supplying a value for '{ls.Name}'",
                ["end every path with 'assign <value>;'",
                 "or leave the handler through 'return', 'throw', 'break', or 'continue'"]);

        CheckNotReservedLocal(ls.Name, ls.Span, "variable", ctx);
        if (ctx.Locals.DeclaredHere(ls.Name))
            diag.Error(Codes.DuplicateName, ctx.File, ls.Span, $"'{ls.Name}' is already declared in this scope");
        else if (ctx.Locals.CollidesWithParam(ls.Name))
            diag.Error(Codes.DuplicateName, ctx.File, ls.Span,
                $"'{ls.Name}' is already a parameter of this function",
                ["a parameter and a top-level local share one scope; rename one of them",
                 "shadowing is fine inside a nested block"]);
        else if (ctx.Locals.ShadowsOuter(ls.Name))
            diag.Warn(Codes.ShadowedVariable, ctx.File, ls.Span,
                $"'{ls.Name}' shadows a variable of the same name from an enclosing scope",
                ["rename this one if the outer variable was meant to stay reachable"]);
        WarnManagedFixedArray(type, $"'{ls.Name}'", ctx, ls.Span);
        ctx.Locals.Declare(ls.Name, type);
        return new IrDeclVar(ls.Name, type, init);
    }

    #endregion

    #region Expression resolvers
    /// <summary>
    /// Resolves an expression and propagates the source span when the resolver did not set one.
    /// </summary>
    private IrExpr ResolveExpr(Expr e, ResolveCtx ctx)
    {
        if (ctx.Expected != null && e is not (CallExpr or TernaryExpr)) ctx = ctx with { Expected = null };

        var r = ResolveExprCore(e, ctx);
        return r.Span.IsNone ? r with { Span = e.Span } : r;
    }

    /// <summary>
    /// Core expression resolver. Handles literals, identifiers, casts, postfix, unary, and binary
    /// expressions. Additional expression forms added in later commits.
    /// </summary>
    private IrExpr ResolveExprCore(Expr e, ResolveCtx ctx)
    {
        switch (e)
        {
            case PoisonExpr: return Poison(e.Span);
            case ScopedNameExpr sn:
                diag.Error(Codes.ScopeNotEnclosing, ctx.File, sn.Span,
                    "a scope qualifier is only meaningful inside a realm or process");
                return Poison(e.Span);
            case IntLitExpr il:
                if (!TryParseIntLit(il.Value.AsSpan(), out var ival, out var ity, out var ictext))
                    diag.Error(Codes.TypeMismatch, ctx.File, e.Span,
                        $"integer literal '{il.Value}' does not fit in 64 bits");
                return new IrLitInt(ival, ity, ictext);
            case CharLitExpr cl: return new IrLitChar(cl.Value);
            case FloatLitExpr fl: return new IrLitFloat(fl.Value, FloatLitType(fl.Value));
            case BoolLitExpr bl: return new IrLitBool(bl.Value == "true");
            case StrLitExpr sl:
                WarnIfLooksInterpolated(sl, ctx);
                return new IrLitString(sl.Value);
            case NullExpr: return new IrLitNull(IrType.Void);
            case IdentExpr ie: return ResolveIdent(ie, ctx);
            case CastExpr ce:
            {
                CheckType(ce.TargetType, ctx, ce.Span, allowVoid: true);
                var inner = ResolveExpr(ce.Value, ctx);
                var to = ResolveType(ce.TargetType);

                // as should be a static call to the destination class's as operator if it exists,
                // otherwise a normal cast
                if (!SameType(inner.Type, to) && ClassNameOf(to) is { } destCls
                    && FindAsOperator(destCls, inner.Type) is { } asOp)
                {
                    CheckOperatorAccess(destCls, "as", ctx, ce.Span);
                    return new IrStaticCall(asOp.CName, to, [inner]) { Span = ce.Span };
                }
                CheckCast(inner, to, ctx);
                return new IrCast(to, inner);
            }
            case PostfixExpr pf:
            {
                var opnd = ResolveExpr(pf.Operand, ctx);

                if (ClassNameOf(opnd.Type) is { } pfCls && sym.LookupOperator(pfCls, pf.Op.Sym(), 0) is { } pfOp)
                {
                    CheckOperatorAccess(pfCls, pf.Op.Sym(), ctx, pf.Span);
                    return new IrStaticCall(pfOp.CName, IrType.Void, [opnd]) { Span = pf.Span };
                }

                if (opnd.Type.IsError) return Poison(pf.Span);
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
            case CatchCallExpr cce: return ResolveCatchCall(cce, ctx);
            case MemberAccessExpr ma: return ResolveMemberAccess(ma, ctx);
            case NewExpr ne: return ResolveNew(ne, ctx);
            case ArrayLitExpr al: return ResolveArrayLit(al, ctx);
            case IndexExpr ix: return ResolveIndex(ix, ctx);
            case GenericTypeRefExpr g: return ResolveGenericTypeRef(g, ctx);
            case SizeofExpr so:
                CheckType(so.TypeName, ctx, so.Span);
                return new IrSizeof(ResolveType(so.TypeName));
            case DefaultExpr de:
                CheckType(de.TypeName, ctx, de.Span);
                return new IrDefault(ResolveType(de.TypeName));
            case AddrOfExpr ao:
            {
                if (!ctx.InUnsafe)
                    diag.Error(Codes.UnsafeRequired, ctx.File, ao.Span, "address-of '&' requires an 'unsafe' block");
                var target = ResolveExpr(ao.Target, ctx);
                if (target.Type.IsError) return Poison(ao.Span);
                if (target is not (IrVar or IrFieldLoad or IrIndex or IrDeref or IrSelfExpr))
                    diag.Error(Codes.NotAnLvalue, ctx.File, ao.Span,
                        "address-of '&' needs a variable, field, or element; this operand has no address",
                        ["bind the value to a local first, then take its address"]);
                return new IrAddrOf(target);
            }
            case DerefExpr dr:
            {
                if (!ctx.InUnsafe)
                    diag.Error(Codes.UnsafeRequired, ctx.File, dr.Span, "pointer dereference '*' requires an 'unsafe' block");
                var ptr = ResolveExpr(dr.Ptr, ctx);
                if (ptr.Type.IsError) return Poison(dr.Span);
                if (ptr.Type is not IrPtrType)
                    diag.Error(Codes.TypeMismatch, ctx.File, dr.Span,
                        $"pointer dereference '*' requires a pointer, got '{Describe(ptr.Type)}'");
                var inner = ptr.Type is IrPtrType pt ? pt.Inner : IrType.Error;
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
                if (then.Type.IsError || els.Type.IsError) return Poison(te.Span);
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
                throw new System.Diagnostics.UnreachableException($"[TypeResolver] unhandled Expr: {e.GetType().Name}");
        }
    }

    /// <summary>
    /// Resolves a unary expression, validating that the operand type is compatible with the
    /// operator.
    /// </summary>
    private IrExpr ResolveUnary(UnaryExpr un, ResolveCtx ctx)
    {
        var operand = ResolveExpr(un.Operand, ctx);
        if (operand.Type.IsError) return Poison(un.Span);
        if (ClassNameOf(operand.Type) is { } opCls && sym.LookupOperator(opCls, un.Op.Sym(), 0) is { } uop)
        {
            CheckOperatorAccess(opCls, un.Op.Sym(), ctx, un.Span);
            return new IrStaticCall(uop.CName, ResolveType(uop.Type), [operand]);
        }

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
    /// Coerces and type-checks the right-hand operand of a user-defined binary operator against the
    /// operator's declared parameter type. Returns the coerced operand.
    /// </summary>
    private IrExpr CheckOpArg(Symbol op, IrExpr right, ResolveCtx ctx)
    {
        if (op.Sig is not { Params: [var p] }) return right;
        var pt = ResolveType(p.Type);
        right = Coerce(right, pt, ctx);
        if (right.Type is not IrResultType && !Assignable(right, pt))
            diag.Error(Codes.ArgTypeMismatch, ctx.File, right.Span,
                $"operator '{op.Name}' on '{Mangler.DisplayName(op.Owner ?? "")}' takes '{Describe(pt)}', got '{Describe(right.Type)}'");
        return right;
    }

    /// <summary>
    /// Resolves a binary expression. Handles string concatenation, operator overloading, pointer
    /// arithmetic, logical, equality, relational, bitwise, and arithmetic operators.
    /// </summary>
    private IrExpr ResolveBin(BinExpr be, ResolveCtx ctx)
    {
        var left = ResolveExpr(be.Left, ctx);
        var right = ResolveExpr(be.Right, ctx);
        if (left.Type.IsError || right.Type.IsError) return Poison(be.Span);

        // + with a String operand stringifies the other side
        if (be.Op == BinOp.Add && (left.Type.IsString || right.Type.IsString))
        {
            string stringClass = sym.Builtins.GetValueOrDefault(BuiltinTypes.String, BuiltinTypes.String);
            var sop = sym.LookupOperator(stringClass, "+");
            if (sop == null)
                diag.Error(Codes.MissingIntrinsic, ctx.File, be.Span,
                    "String defines no '+' operator for concatenation");
            string cn = sop?.CName ?? Mangler.Operator(stringClass, "+", [], false);
            return new IrStaticCall(cn, IrType.String, [EnsureString(left, ctx), EnsureString(right, ctx)]);
        }

        if (be.Op is BinOp.Eq or BinOp.Ne && (left is IrLitNull || right is IrLitNull))
        {
            if (!ComparableEq(left, right))
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"'{be.Op.Sym()}' operands are not comparable: '{Describe(left.Type)}' and '{Describe(right.Type)}'");
            return new IrBinOp(be.Op, left, right, IrType.Bool);
        }

        string? lhsClass = ClassNameOf(left.Type);
        if (lhsClass != null && sym.LookupOperator(lhsClass, be.Op.Sym(), 1) is { } op)
        {
            CheckOperatorAccess(lhsClass, be.Op.Sym(), ctx, be.Span);
            right = CheckOpArg(op, right, ctx);
            return new IrStaticCall(op.CName, ResolveType(op.Type), [left, right]);
        }


        if (be.Op is BinOp.Eq or BinOp.Ne && lhsClass != null
            && sym.LookupOperator(lhsClass, be.Op == BinOp.Eq ? "!=" : "==", 1) is { } eqOp
            && ResolveType(eqOp.Type) is IrPrimType { CName: "bool" })
        {
            CheckOperatorAccess(lhsClass, be.Op == BinOp.Eq ? "!=" : "==", ctx, be.Span);
            right = CheckOpArg(eqOp, right, ctx);
            return new IrUnaryOp(UnOp.Not, new IrStaticCall(eqOp.CName, IrType.Bool, [left, right]), IrType.Bool);
        }

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

        if (be.Op is BinOp.Eq or BinOp.Ne
            && left.Type is IrUnionType lu && right.Type is IrUnionType ru && lu.Name == ru.Name)
        {
            WarnOnUnionComparison(lu.Name, ctx, be.Span);
            IrExpr call = new IrStaticCall(Mangler.UnionEq(lu.Name), IrType.Bool, [left, right]) { Span = be.Span };
            return be.Op == BinOp.Eq ? call : new IrUnaryOp(UnOp.Not, call, IrType.Bool);
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
                    $"operator '{be.Op.Sym()}' requires numeric operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'",
                    MissingRelationalHint(lhsClass, be.Op.Sym()));
            return new IrBinOp(be.Op, left, right, IrType.Bool);
        }

        if (be.Op is BinOp.BitAnd or BinOp.BitOr or BinOp.BitXor or BinOp.Shl or BinOp.Shr)
        {
            if (!(IsInteger(left.Type) && IsInteger(right.Type)))
                diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                    $"operator '{be.Op.Sym()}' requires integer operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'");

            // Shifts are undefined behaviour if the count is negative or >= the bit width of the left operand
            if (be.Op is BinOp.Shl or BinOp.Shr && right is IrLitInt sh
                && left.Type is IrPrimType lp && PrimTypes.IntBits(lp.CName) is var bits and > 0
                && (sh.Value < 0 || sh.Value >= bits))
                diag.Error(Codes.BadShiftCount, ctx.File, be.Right.Span,
                    $"shift count {sh.Value} is out of range for '{Describe(left.Type)}' ({bits} bits)",
                    [sh.Value < 0
                        ? "a negative shift count is undefined behaviour"
                        : $"the count must be between 0 and {bits - 1}"]);
            IrType bt = be.Op is BinOp.Shl or BinOp.Shr ? left.Type
                      : NumRank(left.Type) >= NumRank(right.Type) ? left.Type : right.Type;
            return new IrBinOp(be.Op, left, right, bt);
        }

        if (!(IsArith(left.Type) && IsArith(right.Type)))
            diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                $"operator '{be.Op.Sym()}' cannot be applied to '{Describe(left.Type)}' and '{Describe(right.Type)}'");

        // C's '%' is defined on integers only - there is no floating-point remainder
        // operator - so a double operand lowers to C that the compiler rejects outright.
        if (be.Op == BinOp.Mod && IsArith(left.Type) && IsArith(right.Type)
            && !(IsInteger(left.Type) && IsInteger(right.Type)))
            diag.Error(Codes.TypeMismatch, ctx.File, be.Span,
                $"operator '%' requires integer operands, got '{Describe(left.Type)}' and '{Describe(right.Type)}'",
                ["for a floating-point remainder, call the library's Math function instead"]);

        // An integer divisor that is literally zero traps at runtime on every target, so
        // there is no program for which this is correct. Reject it at compile time.
        if (be.Op is BinOp.Div or BinOp.Mod && IsInteger(right.Type) && right is IrLitInt { Value: 0 })
            diag.Error(Codes.DivisionByZero, ctx.File, be.Right.Span,
                $"integer {(be.Op == BinOp.Div ? "division" : "remainder")} by a literal zero",
                ["this traps at runtime; guard the divisor or use a non-zero constant"]);
        IrType t = NumRank(left.Type) >= NumRank(right.Type) ? left.Type : right.Type;
        return new IrBinOp(be.Op, left, right, t);
    }

    /// <summary>
    /// Parses an integer literal lexeme into its bit pattern, IR type, and optional verbatim C
    /// text. Returns false when the magnitude does not fit in 64 bits.
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
    /// Infers a field's type from its initializer. Fields register their type before any body is
    /// resolved, so this is limited to literals, optionally under a unary minus - the only
    /// initializers knowable without resolving expressions.
    /// </summary>
    internal static TypeSpec? InferFieldTypeSpec(Expr? init)
    {
        return init switch
        {
            IntLitExpr il => TryParseIntLit(il.Value, out _, out var it, out _) ? new NamedSpec(((IrPrimType)it).CName, il.Span) : null,
            FloatLitExpr fl => new NamedSpec(FloatLitType(fl.Value).CName, fl.Span),
            BoolLitExpr b => new NamedSpec("bool", b.Span),
            CharLitExpr c => new NamedSpec("char", c.Span),
            StrLitExpr sl => new NamedSpec(BuiltinTypes.String, sl.Span),
            UnaryExpr { Op: UnOp.Neg, Operand: IntLitExpr or FloatLitExpr } u => InferFieldTypeSpec(u.Operand),
            _ => null
        };
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

        var local = ctx.Locals.Lookup(name);
        if (local != null) return new IrVar(name, local, ctx.Locals.IsRef(name));

        if (ClassInScope(name)) return new IrVar(name, new IrClassRef(name));

        var fsym = sym.LookupFreeFunc(name);
        if (fsym != null && FuncInScope(fsym))
        {
            if (sym.IsOverloadedFunc(name))
            {
                diag.Error(Codes.AmbiguousOverload, ctx.File, ie.Span,
                    $"cannot take the address of overloaded function '{name}'");
                return Poison(ie.Span);
            }
            if (fsym.Sig!.IsEntry)
            {
                diag.Error(Codes.CallToEntry, ctx.File, ie.Span,
                    $"'{name}' is an entry point and cannot be used as a value");
                return Poison(ie.Span);
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

        string? msg =
            sym.IsField(ctx.CurClass, name)
                ? ctx.InStatic
                    ? $"'{name}' is an instance field and cannot be used in a static context"
                    : $"'{name}' is a field; write 'self.{name}'"
                : sym.IsClass(name)
                    ? $"'{Mangler.DisplayName(name)}' is not in scope; import its module"
                    : ReportNotVisible("name", name, ctx.File, ie.Span) ? null
                    : $"'{name}' is not defined";
        if (msg != null) diag.Error(Codes.UndefinedVariable, ctx.File, ie.Span, msg);
        return new IrVar(name, IrType.Error);
    }

    /// <summary>
    /// Resolves a member access expression, handling enum constants and class field loads.
    /// </summary>
    private IrExpr ResolveMemberAccess(MemberAccessExpr ma, ResolveCtx ctx)
    {
        // Enum member access: Color.Red.
        if (ma.Object is IdentExpr eid && sym.IsEnum(eid.Name) && ctx.Locals.Lookup(eid.Name) == null)
        {
            if (!sym.IsEnumMember(eid.Name, ma.Member))
                diag.Error(Codes.UndefinedVariable, ctx.File, ma.Span,
                    $"enum '{eid.Name}' has no member '{ma.Member}'");
            return new IrEnumConst(eid.Name, ma.Member) { Span = ma.Span };
        }

        if (ma.Object is IdentExpr uid && sym.IsUnion(uid.Name) && ctx.Locals.Lookup(uid.Name) == null)
        {
            var variants = sym.UnionDef(uid.Name)!;
            bool known = variants.Exists(v => v.Name == ma.Member);
            if (known)
                diag.Error(Codes.UndefinedVariable, ctx.File, ma.Span,
                    $"'{uid.Name}.{ma.Member}' is a union variant, not a field",
                    [$"construct it by calling it: '{uid.Name}.{ma.Member}()'"]);
            else
                diag.Error(Codes.UndefinedVariable, ctx.File, ma.Span,
                    $"union '{uid.Name}' has no variant '{ma.Member}'");

            return new IrUnionConstruct(new IrUnionType(uid.Name), 0, []) { Span = ma.Span };
        }

        var obj = ResolveExpr(ma.Object, ctx);
        if (obj.Type.IsError) return Poison(ma.Span);
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
            else if (_notVisible.Contains((ctx.File, cls))) return Poison(ma.Span);
            else if (!HasOpaqueFields(cls))
                diag.Error(Codes.UndefinedVariable, ctx.File, ma.Span,
                    $"'{Mangler.DisplayName(cls)}' has no field '{ma.Member}'");
        }
        else
        {
            diag.Error(Codes.UndefinedVariable, ctx.File, ma.Span,
                $"'{Describe(obj.Type)}' has no member '{ma.Member}'; only class types have fields");
            return Poison(ma.Span);
        }
        return new IrFieldLoad(obj, ma.Member, fieldType);
    }

    /// <summary>
    /// Coerces each resolved argument to its declared parameter type and validates ref/non-ref
    /// passing.
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
                if (args[i].Type != pt && !args[i].Type.IsError)
                    diag.Error(Codes.RefArgMismatch, ctx.File, astArgs[i].Span,
                        $"'ref' argument {i + 1} must be exactly '{Describe(pt)}', got '{Describe(args[i].Type)}'",
                        ["a 'ref' parameter takes the variable's address, so no conversion can apply"]);
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
        var argCtx = ctx.CatchWrapped || ctx.Expected != null
            ? ctx with { CatchWrapped = false, Expected = null }
            : ctx;
        var args = new List<IrExpr>(ce.Args.Length);
        for (int i = 0; i < ce.Args.Length; i++)
        {
            var a = ce.Args[i];
            args.Add(ResolveExpr(a is RefArgExpr ra ? ra.Target : a, argCtx));
        }

        if (ce.Callee is MemberAccessExpr { Object: GenericTypeRefExpr gt } gma
            && Mangler.IsGenericTemplate(gt.Name)
            && (gt.IndexForm == null || ctx.Locals.Lookup(gt.Name) == null))
        {
            if (sym.IsUnion(gt.Mangled))
                return ResolveUnionConstruct(gt.Mangled, gma.Member, args, ctx, ce.Span);

            if (ClassInScope(gt.Mangled))
                return ResolveCall(
                    ce with { Callee = new MemberAccessExpr(new IdentExpr(gt.Mangled, gt.Span), gma.Member, gma.Span) },
                    ctx);

            diag.Error(Codes.UndefinedType, ctx.File, gt.Span,
                $"'{gt.Written}' names no union or class, so it has no '{gma.Member}'");
            return Poison(gt.Span);
        }

        // member access call: obj.Method(args) or ClassName.StaticMethod(args)
        if (ce.Callee is MemberAccessExpr ma)
        {
            string objName = ma.Object is IdentExpr oid ? oid.Name : "";

            if (!string.IsNullOrEmpty(objName) && sym.IsUnion(objName) && ctx.Locals.Lookup(objName) == null)
                return ResolveUnionConstruct(objName, ma.Member, args, ctx, ce.Span);

            if (!string.IsNullOrEmpty(objName) && ctx.Locals.Lookup(objName) == null
                && ResolveGenericUnionConstruct(objName, ma.Member, args, ctx, ce.Span) is { } gu)
                return gu;

            if (!string.IsNullOrEmpty(objName) && ClassInScope(objName.AsSpan()) && ctx.Locals.Lookup(objName) == null)
            {
                if (_methodTemplates.TryGetValue(new MemberKey(objName, ma.Member), out var mtmpl))
                {
                    bool tIsStatic = sym.LookupMethod(objName, ma.Member)?.Sig?.IsStatic ?? true;
                    if (!tIsStatic)
                        diag.Error(Codes.StaticOnInstance, ctx.File, ce.Span,
                            $"'{Mangler.DisplayName(objName)}.{ma.Member}' is an instance method; call it on a value");
                    CheckMemberAccess(objName, ma.Member, ctx, ce.Span);
                    return ResolveGenericMethodCall(mtmpl, objName, tIsStatic, args, ctx, ce.Span, null, ce.Args);
                }
                var msym = sym.LookupMethod(objName, ma.Member);
                if (msym == null)
                {
                    if (!IsOpaqueStruct(objName))
                    {
                        diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                            $"'{Mangler.DisplayName(objName)}' has no method '{ma.Member}'",
                            Suggest.Hints(ma.Member, sym.MethodNames(objName)));
                        return Poison(ce.Span);
                    }
                }
                else if (msym.Sig is { IsStatic: false })
                    diag.Error(Codes.StaticOnInstance, ctx.File, ce.Span,
                        $"'{Mangler.DisplayName(objName)}.{ma.Member}' is an instance method; call it on a value");
                CheckMemberAccess(objName, ma.Member, ctx, ce.Span);
                return BuildCall(sym.MethodOverloads(objName, ma.Member), msym, args,
                    $"{Mangler.DisplayName(objName)}.{ma.Member}",
                    Mangler.Method(objName, ma.Member, [], false), null, ctx, ce);
            }

            if (!string.IsNullOrEmpty(objName) && ctx.Locals.Lookup(objName) == null && !ClassInScope(objName.AsSpan())
                && TryResolveFileNamespacedCall(objName, ma.Member, args, ctx, ce) is { } nsCall)
                return nsCall;

            var recv = ResolveExpr(ma.Object, ctx);
            string? cls = ClassNameOf(recv.Type);
            if (cls != null)
            {
                if (_methodTemplates.TryGetValue(new MemberKey(cls, ma.Member), out var imtmpl))
                {
                    bool iIsStatic = sym.LookupMethod(cls, ma.Member)?.Sig?.IsStatic ?? false;
                    if (iIsStatic)
                        diag.Error(Codes.InstanceOnStatic, ctx.File, ce.Span,
                            $"'{Mangler.DisplayName(cls)}.{ma.Member}' is static; call it as '{Mangler.DisplayName(cls)}.{ma.Member}(...)'");
                    CheckMemberAccess(cls, ma.Member, ctx, ce.Span);
                    return ResolveGenericMethodCall(imtmpl, cls, iIsStatic, args, ctx, ce.Span, recv, ce.Args);
                }
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
                    {
                        diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                            $"'{Mangler.DisplayName(cls)}' has no method '{ma.Member}'",
                            Suggest.Hints(ma.Member, sym.MethodNames(cls)));
                        return Poison(ce.Span);
                    }
                }
                else if (msym.Sig is { IsStatic: true })
                    diag.Error(Codes.InstanceOnStatic, ctx.File, ce.Span,
                        $"'{Mangler.DisplayName(cls)}.{ma.Member}' is static; call it as '{Mangler.DisplayName(cls)}.{ma.Member}(...)'");
                CheckMemberAccess(cls, ma.Member, ctx, ce.Span);
                return BuildCall(sym.MethodOverloads(cls, ma.Member), msym, args,
                    $"{Mangler.DisplayName(cls)}.{ma.Member}",
                    Mangler.Method(cls, ma.Member, [], false), recv, ctx, ce);
            }
            if (recv.Type.IsError) return Poison(ce.Span);
            diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                $"cannot call '{ma.Member}' on '{Describe(recv.Type)}'");
            return new IrInstanceCall(recv, Mangler.FreeFunc(ma.Member, [], false, false, false), IrType.Error, args);
        }

        // bare call: name(args)
        if (ce.Callee is IdentExpr id)
        {
            // local variable holding a function pointer shadows any free function of the same name
            var calleeLocal = ctx.Locals.Lookup(id.Name);
            if (calleeLocal is IrFuncPtrType localFp)
                return ResolveIndirectCallArgs(new IrVar(id.Name, localFp, ctx.Locals.IsRef(id.Name)), localFp, args, ctx, ce.Span, ce.Args);

            if (TryResolveArcIntrinsic(id.Name, args, ctx, ce.Span) is { } arc) return arc;

            if (ResolveFuncTemplate(id.Name, ctx.File, out var collidingFiles) is { } tmpl)
            {
                if (collidingFiles.Count > 1)
                {
                    diag.Error(Codes.AmbiguousCall, ctx.File, ce.Span,
                        $"'{id.Name}' is ambiguous: a public generic function named '{id.Name}' is declared in more than one imported file ({string.Join(", ", collidingFiles.Select(f => Path.GetFileNameWithoutExtension(f)))}); qualify with '<FileName>.{id.Name}(...)'");
                    return new IrStaticCall(Mangler.FreeFunc(id.Name, [], false, false, false), IrType.Void, args);
                }

                // Peek at the lower precedence candidates a bare call would otherwise reach, so a
                // generic template can't silently shadow something equally plausible.
                var otherPf = sym.LookupPrivateFunc(ctx.File, id.Name);
                var otherFsym = sym.LookupFreeFunc(id.Name);
                bool otherFsymInScope = FuncInScope(otherFsym);
                bool hasMethodCandidate = !string.IsNullOrEmpty(ctx.CurClass) &&
                    (sym.LookupMethod(ctx.CurClass, id.Name) != null || _methodTemplates.ContainsKey(new MemberKey(ctx.CurClass, id.Name)));

                if (otherPf != null || otherFsymInScope || hasMethodCandidate)
                {
                    string otherDesc = otherPf != null ? $"a private free function in '{Path.GetFileNameWithoutExtension(ctx.File)}'"
                        : otherFsymInScope ? $"a free function in '{Path.GetFileNameWithoutExtension(otherFsym!.Module)}'"
                        : $"a method of '{Mangler.DisplayName(ctx.CurClass)}'";
                    diag.Error(Codes.AmbiguousCall, ctx.File, ce.Span,
                        $"'{id.Name}' is ambiguous between the generic function declared in '{Path.GetFileNameWithoutExtension(tmpl.File)}' and {otherDesc}; qualify with '{Path.GetFileNameWithoutExtension(tmpl.File)}.{id.Name}(...)'" +
                        (hasMethodCandidate ? $", 'self.{id.Name}(...)', or '{Mangler.DisplayName(ctx.CurClass)}.{id.Name}(...)' as appropriate" : ""));
                    return new IrStaticCall(Mangler.FreeFunc(id.Name, [], false, false, false), IrType.Void, args);
                }

                return ResolveGenericCall(tmpl, args, ctx, ce.Span, ce.Args);
            }

            // file local private free functions take priority over globals
            var pfsym = sym.LookupPrivateFunc(ctx.File, id.Name);
            if (pfsym != null)
            {
                return BuildCall(sym.PrivateFuncOverloads(ctx.File, id.Name), pfsym, args, id.Name,
                    Mangler.PrivateFreeFunc(Mangler.FileToken(ctx.File), id.Name, [], false), null, ctx, ce);
            }

            var fsym = sym.LookupFreeFunc(id.Name);
            if (fsym != null && FuncInScope(fsym))
            {
                if (fsym.Sig?.IsEntry == true)
                    diag.Error(Codes.CallToEntry, ctx.File, ce.Span,
                        $"'{id.Name}' is an entry point and cannot be called directly");
                return BuildCall(sym.FuncOverloads(id.Name), fsym, args, id.Name,
                    Mangler.FreeFunc(id.Name, [], false, false, false), null, ctx, ce);
            }

            // sibling method of the current class
            if (!string.IsNullOrEmpty(ctx.CurClass))
            {
                if (_methodTemplates.TryGetValue(new MemberKey(ctx.CurClass, id.Name), out var smtmpl))
                {
                    bool sIsStatic = sym.LookupMethod(ctx.CurClass, id.Name)?.Sig?.IsStatic ?? true;
                    if (!sIsStatic)
                    {
                        diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                            $"'{id.Name}' is an instance method; call it as 'self.{id.Name}(...)'");
                        return ResolveGenericMethodCall(smtmpl, ctx.CurClass, false, args, ctx, ce.Span, new IrSelfExpr(ctx.CurClass), ce.Args);
                    }
                    return ResolveGenericMethodCall(smtmpl, ctx.CurClass, true, args, ctx, ce.Span, null, ce.Args);
                }
                var msym = sym.LookupMethod(ctx.CurClass, id.Name);
                if (msym != null)
                {
                    bool isStatic = msym.Sig?.IsStatic ?? false;
                    if (!isStatic)
                    {
                        var ichosen = ChooseOverload(sym.MethodOverloads(ctx.CurClass, id.Name), msym, args,
                            $"{Mangler.DisplayName(ctx.CurClass)}.{id.Name}", ctx, ce.Span);
                        diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span,
                            $"'{id.Name}' is an instance method; call it as 'self.{id.Name}(...)'");
                        CoerceArgs(args, ichosen?.Sig, ctx, ce.Args);
                        return new IrInstanceCall(new IrSelfExpr(ctx.CurClass),
                            ichosen?.CName ?? Mangler.Method(ctx.CurClass, id.Name, [], false),
                            ichosen != null ? ResolveType(ichosen.Type) : IrType.Void, args);
                    }
                    return BuildCall(sym.MethodOverloads(ctx.CurClass, id.Name), msym, args,
                        $"{Mangler.DisplayName(ctx.CurClass)}.{id.Name}",
                        Mangler.Method(ctx.CurClass, id.Name, [], false), null, ctx, ce);
                }
            }

            if (sym.LookupFreeFunc(id.Name) != null)
                diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span, $"'{id.Name}' is not in scope; import its module");
            else if (!ReportNotVisible("function", id.Name, ctx.File, ce.Span)
                     && !ReportWrongKind(Codes.UndefinedMethod, "a function", id.Name, ctx.File, ce.Span))
                diag.Error(Codes.UndefinedMethod, ctx.File, ce.Span, $"call to undefined function '{id.Name}'");
            return new IrStaticCall(Mangler.FreeFunc(id.Name, [], false, false, false), IrType.Error, args);
        }

        // indirect call through any other expression
        var calleeExpr = ResolveExpr(ce.Callee, ctx);
        if (calleeExpr.Type is IrFuncPtrType gfp)
            return ResolveIndirectCallArgs(calleeExpr, gfp, args, ctx, ce.Span, ce.Args);
        if (calleeExpr.Type.IsError) return Poison(ce.Span);
        diag.Error(Codes.TypeMismatch, ctx.File, ce.Span, "callee expression is not callable");
        return new IrLitInt(0);
    }

    /// <summary>
    /// Resolves an indirect function-pointer call, checking argument count and types against the
    /// pointer's signature.
    /// </summary>
    private IrIndirectCall ResolveIndirectCallArgs(IrExpr target, IrFuncPtrType fpt, List<IrExpr> args, ResolveCtx ctx,
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
    /// Requires an integer subscript for a fixed-array or pointer index. Only the operator-'[]'
    /// path checks its index; raw indexing lowers straight to C "a[i]", so a bool or a class
    /// reference used to reach the C compiler as a subscript.
    /// </summary>
    private void CheckIndexIsInteger(IrExpr idx, ResolveCtx ctx, TextSpan span)
    {
        if (IsInteger(idx.Type) || idx.Type is IrEnumType) return;
        diag.Error(Codes.TypeMismatch, ctx.File, span,
            $"index must be an integer, got '{Describe(idx.Type)}'");
    }

    /// <summary>
    /// Resolves an index expression, dispatching to the class operator [] overload, fixed-array
    /// element access, or unsafe pointer indexing.
    /// </summary>
    private IrExpr ResolveIndex(IndexExpr ix, ResolveCtx ctx)
    {
        var obj = ResolveExpr(ix.Object, ctx);
        var idx = ResolveExpr(ix.Index, ctx);
        if (obj.Type.IsError || idx.Type.IsError) return Poison(ix.Span);
        if (obj.Type is IrClassRef icr && sym.LookupOperator(icr.ClassName, "[]") is { Sig.Params: [_] } getOp)
        {
            CheckOperatorAccess(icr.ClassName, "[]", ctx, ix.Span);
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
        CheckIndexIsInteger(idx, ctx, ix.Index.Span);
        return new IrIndex(obj, idx, elem);
    }

    /// <summary>
    /// Resolves an indexed assignment, handling operator []= overloads with compound assignment
    /// hoisting, and plain fixed-array or pointer index targets.
    /// </summary>
    private IrStmt ResolveIndexAssign(IndexExpr ixt, AssignStmt asgn, ResolveCtx ctx)
    {
        var obj = ResolveExpr(ixt.Object, ctx);
        var idx = ResolveExpr(ixt.Index, ctx);

        if (obj.Type is IrClassRef cr && sym.LookupOperator(cr.ClassName, "[]=") is { Sig.Params: [_, _] } setOp)
        {
            CheckOperatorAccess(cr.ClassName, "[]=", ctx, asgn.Span);
            var idxType = ResolveType(setOp.Sig!.Params[0].Type);
            var valType = ResolveType(setOp.Sig!.Params[1].Type);
            idx = Coerce(idx, idxType, ctx);
            CheckAssign(idx, idxType, "the index", ctx, Codes.TypeMismatch);
            if (asgn.Op == AssignOp.Assign)
            {
                var value = Coerce(ResolveExpr(asgn.Value, ctx), valType, ctx);
                CheckAssign(value, valType, "the assignment target", ctx, Codes.TypeMismatch);
                ForbidThrowsInAssignForm(value, "an index assignment through a '[]=' operator", ctx);
                ForbidNestedThrows(value, ctx, allowRoot: false);
                return new IrExprStmt(new IrInstanceCall(obj, setOp.CName, IrType.Void, [idx, value])) { Span = asgn.Span };
            }
            var stmts = new List<IrStmt>();
            var objRef = HoistIfImpure(obj, "_ixo", stmts);
            var idxRef = HoistIfImpure(idx, "_ixi", stmts);
            var getOp = sym.LookupOperator(cr.ClassName, "[]");
            IrExpr current;
            if (getOp != null)
            {
                CheckOperatorAccess(cr.ClassName, "[]", ctx, ixt.Span);
                current = new IrInstanceCall(objRef, getOp.CName, ResolveType(getOp.Type), [idxRef]) { Span = ixt.Span };
            }
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
            if (elemClass != null && sym.LookupOperator(elemClass, baseOp.Sym(), 1) is { } elemOp)
            {
                CheckOperatorAccess(elemClass, baseOp.Sym(), ctx, asgn.Span);
                rhs = CheckOpArg(elemOp, rhs, ctx);
                combined = new IrStaticCall(elemOp.CName, ResolveType(elemOp.Type), [current, rhs]);
            }
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
        CheckIndexIsInteger(idx, ctx, ixt.Index.Span);
        var val = ResolveExpr(asgn.Value, ctx);
        if (asgn.Op == AssignOp.Assign)
        {
            var target = new IrIndex(obj, idx, elem) { Span = ixt.Span };
            val = CheckRootThrowsValue(val, target.Type, "the assignment target", ctx, asgn.Span);
            return new IrAssign(target, AssignOp.Assign, val);
        }
        string elemBaseOp = asgn.Op.BaseOp()!.Value.Sym();
        if (ClassNameOf(elem) is { } elemClass2 && sym.LookupOperator(elemClass2, elemBaseOp, 1) is { } elemOp2)
        {
            CheckOperatorAccess(elemClass2, elemBaseOp, ctx, asgn.Span);
            val = CheckOpArg(elemOp2, val, ctx);
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
    /// Resolves a new expression, validating the type is a class in scope and checking the
    /// constructor argument count. Handles collection initializers via ResolveCollectionInit.
    /// </summary>
    private IrExpr ResolveNew(NewExpr ne, ResolveCtx ctx)
    {
        var args = new List<IrExpr>(ne.Args.Length);
        for (int i = 0; i < ne.Args.Length; i++)
        {
            var a = ne.Args[i];
            args.Add(ResolveExpr(a is RefArgExpr ra ? ra.Target : a, ctx));
        }

        string typeName = ne.Type.ToSpecString();
        if (typeName == NamedSpec.Poison) return Poison(ne.Span);
        if (Mangler.GenericFailed(typeName)) return Poison(ne.Span);
        if (sym.Modules.Contains(typeName))
        {
            diag.Error(Codes.NewOnNonClass, ctx.File, ne.Span,
                $"'{Mangler.DisplayName(typeName)}' is a module and cannot be instantiated",
                ["a module has no instances; call its members directly, as " +
                 $"'{Mangler.DisplayName(typeName)}.Member(...)'"]);
            return Poison(ne.Span);
        }
        if (!ClassInScope(typeName))
        {
            string shown = Mangler.DisplayName(typeName);
            if (sym.IsClass(typeName))
                diag.Error(Codes.NewOnNonClass, ctx.File, ne.Span,
                    $"'{shown}' is not in scope; import its module");
            else if (SymbolTable.Primitives.Contains(typeName))
                diag.Error(Codes.NewOnNonClass, ctx.File, ne.Span,
                    $"'{shown}' is a primitive; use 'let', not 'new'");
            else if (sym.IsUnion(typeName))
                diag.Error(Codes.NewOnNonClass, ctx.File, ne.Span,
                    $"'{shown}' is a union and cannot be instantiated with 'new'",
                    [$"a union value is one of its variants; construct one by calling it, as '{shown}.Variant(...)'"]);
            else if (sym.IsEnum(typeName))
                diag.Error(Codes.NewOnNonClass, ctx.File, ne.Span,
                    $"'{shown}' is an enum and cannot be instantiated with 'new'",
                    [$"an enum value is one of its members; name one directly, as '{shown}.Member'"]);
            else if (!ReportNotVisible("type", ne.Type is NamedSpec nn ? nn.Name : typeName, ctx.File, ne.Span)
                     && !ReportWrongKind(Codes.NewOnNonClass,
                                         ne.Type is NamedSpec { Args.Length: > 0 } ? "a generic type" : "a type",
                                         ne.Type is NamedSpec nk ? nk.Name : typeName, ctx.File, ne.Span))
                diag.Error(Codes.NewOnNonClass, ctx.File, ne.Span, $"'{shown}' is not a class");
            return Poison(ne.Span);
        }
        var init = sym.LookupMethod(typeName, Lifecycle.Init);
        if (init?.Sig is { } isig && isig.Params.Count > 0)
        {
            CheckArgCount(isig, args.Count, $"{Mangler.DisplayName(typeName)} constructor", ctx, ne.Span);
            CoerceArgs(args, isig, ctx, ne.Args);
        }
        else if (args.Count > 0)
            diag.Error(Codes.WrongArgCount, ctx.File, ne.Span,
                $"'{Mangler.DisplayName(typeName)}' has no constructor taking arguments");
        if (ne.CollectionInit.Length > 0)
            return ResolveCollectionInit(ne, typeName, args, ctx);
        return new IrNew(typeName, args);
    }

    /// <summary>
    /// Resolves a collection initializer by looking up an Add method and coercing each element.
    /// </summary>
    private IrExpr ResolveCollectionInit(NewExpr ne, string typeName, List<IrExpr> ctorArgs, ResolveCtx ctx)
    {
        var add = sym.LookupMethod(typeName, "Add");
        if (add?.Sig == null)
        {
            diag.Error(Codes.UndefinedMethod, ctx.File, ne.Span,
                $"'{Mangler.DisplayName(typeName)}' has no 'Add' method for a collection initializer");
            return new IrNew(typeName, ctorArgs);
        }
        if (add.Sig.Params.Count != 1)
        {
            diag.Error(Codes.WrongArgCount, ctx.File, ne.Span,
                $"'{Mangler.DisplayName(typeName)}.Add' must take exactly one argument to be used in a collection initializer");
            return new IrNew(typeName, ctorArgs);
        }
        var elemType = ResolveType(add.Sig.Params[0].Type);
        var inits = new List<IrExpr>(ne.CollectionInit.Length);
        foreach (var el in ne.CollectionInit)
        {
            var r = Coerce(ResolveExpr(el, ctx), elemType, ctx);
            CheckAssign(r, elemType, $"a '{Mangler.DisplayName(typeName)}' element", ctx, Codes.ArgTypeMismatch);
            ForbidNestedThrows(r, ctx, allowRoot: false);
            inits.Add(r);
        }
        return new IrNewInit(typeName, ctorArgs, add.CName, inits);
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
    /// Settles a 'Name[Args]' the parser could not: an index if the name denotes a value or nothing
    /// at all, a generic type reference otherwise. Only the index path knows about fields needing
    /// 'self.' and near-miss spellings, which are far commoner.
    /// </summary>
    private IrExpr ResolveGenericTypeRef(GenericTypeRefExpr g, ResolveCtx ctx)
    {
        bool isTemplate = Mangler.IsGenericTemplate(g.Name);
        bool namesType = isTemplate || sym.IsUnion(g.Mangled) || sym.IsClass(g.Mangled);
        if (g.IndexForm != null && (!namesType || ctx.Locals.Lookup(g.Name) != null))
            return ResolveIndex(new IndexExpr(new IdentExpr(g.Name, g.Span), g.IndexForm, g.Span), ctx);

        if (isTemplate)
            diag.Error(Codes.TypeMismatch, ctx.File, g.Span,
                $"'{g.Written}' is a type, not a value",
                [$"to build one of its variants, call it: '{g.Written}.SomeVariant(...)'"]);
        else if (sym.IsUnion(g.Name) || sym.IsClass(g.Name) || sym.IsEnum(g.Name))
            diag.Error(Codes.TypeMismatch, ctx.File, g.Span,
                $"'{g.Name}' is not generic, so it takes no type arguments");
        else
            diag.Error(Codes.UndefinedType, ctx.File, g.Span, $"unknown generic type '{g.Name}'");

        return Poison(g.Span);
    }

    /// <summary>
    /// Resolves 'Maybe.Found(7)' by choosing which stamped instance is meant: the arguments where
    /// they single one out, otherwise the expected type from the enclosing let or return. Null if
    /// the name is not a generic union, so the caller falls through.
    /// </summary>
    private IrExpr? ResolveGenericUnionConstruct(
        string baseName, string variant, List<IrExpr> args, ResolveCtx ctx, TextSpan span)
    {
        var instances = new List<string>();
        foreach (var inst in Mangler.InstancesOf(baseName))
            if (sym.IsUnion(inst)) instances.Add(inst);

        if (instances.Count == 0)
        {
            if (Mangler.IsGenericTemplate(baseName))
            {
                diag.Error(Codes.CannotInfer, ctx.File, span,
                    $"generic '{baseName}' is never instantiated, so '{baseName}.{variant}' has no type",
                    [$"name the type somewhere first, e.g. 'let {baseName}[int] x = {baseName}.{variant}(...);'"]);
                return new IrUnionConstruct(new IrUnionType(baseName), 0, args);
            }
            return null;
        }

        // Only instances that actually declare this variant with a matching arity are candidates.
        var candidates = new List<string>();
        foreach (var inst in instances)
        {
            var variants = sym.UnionDef(inst);
            if (variants == null) continue;
            int idx = variants.FindIndex(v => v.Name == variant);
            if (idx >= 0 && variants[idx].Fields.Length == args.Count) candidates.Add(inst);
        }

        if (candidates.Count == 0)
        {
            diag.Error(Codes.UndefinedVariable, ctx.File, span,
                $"no instantiation of generic union '{baseName}' has a variant '{variant}' " +
                $"taking {args.Count} argument(s)",
                [$"instantiated as: {string.Join(", ", instances.Select(Mangler.DisplayName))}"]);
            return new IrUnionConstruct(new IrUnionType(instances[0]), 0, args);
        }

        // The arguments decide it when exactly one candidate accepts them all.
        var accepting = new List<string>();
        foreach (var inst in candidates)
        {
            var fields = sym.UnionDef(inst)!.Find(v => v.Name == variant)!.Fields;
            bool ok = true;
            for (int i = 0; i < args.Count && ok; i++)
                ok = Assignable(args[i], ResolveType(fields[i].Type));
            if (ok) accepting.Add(inst);
        }

        string? chosen = accepting.Count == 1 ? accepting[0] : null;

        // Otherwise the expected type, when it names an instantiation of this same generic.
        if (chosen == null && ctx.Expected is IrUnionType want
            && (accepting.Count == 0 ? candidates : accepting).Contains(want.Name))
            chosen = want.Name;

        if (chosen == null)
        {
            var shown = (accepting.Count > 0 ? accepting : candidates).Select(Mangler.DisplayName);
            diag.Error(Codes.CannotInfer, ctx.File, span,
                $"cannot tell which instantiation of '{baseName}' this means",
                [
                    $"it could be: {string.Join(", ", shown)}",
                    $"give the target an explicit type, e.g. 'let {Mangler.DisplayName(candidates[0])} x = " +
                    $"{baseName}.{variant}(...);'",
                ]);
            chosen = candidates[0];
        }

        return ResolveUnionConstruct(chosen, variant, args, ctx, span);
    }

    /// <summary>
    /// Resolves a union variant construction call, validating the variant name and coercing each
    /// argument to its declared field type.
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
