namespace Appa;

/// <summary>
/// ARC lowering pass. Rewrites each function body into flat IR with ownership made
/// explicit - owning locals are released at scope exit, return, break, and throw.
/// After this pass the emitter prints the IR with no ARC logic of its own.
/// </summary>
sealed class Ownership(IrModule module)
{
    readonly HashSet<string> _managed =
        module.Classes.Where(c => !c.IsModule).Select(c => c.Name).ToHashSet();

    readonly string _retain = Role(module, Roles.Retain);
    readonly string _release = Role(module, Roles.Release);

    static string Role(IrModule m, string role) =>
        m.Symbols.IntrinsicOrNull(role) ?? $"gata_MISSING_{role}";

    bool IsManaged(IrType t) => t is IrClassRef cr && _managed.Contains(cr.ClassName);

    bool IsProducer(IrExpr e) => e switch
    {
        IrNew or IrNewInit or IrLitString => true,
        IrCast c => IsProducer(c.Value),
        IrStaticCall sc when IsManaged(sc.Type) => true,
        IrInstanceCall ic when IsManaged(ic.Type) => true,
        IrIndirectCall ic when IsManaged(ic.Type) => true,
        IrTernary t when IsManaged(t.Type) => true,
        _ => false
    };

    int _seq;
    string Tmp(string prefix) => $"{prefix}{_seq++}";

    sealed class Frame { public List<(string Name, IrType Type)> Owners = []; public List<IrStmt> Defers = []; public bool Loop; public bool Try; }
    readonly Stack<Frame> _frames = new();
    bool _nextFrameIsLoop;

    bool _inTry;
    string _catchLabel = "";
    bool _inThrowsFunc;
    IrResultType? _resultType;

    // Inside `unsafe`, automatic reference counting is suppressed: owning stores,
    // owner tracking, consume-retains and producer hoisting all step aside, so the
    // author manages element lifetimes by hand via retain/release. Exits (return /
    // break / throw) still release owners from the enclosing safe frames.
    bool _inUnsafe;

    #region Entry

    /// <summary>
    /// Runs the ARC pass over an entire module, lowering all functions.
    /// </summary>
    public IrModule Run() => module with
    {
        Classes = module.Classes.Select(LowerClass).ToList(),
        FreeFunctions = module.FreeFunctions.Select(LowerFunction).ToList(),
        Processes = module.Processes.Select(LowerProcess).ToList(),
    };

    IrClass LowerClass(IrClass c) => c with
    {
        Methods = c.Methods.Select(LowerFunction).ToList(),
        Operators = c.Operators.Select(LowerOperator).ToList(),
    };

    IrProcess LowerProcess(IrProcess p) => p with
    {
        Threads = p.Threads.Select(t => t.EntryFunc == null ? t : t with { EntryFunc = LowerFunction(t.EntryFunc) }).ToList()
    };

    /// <summary>
    /// Lowers a single function body, tracking whether it is a throws function.
    /// </summary>
    IrFunction LowerFunction(IrFunction f)
    {
        if (f.Body == null) return f;
        var prev = (_inThrowsFunc, _resultType);
        if (f.IsThrows) { _inThrowsFunc = true; _resultType = new IrResultType(f.ReturnType); }
        var body = LowerBlock(f.Body);
        (_inThrowsFunc, _resultType) = prev;
        return f with { Body = body };
    }

    IrOperator LowerOperator(IrOperator o) => o.Body == null ? o : o with { Body = LowerBlock(o.Body) };

    #endregion

    #region Blocks and frames

    /// <summary>
    /// Pushes a new frame, lowers all statements into it, emits owner releases, then pops.
    /// </summary>
    IrBlock LowerBlock(IrBlock b)
    {
        var frame = new Frame { Loop = _nextFrameIsLoop };
        _nextFrameIsLoop = false;
        _frames.Push(frame);
        var outs = new List<IrStmt>();
        foreach (var s in b.Stmts) LowerStmt(s, outs);
        ReleaseFrame(frame, outs);
        _frames.Pop();
        return new IrBlock(outs) { Span = b.Span };
    }

    void LowerBodyInto(IrBlock b, List<IrStmt> outs)
    {
        foreach (var s in b.Stmts) LowerStmt(s, outs);
    }

    /// <summary>
    /// Splices this frame's defers (in LIFO order) then releases its owning locals.
    /// Deferred cleanup runs before owners are released so a defer can still use a local
    /// before ARC touches its refcount. Re-lowered fresh at each splice site so each
    /// occurrence gets its own hoisted-temp names.
    /// </summary>
    void ReleaseFrame(Frame f, List<IrStmt> outs)
    {
        foreach (var action in f.Defers) LowerStmt(action, outs);
        for (int i = f.Owners.Count - 1; i >= 0; i--)
            outs.Add(ReleaseStmt(new IrVar(f.Owners[i].Name, f.Owners[i].Type)));
    }

    /// <summary>
    /// Releases all frames from innermost outward, stopping after the first frame where stopAfter returns true.
    /// Used by early-exit statements such as return, break, and throw.
    /// </summary>
    void ReleaseForExit(List<IrStmt> outs, Func<Frame, bool> stopAfter)
    {
        foreach (var f in _frames)
        {
            ReleaseFrame(f, outs);
            if (stopAfter(f)) break;
        }
    }

    /// <summary>
    /// Registers a local variable as an owner in the current frame.
    /// Skipped inside unsafe blocks where lifetimes are managed by hand.
    /// </summary>
    void RegisterOwner(string name, IrType type)
    {
        if (_inUnsafe) return;   // unsafe locals are released by hand
        if (_frames.Count > 0) _frames.Peek().Owners.Add((name, type));
    }

    IrStmt ReleaseStmt(IrExpr e) => new IrExprStmt(new IrStaticCall(_release, IrType.Void, [e]));
    IrExpr Retain(IrExpr e) => new IrStaticCall(_retain, e.Type, [e]) { Span = e.Span };

    /// <summary>
    /// Registers the unlowered defer action with the enclosing frame for splicing at every exit.
    /// Kept unlowered so each splice site re-lowers it fresh with its own temp names.
    /// Prepended for LIFO order against other defers already in the frame.
    /// </summary>
    void LowerDefer(IrDefer d)
    {
        if (_frames.Count > 0) _frames.Peek().Defers.Insert(0, d.Action);
    }

    #endregion

    #region Statements

    void LowerStmt(IrStmt s, List<IrStmt> outs)
    {
        switch (s)
        {
            case IrNativeStmt or IrRaw or IrDebug or IrPanic: outs.Add(s); break;
            case IrBlock b: outs.Add(LowerBlock(b)); break;
            case IrUnsafeBlock u:
            {
                bool prev = _inUnsafe; _inUnsafe = true;
                outs.Add(LowerBlock(u.Body));
                _inUnsafe = prev;
                break;
            }
            case IrThrow: LowerThrow(outs); break;
            case IrDeclVar dv: LowerDecl(dv, outs); break;
            case IrAssign a: LowerAssign(a, outs); break;
            case IrExprStmt es: LowerExprStmt(es, outs); break;
            case IrReturn r: LowerReturn(r, outs); break;
            case IrBreak: ReleaseForExit(outs, f => f.Loop); outs.Add(new IrBreak()); break;
            case IrContinue: ReleaseForExit(outs, f => f.Loop); outs.Add(new IrContinue()); break;
            case IrIf i: LowerIf(i, outs); break;
            case IrWhile w: LowerWhile(w, outs); break;
            case IrFor fr: LowerFor(fr, outs); break;
            case IrForIn fi: LowerForIn(fi, outs); break;
            case IrTryCatch tc: LowerTryCatch(tc, outs); break;
            case IrDefer d: LowerDefer(d); break;
            default: throw new InvalidOperationException($"[Ownership] unhandled IrStmt: {s.GetType().Name}");
        }
    }

    void LowerDecl(IrDeclVar dv, List<IrStmt> outs)
    {
        bool managed = IsManaged(dv.Type);

        if (dv.Init == null)
        {
            outs.Add(dv);   // emitter NULL/{0}-initialises managed/array locals
            if (managed) RegisterOwner(dv.Name, dv.Type);
            return;
        }

        var p = new List<IrStmt>(); var c = new List<(string, IrType)>();
        IrExpr init = managed ? Consume(dv.Init, p, c) : Flatten(dv.Init, false, p, c);
        outs.AddRange(p);
        outs.Add(new IrDeclVar(dv.Name, dv.Type, init) { Span = dv.Span });
        ReleaseAll(c, outs);
        if (managed) RegisterOwner(dv.Name, dv.Type);
    }

    void LowerAssign(IrAssign a, List<IrStmt> outs)
    {
        var p = new List<IrStmt>(); var c = new List<(string, IrType)>();
        if (a.Op == "=" && IsManaged(a.Target.Type) && !_inUnsafe)
        {
            // Owning store: release the old value, install the new (+1) one.
            IrExpr tgt = Flatten(a.Target, false, p, c);
            IrExpr val = Consume(a.Value, p, c);
            outs.AddRange(p);
            string tmp = Tmp("_asg");
            outs.Add(new IrDeclVar(tmp, a.Target.Type, val));
            outs.Add(ReleaseStmt(tgt));
            outs.Add(new IrAssign(tgt, "=", new IrVar(tmp, a.Target.Type)));
            ReleaseAll(c, outs);
            return;
        }
        IrExpr t = Flatten(a.Target, false, p, c);
        IrExpr v = Flatten(a.Value, false, p, c);
        outs.AddRange(p);
        outs.Add(new IrAssign(t, a.Op, v) { Span = a.Span });
        ReleaseAll(c, outs);
    }

    void LowerExprStmt(IrExprStmt es, List<IrStmt> outs)
    {
        var p = new List<IrStmt>(); var c = new List<(string, IrType)>();
        IrExpr e = Flatten(es.Expr, false, p, c);
        outs.AddRange(p);
        if (!(IsProducer(es.Expr) && IsManaged(es.Expr.Type)))
            outs.Add(new IrExprStmt(e) { Span = es.Span });   // hoisted producers are already released temps
        ReleaseAll(c, outs);
    }

    void LowerReturn(IrReturn rs, List<IrStmt> outs)
    {
        if (rs.Value == null)
        {
            ReleaseForExit(outs, _ => false);
            outs.Add(_inThrowsFunc ? new IrRaw($"return ({_resultType!.ToCType()}){{ .has_error = false }};") : new IrReturn(null));
            return;
        }
        var p = new List<IrStmt>(); var c = new List<(string, IrType)>();
        bool managed = IsManaged(rs.Value.Type);
        IrExpr val = managed ? Consume(rs.Value, p, c) : Flatten(rs.Value, false, p, c);
        outs.AddRange(p);
        string tmp = Tmp("_ret");
        outs.Add(new IrDeclVar(tmp, rs.Value.Type, val));
        ReleaseAll(c, outs);
        ReleaseForExit(outs, _ => false);
        outs.Add(_inThrowsFunc
            ? new IrRaw($"return ({_resultType!.ToCType()}){{ .value = {tmp}, .has_error = false }};")
            : new IrReturn(new IrVar(tmp, rs.Value.Type)));
    }

    void LowerIf(IrIf ifs, List<IrStmt> outs)
    {
        var p = new List<IrStmt>(); var c = new List<(string, IrType)>();
        IrExpr cond = Flatten(ifs.Cond, false, p, c);
        if (p.Count == 0 && c.Count == 0)
        {
            outs.Add(new IrIf(cond, LowerBlock(ifs.Then), ifs.Else == null ? null : LowerBlock(ifs.Else)) { Span = ifs.Span });
            return;
        }
        outs.AddRange(p);
        string cv = Tmp("_if");
        outs.Add(new IrDeclVar(cv, IrType.Bool, cond));
        ReleaseAll(c, outs);
        outs.Add(new IrIf(new IrVar(cv, IrType.Bool), LowerBlock(ifs.Then), ifs.Else == null ? null : LowerBlock(ifs.Else)));
    }

    void LowerWhile(IrWhile ws, List<IrStmt> outs)
    {
        var p = new List<IrStmt>(); var c = new List<(string, IrType)>();
        IrExpr cond = Flatten(ws.Cond, false, p, c);
        if (p.Count == 0 && c.Count == 0)
        {
            _nextFrameIsLoop = true;
            outs.Add(new IrWhile(cond, LowerBlock(ws.Body)) { Span = ws.Span });
            return;
        }
        // Condition allocates each iteration: re-evaluate (and release) per pass.
        var inner = new List<IrStmt>();
        inner.AddRange(p);
        string cv = Tmp("_wh");
        inner.Add(new IrDeclVar(cv, IrType.Bool, cond));
        ReleaseAll(c, inner);
        inner.Add(new IrRaw($"if (!{cv}) break;"));
        _nextFrameIsLoop = true;
        inner.Add(LowerBlock(ws.Body));
        outs.Add(new IrWhile(new IrLitBool(true), new IrBlock(inner)));
    }

    void LowerFor(IrFor fr, List<IrStmt> outs)
    {
        bool initManaged = fr.Init is IrDeclVar idv && IsManaged(idv.Type);
        var cp = new List<IrStmt>(); var cc = new List<(string, IrType)>();
        var sp = new List<IrStmt>(); var sc = new List<(string, IrType)>();
        IrExpr? cond = fr.Cond == null ? null : Flatten(fr.Cond, false, cp, cc);
        IrExpr? step = fr.Step == null ? null : Flatten(fr.Step, false, sp, sc);
        bool simple = !initManaged && cp.Count == 0 && cc.Count == 0 && sp.Count == 0 && sc.Count == 0;

        if (simple)
        {
            IrStmt? init = fr.Init switch
            {
                IrDeclVar dv => dv,
                IrAssign aa => aa,
                IrExprStmt e => e,
                _ => null
            };
            _nextFrameIsLoop = true;
            outs.Add(new IrFor(init, cond, step, LowerBlock(fr.Body)) { Span = fr.Span });
            return;
        }

        // Lowered form: the init variable spans the whole loop in its own frame. The
        // step runs at the top of each iteration after the first, so a C `continue`
        // still advances the loop instead of skipping it.
        var outer = new List<IrStmt>();
        var frame = new Frame();
        _frames.Push(frame);
        if (fr.Init != null) LowerStmt(fr.Init, outer);
        var loop = new List<IrStmt>();
        string? firstFlag = null;
        if (fr.Step != null)
        {
            firstFlag = Tmp("_first");
            outer.Add(new IrRaw($"int {firstFlag} = 1;"));
            var stepStmts = new List<IrStmt>();
            var p3 = new List<IrStmt>(); var c3 = new List<(string, IrType)>();
            IrExpr st = Flatten(fr.Step, false, p3, c3);
            stepStmts.AddRange(p3);
            stepStmts.Add(new IrExprStmt(st));
            ReleaseAll(c3, stepStmts);
            loop.Add(new IrRaw($"if (!{firstFlag})"));
            loop.Add(new IrBlock(stepStmts));
            loop.Add(new IrRaw($"{firstFlag} = 0;"));
        }
        if (fr.Cond != null)
        {
            var p2 = new List<IrStmt>(); var c2 = new List<(string, IrType)>();
            IrExpr c = Flatten(fr.Cond, false, p2, c2);
            loop.AddRange(p2);
            string cv = Tmp("_fc");
            loop.Add(new IrDeclVar(cv, IrType.Bool, c));
            ReleaseAll(c2, loop);
            loop.Add(new IrRaw($"if (!{cv}) break;"));
        }
        _nextFrameIsLoop = true;
        loop.Add(LowerBlock(fr.Body));
        outer.Add(new IrWhile(new IrLitBool(true), new IrBlock(loop)));
        ReleaseFrame(frame, outer);
        _frames.Pop();
        outs.Add(new IrBlock(outer));
    }

    void LowerForIn(IrForIn fi, List<IrStmt> outs)
    {
        var p = new List<IrStmt>(); var c = new List<(string, IrType)>();

        if (fi.ArraySize >= 0)
        {
            IrExpr acol = Flatten(fi.Collection, false, p, c);
            outs.AddRange(p);
            string av = Tmp("_arr");
            outs.Add(new IrDeclVar(av, fi.Collection.Type, acol));
            ReleaseAll(c, outs);
            outs.Add(new IrRaw($"for (int _fi = 0; _fi < {fi.ArraySize}; _fi++)"));
            var body = new List<IrStmt>();
            var frame = new Frame { Loop = true };
            _frames.Push(frame);
            string elem = $"({av})._[_fi]";
            if (IsManaged(fi.ElemType))
            {
                body.Add(new IrRaw($"{fi.ElemType.ToCType()} {fi.Var} = {_retain}({elem});"));
                RegisterOwner(fi.Var, fi.ElemType);
            }
            else body.Add(new IrRaw($"{fi.ElemType.ToCType()} {fi.Var} = {elem};"));
            LowerBodyInto(fi.Body, body);
            ReleaseFrame(frame, body);
            _frames.Pop();
            outs.Add(new IrBlock(body));
            return;
        }

        IrExpr col = Consume(fi.Collection, p, c);
        outs.AddRange(p);
        bool colManaged = IsManaged(fi.Collection.Type);
        string cv = Tmp("_col");
        outs.Add(new IrDeclVar(cv, fi.Collection.Type, col));
        ReleaseAll(c, outs);
        outs.Add(new IrRaw($"for (int _fi = 0; _fi < {fi.LenCName}({cv}); _fi++)"));
        var b2 = new List<IrStmt>();
        var f2 = new Frame { Loop = true };
        _frames.Push(f2);
        b2.Add(new IrRaw($"{fi.ElemType.ToCType()} {fi.Var} = {fi.GetCName}({cv}, _fi);"));
        if (IsManaged(fi.ElemType)) RegisterOwner(fi.Var, fi.ElemType);
        LowerBodyInto(fi.Body, b2);
        ReleaseFrame(f2, b2);
        _frames.Pop();
        outs.Add(new IrBlock(b2));
        if (colManaged) outs.Add(ReleaseStmt(new IrVar(cv, fi.Collection.Type)));
    }

    void LowerTryCatch(IrTryCatch tc, List<IrStmt> outs)
    {
        string catchLbl = $"_catch_{tc.Seq}";
        string endLbl = $"_end_{tc.Seq}";

        var tryStmts = new List<IrStmt> { new IrRaw("int _has_error = 0;") };
        var prev = (_inTry, _catchLabel);
        _inTry = true; _catchLabel = catchLbl;
        var tryFrame = new Frame { Try = true };
        _frames.Push(tryFrame);
        LowerBodyInto(tc.Try, tryStmts);
        ReleaseFrame(tryFrame, tryStmts);
        _frames.Pop();
        (_inTry, _catchLabel) = prev;
        tryStmts.Add(new IrRaw($"if (_has_error) goto {catchLbl};"));
        outs.Add(new IrBlock(tryStmts));

        outs.Add(new IrRaw($"goto {endLbl};"));
        outs.Add(new IrRaw($"{catchLbl}:;"));

        var catchStmts = new List<IrStmt>();
        var catchFrame = new Frame();
        _frames.Push(catchFrame);
        LowerBodyInto(tc.Catch, catchStmts);
        ReleaseFrame(catchFrame, catchStmts);
        _frames.Pop();
        outs.Add(new IrBlock(catchStmts));
        outs.Add(new IrRaw($"{endLbl}:;"));
    }

    /// <summary>
    /// Releases owners out to the catch or function boundary, then jumps to the handler.
    /// </summary>
    void LowerThrow(List<IrStmt> outs)
    {
        if (_inTry)
        {
            ReleaseForExit(outs, f => f.Try);
            outs.Add(new IrRaw($"goto {_catchLabel};"));
        }
        else if (_resultType != null)
        {
            ReleaseForExit(outs, _ => false);
            outs.Add(new IrRaw($"{_resultType.ToCType()} _err = {{ .has_error = true }};"));
            outs.Add(new IrRaw("return _err;"));
        }
    }

    #endregion

    #region Expression flattening

    /// <summary>
    /// Returns a simple IrExpr; hoists managed producers in borrow position into temps.
    /// Mirrors the emitter's former EmitExprH.
    /// </summary>
    IrExpr Flatten(IrExpr e, bool owned, List<IrStmt> pre, List<(string, IrType)> cl)
    {
        if (e is IrNewInit ni)
        {
            var v = LowerNewInit(ni, pre);
            if (!owned) cl.Add((v.Name, v.Type));
            return v;
        }

        if (e is IrTernary tern) return FlattenTernary(tern, owned, pre, cl);

        IrExpr inline = e switch
        {
            IrStaticCall sc => sc with { Args = FlattenArgs(sc.Args, pre, cl) },
            IrInstanceCall ic => ic with { Recv = Flatten(ic.Recv, false, pre, cl), Args = FlattenArgs(ic.Args, pre, cl) },
            IrThrowsCall tc => tc with { Args = FlattenArgs(tc.Args, pre, cl) },
            IrThrowsInstanceCall ti => ti with { Recv = Flatten(ti.Recv, false, pre, cl), Args = FlattenArgs(ti.Args, pre, cl) },
            IrNew n => n with { Args = FlattenArgs(n.Args, pre, cl) },
            IrCast c => c with { Value = Flatten(c.Value, true, pre, cl) },
            IrFieldLoad fl => fl with { Obj = Flatten(fl.Obj, false, pre, cl) },
            IrIndex ix => ix with { Obj = Flatten(ix.Obj, false, pre, cl), Idx = Flatten(ix.Idx, false, pre, cl) },
            IrBinOp b => b with { Left = Flatten(b.Left, false, pre, cl), Right = Flatten(b.Right, false, pre, cl) },
            IrUnaryOp u => u with { Operand = Flatten(u.Operand, false, pre, cl) },
            IrPostfix pf => pf with { Operand = Flatten(pf.Operand, false, pre, cl) },
            IrAddrOf a => a with { Target = Flatten(a.Target, false, pre, cl) },
            IrDeref d => d with { Ptr = Flatten(d.Ptr, false, pre, cl) },
            IrIndirectCall ic2 => ic2 with { Target = Flatten(ic2.Target, false, pre, cl), Args = FlattenArgs(ic2.Args, pre, cl) },
            IrUnionConstruct uc => uc with { Args = FlattenArgs(uc.Args, pre, cl) },
            IrUnionField uf => uf with { Union = Flatten(uf.Union, false, pre, cl) },
            _ => e   // literals, IrVar, IrSelfExpr, IrArrayLit, IrFuncRef
        };

        if (IsProducer(e))
            return owned || _inUnsafe ? inline : Hoist(inline, e.Type, pre, cl);
        return inline;
    }

    List<IrExpr> FlattenArgs(List<IrExpr> args, List<IrStmt> pre, List<(string, IrType)> cl) =>
        args.Select(a => Flatten(a, false, pre, cl)).ToList();

    /// <summary>
    /// A ternary evaluates exactly one arm at runtime, so an arm's hoists/retains must
    /// never spill into the surrounding unconditional pre. Non-managed arms with nothing
    /// to sequence stay inline; otherwise both arms materialise into a temp via if/else.
    /// A managed temp is owned (+1) and released by the caller's frame (borrow) or consumed.
    /// </summary>
    IrExpr FlattenTernary(IrTernary t, bool owned, List<IrStmt> pre, List<(string, IrType)> cl)
    {
        IrExpr cond = Flatten(t.Cond, false, pre, cl);
        bool managed = IsManaged(t.Type) && !_inUnsafe;

        var tp = new List<IrStmt>(); var tc = new List<(string, IrType)>();
        var ep = new List<IrStmt>(); var ec = new List<(string, IrType)>();
        IrExpr tv = managed ? Consume(t.Then, tp, tc) : Flatten(t.Then, owned, tp, tc);
        IrExpr ev = managed ? Consume(t.Else, ep, ec) : Flatten(t.Else, owned, ep, ec);

        // Fast path: no conditional sequencing needed - a pure C conditional expression.
        if (!managed && tp.Count == 0 && tc.Count == 0 && ep.Count == 0 && ec.Count == 0)
            return t with { Cond = cond, Then = tv, Else = ev };

        string tmp = Tmp("_tern");
        var tgt = new IrVar(tmp, t.Type);
        pre.Add(new IrDeclVar(tmp, t.Type, null));

        var thenStmts = new List<IrStmt>(tp); thenStmts.Add(new IrAssign(tgt, "=", tv)); ReleaseAll(tc, thenStmts);
        var elseStmts = new List<IrStmt>(ep); elseStmts.Add(new IrAssign(tgt, "=", ev)); ReleaseAll(ec, elseStmts);
        pre.Add(new IrIf(cond, new IrBlock(thenStmts), new IrBlock(elseStmts)));

        if (managed && !owned) cl.Add((tmp, t.Type));   // borrow: release when the statement ends
        return tgt;
    }

    // A +1-owned value for a consuming position: producers pass through; a managed borrow is retained.
    IrExpr Consume(IrExpr e, List<IrStmt> pre, List<(string, IrType)> cl)
    {
        IrExpr s = Flatten(e, true, pre, cl);
        return IsManaged(e.Type) && !IsProducer(e) && !_inUnsafe ? Retain(s) : s;
    }

    IrVar Hoist(IrExpr inline, IrType t, List<IrStmt> pre, List<(string, IrType)> cl)
    {
        string tmp = Tmp("_a");
        pre.Add(new IrDeclVar(tmp, t, inline));
        cl.Add((tmp, t));
        return new IrVar(tmp, t);
    }

    IrExpr FlattenThrows(IrExpr e, List<IrStmt> pre, List<(string, IrType)> cl) => e switch
    {
        IrThrowsCall tc => new IrStaticCall(tc.CName, tc.Type, FlattenArgs(tc.Args, pre, cl)) { Span = tc.Span },
        IrThrowsInstanceCall ti => new IrInstanceCall(Flatten(ti.Recv, false, pre, cl), ti.CName, ti.Type, FlattenArgs(ti.Args, pre, cl)) { Span = ti.Span },
        _ => e
    };

    /// <summary>
    /// Lowers a collection initializer into an alloc followed by Add-per-element calls.
    /// Returns the new collection temp (a +1 producer).
    /// </summary>
    IrVar LowerNewInit(IrNewInit ni, List<IrStmt> pre)
    {
        string v = Tmp("_ci");
        var ct = new IrClassRef(ni.ClassName);
        var ap = new List<IrStmt>(); var ac = new List<(string, IrType)>();
        var args = FlattenArgs(ni.Args, ap, ac);
        pre.AddRange(ap);
        pre.Add(new IrDeclVar(v, ct, new IrNew(ni.ClassName, args)));
        ReleaseAll(ac, pre);
        foreach (var el in ni.Inits)
        {
            var ep = new List<IrStmt>(); var ec = new List<(string, IrType)>();
            IrExpr es = Flatten(el, true, ep, ec);
            pre.AddRange(ep);
            if (IsProducer(el) && IsManaged(el.Type))
            {
                string e2 = Tmp("_e");
                pre.Add(new IrDeclVar(e2, el.Type, es));
                pre.Add(new IrExprStmt(new IrStaticCall(ni.AddCName, IrType.Void, [new IrVar(v, ct), new IrVar(e2, el.Type)])));
                pre.Add(ReleaseStmt(new IrVar(e2, el.Type)));
            }
            else
                pre.Add(new IrExprStmt(new IrStaticCall(ni.AddCName, IrType.Void, [new IrVar(v, ct), es])));
            ReleaseAll(ec, pre);
        }
        return new IrVar(v, ct);
    }

    void ReleaseAll(List<(string Name, IrType Type)> cl, List<IrStmt> outs)
    {
        foreach (var (name, type) in cl) outs.Add(ReleaseStmt(new IrVar(name, type)));
    }

    #endregion
}
