namespace Appa;

internal sealed class Ownership(IrModule module)
{
    private readonly ManagedTypes _managed = new(module);

    private readonly string _retain = Role(module, Roles.Retain);
    private readonly string _release = Role(module, Roles.Release);

    /// <summary>
    /// Returns the intrinsic symbol for a role, or a `gata_MISSING_*` placeholder. Silent by
    /// design: this pass runs over stdlib-free input too, and a real build has
    /// Pipeline.ValidateIntrinsics demand the whole role set before emission.
    /// </summary>
    private static string Role(IrModule m, string role)
    {
        return m.Symbols.IntrinsicOrNull(role) ?? $"gata_MISSING_{role}";
    }

    /// <summary>
    /// Returns true if the type participates in reference counting - a managed class reference, or
    /// a union whose live variant may hold one.
    /// </summary>
    private bool IsManaged(IrType t)
    {
        return _managed.IsManaged(t);
    }

    /// <summary>
    /// Returns true if the expression is a producer of a managed value, false otherwise.
    /// </summary>
    private bool IsProducer(IrExpr e)
    {
        return e switch
        {
            IrNew or IrNewInit or IrLitString => true,
            IrCast c => IsProducer(c.Value),
            IrStaticCall sc when IsManaged(sc.Type) => true,
            IrInstanceCall ic when IsManaged(ic.Type) => true,
            IrIndirectCall ic when IsManaged(ic.Type) => true,
            IrTernary t when IsManaged(t.Type) => true,
            _ => false
        };
    }

    private int _seq;
    /// <summary>
    /// Generates a unique temporary variable name with the given prefix.
    /// </summary>
    private string Tmp(string prefix)
    {
        return $"{prefix}{_seq++}";
    }

    // A frame represents a lexical scope in the lowered IR, tracking owning locals and deferred actions.
    private sealed class Frame
    {
        public List<(string Name, IrType Type)>? Owners;
        public List<IrStmt>? Defers;
        public bool Loop;
        public bool Try;

        /// <summary>
        /// Registers a local variable as an owner in this frame, lazily initializing the owner
        /// list.
        /// </summary>
        public void AddOwner(string name, IrType type)
        {
            Owners ??= [];
            Owners.Add((name, type));
        }

        /// <summary>
        /// Registers a deferred action in this frame, lazily initializing the defers list.
        /// </summary>
        public void AddDefer(IrStmt action)
        {
            Defers ??= [];
            Defers.Insert(0, action);
        }
    }

    private readonly Stack<Frame> _frames = new();
    private bool _nextFrameIsLoop;
    private bool _inTry;
    private string _catchLabel = "";
    private bool _inThrowsFunc;
    private IrResultType? _resultType;

    // The declared return type of the function being lowered.
    private IrType _returnType = IrType.Void;

    // The storage an `assign` inside the current catch handler writes to
    private IrExpr? _assignTarget;

    // True when the target already holds a value, so a store has to release the old one. A
    // declaration's target starts null and must not.
    private bool _assignTargetOwns;

    // Inside `unsafe`, ARC is suppressed: owning stores, owner tracking, consume-retains and
    // producer hoisting all step aside, and the author manages lifetimes by hand. Exits still
    // release owners from the enclosing safe frames.
    private bool _inUnsafe;

    // Zero-allocation side-effect and cleanup lists
    private readonly List<IrStmt> _pre = [];
    private readonly List<(string Name, IrType Type)> _cl = [];

    // The two Result fields the throws lowering reads and writes. Spelled once here so the
    // struct's shape lives in exactly two places: this pass and Emitter.EmitResultTypedefs.
    private const string ResultValue = "value";
    private const string ResultHasError = "has_error";

    // The per-try error flag. One is declared at the top of each try block; nested throwing
    // calls inside that block set it, and the block's tail tests it to reach the catch label.
    private const string HasErrorFlag = "__has_error";
    private static IrVar HasErrorVar => new(HasErrorFlag, IrType.Bool);

    /// <summary>
    /// Builds `res.value` for a Result-typed temp, typed as the underlying value.
    /// </summary>
    private static IrFieldLoad ResultValueOf(string res, IrResultType rt)
    {
        return new IrFieldLoad(new IrVar(res, rt), ResultValue, rt.Inner);
    }

    /// <summary>
    /// Builds `res.has_error` for a Result-typed temp.
    /// </summary>
    private static IrFieldLoad ResultHasErrorOf(string res, IrResultType rt)
    {
        return new IrFieldLoad(new IrVar(res, rt), ResultHasError, IrType.Bool);
    }

    /// <summary>
    /// Builds the `(Result_T){ .has_error = true }` literal a failed throws call returns. The value
    /// field is deliberately omitted: C zero-initializes it.
    /// </summary>
    private IrStructLit ErrorResult()
    {
        return new IrStructLit(_resultType!, [(ResultHasError, new IrLitBool(true))]);
    }

    /// <summary>
    /// Builds the success Result for a throws function's return, with or without a value.
    /// </summary>
    private IrStructLit OkResult(IrExpr? value)
    {
        return value == null
            ? new IrStructLit(_resultType!, [(ResultHasError, new IrLitBool(false))])
            : new IrStructLit(_resultType!, [(ResultValue, value), (ResultHasError, new IrLitBool(false))]);
    }

    /// <summary>
    /// Wraps a condition in `if (cond) { body }` for the single-statement branches this pass emits.
    /// </summary>
    private static IrIf IfThen(IrExpr cond, List<IrStmt> body)
    {
        return new IrIf(cond, new IrBlock(body), null);
    }

    /// <summary>
    /// Builds a logical negation of a bool-typed expression.
    /// </summary>
    private static IrUnaryOp Not(IrExpr e)
    {
        return new IrUnaryOp(UnOp.Not, e, IrType.Bool);
    }

    // The induction variable both for-in shapes count with. A nested for-in re-declares it in
    // its own C for-scope, which shadows the outer one exactly as the previous raw text did.
    private const string IndexName = "__fi";
    private static IrVar IndexVar => new(IndexName, IrType.Int);

    /// <summary>
    /// Builds `for (int _fi = 0; _fi &lt; limit; _fi++) body` - the counted loop both for-in shapes
    /// iterate with.
    /// </summary>
    private static IrFor CountedFor(IrExpr limit, IrBlock body)
    {
        return new IrFor(
            new IrDeclVar(IndexName, IrType.Int, new IrLitInt(0)),
            new IrBinOp(BinOp.Lt, IndexVar, limit, IrType.Bool),
            new IrExprStmt(new IrPostfix(PostfixOp.Inc, IndexVar)),
            body);
    }

    #region Entry

    /// <summary>
    /// Runs the ARC pass over an entire module, lowering all functions.
    /// </summary>
    public IrModule Run()
    {
        var classes = new List<IrClass>(module.Classes.Count);
        for (int i = 0; i < module.Classes.Count; i++)
            classes.Add(LowerClass(module.Classes[i]));

        var freeFunctions = new List<IrFunction>(module.FreeFunctions.Count);
        for (int i = 0; i < module.FreeFunctions.Count; i++)
            freeFunctions.Add(LowerFunction(module.FreeFunctions[i]));

        var processes = new List<IrProcess>(module.Processes.Count);
        for (int i = 0; i < module.Processes.Count; i++)
            processes.Add(LowerProcess(module.Processes[i]));

        return module with
        {
            Classes = classes,
            FreeFunctions = freeFunctions,
            Processes = processes
        };
    }

    /// <summary>
    /// Lowers a single class, lowering all its methods and operators.
    /// </summary>
    private IrClass LowerClass(IrClass c)
    {
        var methods = new List<IrFunction>(c.Methods.Count);
        for (int i = 0; i < c.Methods.Count; i++)
            methods.Add(LowerFunction(c.Methods[i]));

        var operators = new List<IrOperator>(c.Operators.Count);
        for (int i = 0; i < c.Operators.Count; i++)
            operators.Add(LowerOperator(c.Operators[i]));

        return c with
        {
            Methods = methods,
            Operators = operators
        };
    }

    /// <summary>
    /// Lowers a single process, lowering all its threads.
    /// </summary>
    private IrProcess LowerProcess(IrProcess p)
    {
        var threads = new List<IrThread>(p.Threads.Count);
        for (int i = 0; i < p.Threads.Count; i++)
        {
            var t = p.Threads[i];
            threads.Add(t.EntryFunc == null ? t : t with { EntryFunc = LowerFunction(t.EntryFunc) });
        }
        var init = p.StateInit == null ? null : LowerFunction(p.StateInit);
        return p with { Threads = threads, StateInit = init };
    }

    /// <summary>
    /// Lowers a single function body, tracking whether it is a throws function.
    /// </summary>
    private IrFunction LowerFunction(IrFunction f)
    {
        if (f.Body == null) return f;
        var prev = (_inThrowsFunc, _resultType, _returnType);
        _returnType = f.ReturnType;
        if (f.IsThrows) { _inThrowsFunc = true; _resultType = IrTypes.Result(f.ReturnType); }
        var body = LowerBlock(f.Body);
        if (f.IsThrows && (body.Stmts.Count == 0 || body.Stmts[^1] is not IrReturn))
            body = body with { Stmts = [.. body.Stmts, new IrReturn(OkResult(null))] };
        (_inThrowsFunc, _resultType, _returnType) = prev;
        return f with { Body = body };
    }

    /// <summary>
    /// Lowers a single operator body, tracking whether it is a throws operator.
    /// </summary>
    private IrOperator LowerOperator(IrOperator o)
    {
        return o.Body == null ? o : o with { Body = LowerBlock(o.Body) };
    }

    #endregion

    #region Blocks and frames

    /// <summary>
    /// Pushes a new frame, lowers all statements into it, emits owner releases, then pops.
    /// </summary>
    private IrBlock LowerBlock(IrBlock b)
    {
        var frame = new Frame { Loop = _nextFrameIsLoop };
        _nextFrameIsLoop = false;
        _frames.Push(frame);
        var outs = new List<IrStmt>();
        foreach (var s in b.Stmts) LowerStmt(s, outs);
        bool alreadyExited = outs.Count > 0 && outs[^1] is IrReturn or IrBreak or IrContinue;
        if (!alreadyExited) ReleaseFrame(frame, outs);
        _frames.Pop();
        return new IrBlock(outs) { Span = b.Span };
    }

    /// <summary>
    /// Lowers all statements in a block into the given output list, without pushing a new frame.
    /// </summary>
    private void LowerBodyInto(IrBlock b, List<IrStmt> outs)
    {
        foreach (var s in b.Stmts) LowerStmt(s, outs);
    }

    /// <summary>
    /// Splices this frame's defers in LIFO order, then releases its owning locals - that order so a
    /// defer can still use a local before ARC touches its refcount. Re-lowered at each splice site
    /// so every occurrence gets its own hoisted-temp names.
    /// </summary>
    private void ReleaseFrame(Frame f, List<IrStmt> outs)
    {
        if (f.Defers != null)
        {
            foreach (var action in f.Defers) LowerStmt(action, outs);
        }
        if (f.Owners != null)
        {
            for (int i = f.Owners.Count - 1; i >= 0; i--)
                outs.Add(ReleaseStmt(new IrVar(f.Owners[i].Name, f.Owners[i].Type)));
        }
    }

    /// <summary>
    /// Releases frames innermost outward until stopAfter returns true; used by return, break and
    /// throw. The stack is snapshotted first because ReleaseFrame re-lowers defers, and a
    /// block-bodied one pushes a frame that would invalidate the enumerator.
    /// </summary>
    private void ReleaseForExit(List<IrStmt> outs, Func<Frame, bool> stopAfter)
    {
        foreach (var f in _frames.ToArray())
        {
            ReleaseFrame(f, outs);
            if (stopAfter(f)) break;
        }
    }

    /// <summary>
    /// Registers a local variable as an owner in the current frame. Skipped inside unsafe blocks
    /// where lifetimes are managed by hand.
    /// </summary>
    private void RegisterOwner(string name, IrType type)
    {
        if (_inUnsafe) return;   // unsafe locals are released by hand
        if (_frames.Count > 0) _frames.Peek().AddOwner(name, type);
    }

    /// <summary>
    /// Wraps an expression in a release call. A class goes to the runtime intrinsic, a managed
    /// union to its own generated release - both plain static calls, so every exit path here gets
    /// union support without a union case of its own.
    /// </summary>
    private IrExprStmt ReleaseStmt(IrExpr e)
    {
        string fn = e.Type is IrUnionType ut ? Mangler.UnionRelease(ut.Name) : _release;
        return new IrExprStmt(new IrStaticCall(fn, IrType.Void, [e]));
    }

    /// <summary>
    /// Wraps an expression in a retain call, returning a new expression that owns the value. The
    /// generated union retain returns the union by value so it composes here exactly as the runtime
    /// intrinsic does for a pointer.
    /// </summary>
    private IrStaticCall Retain(IrExpr e)
    {
        string fn = e.Type is IrUnionType ut ? Mangler.UnionRetain(ut.Name) : _retain;
        return new IrStaticCall(fn, e.Type, [e]) { Span = e.Span };
    }


    /// <summary>
    /// Registers the unlowered defer action with the enclosing frame for splicing at every exit.
    /// Kept unlowered so each splice site re-lowers it fresh with its own temp names. Prepended for
    /// LIFO order against other defers already in the frame.
    /// </summary>
    private void LowerDefer(IrDefer d)
    {
        if (_frames.Count > 0) _frames.Peek().AddDefer(d.Action);
    }

    #endregion

    #region Statements

    /// <summary>
    /// Dispatches a statement node to its children, lowering it into the given output list.
    /// </summary>
    private void LowerStmt(IrStmt s, List<IrStmt> outs)
    {
        switch (s)
        {
            case IrNativeStmt or IrGoto or IrLabel or IrDebug or IrPanic: outs.Add(s); break;
            case IrBlock b: outs.Add(LowerBlock(b)); break;
            case IrUnsafeBlock u:
            {
                bool prev = _inUnsafe; _inUnsafe = true;
                outs.Add(LowerBlock(u.Body));
                _inUnsafe = prev;
                break;
            }
            case IrThrow: LowerThrow(outs); break;
            case IrAssignValue av: LowerAssignValue(av, outs); break;
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
            default: throw new System.Diagnostics.UnreachableException($"[Ownership] unhandled IrStmt: {s.GetType().Name}");
        }
    }

    /// <summary>
    /// Releases all owners and defers from the current frame, then emits a throw statement.
    /// </summary>
    private void LowerDecl(IrDeclVar dv, List<IrStmt> outs)
    {
        bool managed = IsManaged(dv.Type);

        if (dv.Init is IrCatchCall cc)
        {
            LowerCatchDecl(dv, cc, managed, outs);
            return;
        }

        if (dv.Init is IrThrowsCall or IrThrowsInstanceCall)
        {
            int preStart = _pre.Count;
            int clStart = _cl.Count;
            var call = FlattenThrows(dv.Init);
            
            for (int i = preStart; i < _pre.Count; i++)
                outs.Add(_pre[i]);
            _pre.RemoveRange(preStart, _pre.Count - preStart);

            string res = $"__res_{dv.Name}";
            outs.Add(new IrDeclVar(res, dv.Init.Type, call));
            
            int clCount = _cl.Count - clStart;
            for (int i = 0; i < clCount; i++)
                outs.Add(ReleaseStmt(new IrVar(_cl[clStart + i].Name, _cl[clStart + i].Type)));
            _cl.RemoveRange(clStart, clCount);

            ThrowsCheck(res, (IrResultType)dv.Init.Type, outs);
            outs.Add(new IrDeclVar(dv.Name, dv.Type, ResultValueOf(res, (IrResultType)dv.Init.Type)));
            if (managed) RegisterOwner(dv.Name, dv.Type);
            return;
        }

        if (dv.Init == null)
        {
            outs.Add(dv);   // emitter NULL/{0}-initialises managed/array locals
            if (managed) RegisterOwner(dv.Name, dv.Type);
            return;
        }

        int pStart = _pre.Count;
        int cStart = _cl.Count;
        IrExpr init = managed ? Consume(dv.Init) : Flatten(dv.Init, false);

        for (int i = pStart; i < _pre.Count; i++)
            outs.Add(_pre[i]);
        _pre.RemoveRange(pStart, _pre.Count - pStart);

        outs.Add(new IrDeclVar(dv.Name, dv.Type, init) { Span = dv.Span });

        int cCount = _cl.Count - cStart;
        for (int i = 0; i < cCount; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[cStart + i].Name, _cl[cStart + i].Type)));
        _cl.RemoveRange(cStart, cCount);

        if (managed) RegisterOwner(dv.Name, dv.Type);
    }

    /// <summary>
    /// Lowers `let T x = f() catch {...};` to a bare `T x;` plus an if/else over the Result,
    /// `assign v` becoming `x = v`. x is owned by the enclosing block - the whole point, since a
    /// try would trap it - and starts null, so the give-up path is safe.
    /// </summary>
    private void LowerCatchDecl(IrDeclVar dv, IrCatchCall cc, bool managed, List<IrStmt> outs)
    {
        int preStart = _pre.Count;
        int clStart = _cl.Count;
        var call = FlattenThrows(cc.Call);

        for (int i = preStart; i < _pre.Count; i++) outs.Add(_pre[i]);
        _pre.RemoveRange(preStart, _pre.Count - preStart);

        // Declare first, so both arms of the branch below store into the same enclosing local.
        outs.Add(new IrDeclVar(dv.Name, dv.Type, null) { Span = dv.Span });
        if (managed) RegisterOwner(dv.Name, dv.Type);

        CatchBranch(cc, new IrVar(dv.Name, dv.Type), $"__res_{dv.Name}", call, clStart,
                    ownsOldValue: false, outs);
    }

    /// <summary>
    /// Lowers `x = f() catch {...};` onto storage that already exists
    /// </summary>
    private void LowerCatchAssign(IrAssign a, IrCatchCall cc, List<IrStmt> outs)
    {
        int preStart = _pre.Count;
        int clStart = _cl.Count;

        IrExpr target = Flatten(a.Target, false);
        var call = FlattenThrows(cc.Call);

        for (int i = preStart; i < _pre.Count; i++) outs.Add(_pre[i]);
        _pre.RemoveRange(preStart, _pre.Count - preStart);

        CatchBranch(cc, target, Tmp("__res_asg"), call, clStart,
                    ownsOldValue: IsManaged(a.Target.Type) && !_inUnsafe, outs);
    }

    /// <summary>
    /// The branch both catch forms share: bind the Result, then either run the handler or store
    /// the value the call produced. Whichever arm stores, it stores into the same target.
    /// </summary>
    private void CatchBranch(IrCatchCall cc, IrExpr target, string res, IrExpr call,
                             int clStart, bool ownsOldValue, List<IrStmt> outs)
    {
        var rt = (IrResultType)cc.Call.Type;
        outs.Add(new IrDeclVar(res, cc.Call.Type, call));

        int clCount = _cl.Count - clStart;
        for (int i = 0; i < clCount; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[clStart + i].Name, _cl[clStart + i].Type)));
        _cl.RemoveRange(clStart, clCount);

        // The handler's own frame
        var prevTarget = _assignTarget;
        bool prevOwns = _assignTargetOwns;
        _assignTarget = target;
        _assignTargetOwns = ownsOldValue;
        var handlerStmts = new List<IrStmt>();
        var handlerFrame = new Frame();
        _frames.Push(handlerFrame);
        LowerBodyInto(cc.Handler, handlerStmts);
        ReleaseFrame(handlerFrame, handlerStmts);
        _frames.Pop();
        _assignTarget = prevTarget;
        _assignTargetOwns = prevOwns;

        // Success arm: the call already handed back a +1 value.
        var okStmts = new List<IrStmt>();
        StoreInto(target, ResultValueOf(res, rt), ownsOldValue && IsManaged(target.Type), okStmts);

        outs.Add(new IrIf(ResultHasErrorOf(res, rt), new IrBlock(handlerStmts), new IrBlock(okStmts)));
    }

    /// <summary>
    /// Lowers `f() catch { ... };` in statement position, where the result is discarded. No
    /// variable, so the resolver rejects `assign` here. The success arm still releases the +1
    /// nothing else will own; the failure arm must not, since value was never set.
    /// </summary>
    private void LowerCatchExprStmt(IrCatchCall cc, List<IrStmt> outs)
    {
        int preStart = _pre.Count;
        int clStart = _cl.Count;
        var call = FlattenThrows(cc.Call);

        for (int i = preStart; i < _pre.Count; i++) outs.Add(_pre[i]);
        _pre.RemoveRange(preStart, _pre.Count - preStart);

        var rt = (IrResultType)cc.Call.Type;
        string res = Tmp("__res_tmp_");
        outs.Add(new IrDeclVar(res, cc.Call.Type, call));

        int clCount = _cl.Count - clStart;
        for (int i = 0; i < clCount; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[clStart + i].Name, _cl[clStart + i].Type)));
        _cl.RemoveRange(clStart, clCount);

        var handlerStmts = new List<IrStmt>();
        var handlerFrame = new Frame();
        _frames.Push(handlerFrame);
        LowerBodyInto(cc.Handler, handlerStmts);
        ReleaseFrame(handlerFrame, handlerStmts);
        _frames.Pop();

        List<IrStmt> okStmts = IsManaged(rt.Inner)
            ? [ReleaseStmt(ResultValueOf(res, rt))]
            : [];

        outs.Add(okStmts.Count == 0
            ? IfThen(ResultHasErrorOf(res, rt), handlerStmts)
            : new IrIf(ResultHasErrorOf(res, rt), new IrBlock(handlerStmts), new IrBlock(okStmts)));
    }

    /// <summary>
    /// Lowers `assign v;` into a store to the storage its handler belongs to. A managed value is
    /// consumed (+1) so the target owns it, matching what the success arm gets from the call.
    /// </summary>
    private void LowerAssignValue(IrAssignValue av, List<IrStmt> outs)
    {
        var target = _assignTarget!;
        int preStart = _pre.Count;
        int clStart = _cl.Count;

        bool managed = IsManaged(target.Type);
        IrExpr value = managed ? Consume(av.Value) : Flatten(av.Value, false);

        for (int i = preStart; i < _pre.Count; i++) outs.Add(_pre[i]);
        _pre.RemoveRange(preStart, _pre.Count - preStart);

        StoreInto(target, value, managed && _assignTargetOwns, outs);

        int clCount = _cl.Count - clStart;
        for (int i = 0; i < clCount; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[clStart + i].Name, _cl[clStart + i].Type)));
        _cl.RemoveRange(clStart, clCount);
    }

    /// <summary>
    /// Stores an already-owned (+1) value into a target. When the target already holds a value the
    /// old one is released first, through a temp so that self-assignment does not free the value
    /// being stored.
    /// </summary>
    private void StoreInto(IrExpr target, IrExpr value, bool releaseOld, List<IrStmt> outs)
    {
        if (!releaseOld)
        {
            outs.Add(new IrAssign(target, AssignOp.Assign, value));
            return;
        }
        string tmp = Tmp("__cas");
        outs.Add(new IrDeclVar(tmp, target.Type, value));
        outs.Add(ReleaseStmt(target));
        outs.Add(new IrAssign(target, AssignOp.Assign, new IrVar(tmp, target.Type)));
    }

    /// <summary>
    /// Lowers an assignment statement, releasing the old value if the target is a managed type.
    /// </summary>
    private void LowerAssign(IrAssign a, List<IrStmt> outs)
    {
        if (a.Value is IrCatchCall cc) { LowerCatchAssign(a, cc, outs); return; }
        if (a.Value is IrThrowsCall or IrThrowsInstanceCall) { LowerThrowsAssign(a, outs); return; }

        int preStart = _pre.Count;
        int clStart = _cl.Count;

        if (a.Op == AssignOp.Assign && IsManaged(a.Target.Type) && !_inUnsafe)
        {
            // Owning store: release the old value, install the new (+1) one.
            IrExpr tgt = Flatten(a.Target, false);
            IrExpr val = Consume(a.Value);

            for (int i = preStart; i < _pre.Count; i++)
                outs.Add(_pre[i]);
            _pre.RemoveRange(preStart, _pre.Count - preStart);

            string tmp = Tmp("__asg");
            outs.Add(new IrDeclVar(tmp, a.Target.Type, val));
            outs.Add(ReleaseStmt(tgt));
            outs.Add(new IrAssign(tgt, AssignOp.Assign, new IrVar(tmp, a.Target.Type)));

            int clCount = _cl.Count - clStart;
            for (int i = 0; i < clCount; i++)
                outs.Add(ReleaseStmt(new IrVar(_cl[clStart + i].Name, _cl[clStart + i].Type)));
            _cl.RemoveRange(clStart, clCount);

            return;
        }

        IrExpr t = Flatten(a.Target, false);
        IrExpr v = Flatten(a.Value, false);

        for (int i = preStart; i < _pre.Count; i++)
            outs.Add(_pre[i]);
        _pre.RemoveRange(preStart, _pre.Count - preStart);

        outs.Add(new IrAssign(t, a.Op, v) { Span = a.Span });

        int clCount2 = _cl.Count - clStart;
        for (int i = 0; i < clCount2; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[clStart + i].Name, _cl[clStart + i].Type)));
        _cl.RemoveRange(clStart, clCount2);
    }

    /// <summary>
    /// Lowers `x = f();` where f throws and no handler is attached: the Result is bound, the
    /// failure path taken by ThrowsCheck, and only then is the value stored - so a propagating
    /// failure leaves the target holding whatever it held before.
    /// </summary>
    private void LowerThrowsAssign(IrAssign a, List<IrStmt> outs)
    {
        int preStart = _pre.Count;
        int clStart = _cl.Count;

        IrExpr target = Flatten(a.Target, false);
        var call = FlattenThrows(a.Value);

        for (int i = preStart; i < _pre.Count; i++) outs.Add(_pre[i]);
        _pre.RemoveRange(preStart, _pre.Count - preStart);

        var rt = (IrResultType)a.Value.Type;
        string res = Tmp("__res_asg");
        outs.Add(new IrDeclVar(res, a.Value.Type, call));

        int clCount = _cl.Count - clStart;
        for (int i = 0; i < clCount; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[clStart + i].Name, _cl[clStart + i].Type)));
        _cl.RemoveRange(clStart, clCount);

        ThrowsCheck(res, rt, outs);
        StoreInto(target, ResultValueOf(res, rt),
                  IsManaged(a.Target.Type) && !_inUnsafe && IsManaged(target.Type), outs);
    }

    /// <summary>
    /// Lowers an expression statement, releasing any hoisted temps and checking for throws.
    /// </summary>
    private void LowerExprStmt(IrExprStmt es, List<IrStmt> outs)
    {
        if (es.Expr is IrCatchCall scc)
        {
            LowerCatchExprStmt(scc, outs);
            return;
        }

        if (es.Expr is IrThrowsCall or IrThrowsInstanceCall)
        {
            int pStart = _pre.Count;
            int cStart = _cl.Count;
            var call = FlattenThrows(es.Expr);

            for (int i = pStart; i < _pre.Count; i++)
                outs.Add(_pre[i]);
            _pre.RemoveRange(pStart, _pre.Count - pStart);

            string res = Tmp("__res_tmp_");
            outs.Add(new IrDeclVar(res, es.Expr.Type, call));

            int cCount = _cl.Count - cStart;
            for (int i = 0; i < cCount; i++)
                outs.Add(ReleaseStmt(new IrVar(_cl[cStart + i].Name, _cl[cStart + i].Type)));
            _cl.RemoveRange(cStart, cCount);

            var ert = (IrResultType)es.Expr.Type;
            ThrowsCheck(res, ert, outs);
            if (IsManaged(ert.Inner))
                outs.Add(ReleaseStmt(ResultValueOf(res, ert)));
            return;
        }

        int p2Start = _pre.Count;
        int c2Start = _cl.Count;
        IrExpr e = Flatten(es.Expr, false);

        for (int i = p2Start; i < _pre.Count; i++)
            outs.Add(_pre[i]);
        _pre.RemoveRange(p2Start, _pre.Count - p2Start);

        if (!(IsProducer(es.Expr) && IsManaged(es.Expr.Type)))
            outs.Add(new IrExprStmt(e) { Span = es.Span });   // hoisted producers are already released temps

        int c2Count = _cl.Count - c2Start;
        for (int i = 0; i < c2Count; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[c2Start + i].Name, _cl[c2Start + i].Type)));
        _cl.RemoveRange(c2Start, c2Count);
    }

    /// <summary>
    /// Releases all owners and defers from the current frame, then emits a return statement.
    /// </summary>
    private void LowerReturn(IrReturn rs, List<IrStmt> outs)
    {
        if (rs.Value == null)
        {
            ReleaseForExit(outs, _ => false);
            outs.Add(new IrReturn(_inThrowsFunc ? OkResult(null) : null));
            return;
        }

        int pStart = _pre.Count;
        int cStart = _cl.Count;
        bool managed = IsManaged(rs.Value.Type);
        IrExpr val = managed ? Consume(rs.Value) : Flatten(rs.Value, false);

        for (int i = pStart; i < _pre.Count; i++)
            outs.Add(_pre[i]);
        _pre.RemoveRange(pStart, _pre.Count - pStart);

        IrType retType = rs.Value.Type.IsVoid ? _returnType : rs.Value.Type;
        string tmp = Tmp("__ret");
        outs.Add(new IrDeclVar(tmp, retType, val));

        int cCount = _cl.Count - cStart;
        for (int i = 0; i < cCount; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[cStart + i].Name, _cl[cStart + i].Type)));
        _cl.RemoveRange(cStart, cCount);

        ReleaseForExit(outs, _ => false);
        var retVar = new IrVar(tmp, retType);
        outs.Add(new IrReturn(_inThrowsFunc ? OkResult(retVar) : retVar));
    }

    /// <summary>
    /// Lowers an if statement, hoisting any pre- or post-allocations into the surrounding block.
    /// </summary>
    private void LowerIf(IrIf ifs, List<IrStmt> outs)
    {
        int pStart = _pre.Count;
        int cStart = _cl.Count;
        IrExpr cond = Flatten(ifs.Cond, false);
        int pCount = _pre.Count - pStart;
        int cCount = _cl.Count - cStart;

        if (pCount == 0 && cCount == 0)
        {
            outs.Add(new IrIf(cond, LowerBlock(ifs.Then), ifs.Else == null ? null : LowerBlock(ifs.Else)) { Span = ifs.Span });
            return;
        }

        for (int i = pStart; i < _pre.Count; i++)
            outs.Add(_pre[i]);
        _pre.RemoveRange(pStart, pCount);

        string cv = Tmp("__if");
        outs.Add(new IrDeclVar(cv, IrType.Bool, cond));

        for (int i = cStart; i < _cl.Count; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[i].Name, _cl[i].Type)));
        _cl.RemoveRange(cStart, cCount);

        outs.Add(new IrIf(new IrVar(cv, IrType.Bool), LowerBlock(ifs.Then), ifs.Else == null ? null : LowerBlock(ifs.Else)));
    }

    /// <summary>
    /// Lowers a while statement, hoisting any pre- or post-allocations into the surrounding block.
    /// </summary>
    private void LowerWhile(IrWhile ws, List<IrStmt> outs)
    {
        int pStart = _pre.Count;
        int cStart = _cl.Count;
        IrExpr cond = Flatten(ws.Cond, false);
        int pCount = _pre.Count - pStart;
        int cCount = _cl.Count - cStart;

        if (pCount == 0 && cCount == 0)
        {
            _nextFrameIsLoop = true;
            outs.Add(new IrWhile(cond, LowerBlock(ws.Body)) { Span = ws.Span });
            return;
        }

        var inner = new List<IrStmt>(pCount + 2);
        for (int i = pStart; i < _pre.Count; i++)
            inner.Add(_pre[i]);
        _pre.RemoveRange(pStart, pCount);

        string cv = Tmp("__wh");
        inner.Add(new IrDeclVar(cv, IrType.Bool, cond));

        for (int i = cStart; i < _cl.Count; i++)
            inner.Add(ReleaseStmt(new IrVar(_cl[i].Name, _cl[i].Type)));
        _cl.RemoveRange(cStart, cCount);

        inner.Add(IfThen(Not(new IrVar(cv, IrType.Bool)), [new IrBreak()]));
        _nextFrameIsLoop = true;
        inner.Add(LowerBlock(ws.Body));
        outs.Add(new IrWhile(new IrLitBool(true), new IrBlock(inner)));
    }

    /// <summary>
    /// Lowers a for statement, hoisting any pre- or post-allocations into the surrounding block.
    /// </summary>
    private void LowerFor(IrFor fr, List<IrStmt> outs)
    {
        bool initManaged = fr.Init is IrDeclVar idv && IsManaged(idv.Type);

        int cpStart = _pre.Count;
        int ccStart = _cl.Count;
        IrExpr? cond = fr.Cond == null ? null : Flatten(fr.Cond, false);
        int cpCount = _pre.Count - cpStart;
        int ccCount = _cl.Count - ccStart;
        var stepOut = new List<IrStmt>();
        if (fr.Step != null) LowerStmt(fr.Step, stepOut);
        bool stepSimple = stepOut.Count == 0 || (stepOut.Count == 1 && stepOut[0] is IrExprStmt or IrAssign);

        bool simple = !initManaged && cpCount == 0 && ccCount == 0 && stepSimple;

        if (simple)
        {
            _pre.RemoveRange(cpStart, _pre.Count - cpStart);
            _cl.RemoveRange(ccStart, _cl.Count - ccStart);

            IrStmt? init = fr.Init switch
            {
                IrDeclVar dv => dv,
                IrAssign aa => aa,
                IrExprStmt e => e,
                _ => null
            };
            _nextFrameIsLoop = true;
            outs.Add(new IrFor(init, cond, stepOut.Count == 1 ? stepOut[0] : null, LowerBlock(fr.Body)) { Span = fr.Span });
            return;
        }

        var outer = new List<IrStmt>();
        var frame = new Frame();
        _frames.Push(frame);
        if (fr.Init != null) LowerStmt(fr.Init, outer);
        var loop = new List<IrStmt>();
        if (stepOut.Count > 0)
        {
            string firstFlag = Tmp("__first");
            var flagVar = new IrVar(firstFlag, IrType.Bool);
            outer.Add(new IrDeclVar(firstFlag, IrType.Bool, new IrLitBool(true)));
            loop.Add(IfThen(Not(flagVar), stepOut));
            loop.Add(new IrAssign(flagVar, AssignOp.Assign, new IrLitBool(false)));
        }
        if (fr.Cond != null)
        {
            for (int i = 0; i < cpCount; i++)
                loop.Add(_pre[cpStart + i]);
            string cv = Tmp("__fc");
            loop.Add(new IrDeclVar(cv, IrType.Bool, cond));
            for (int i = 0; i < ccCount; i++)
                loop.Add(ReleaseStmt(new IrVar(_cl[ccStart + i].Name, _cl[ccStart + i].Type)));
            loop.Add(IfThen(Not(new IrVar(cv, IrType.Bool)), [new IrBreak()]));
        }

        _pre.RemoveRange(cpStart, _pre.Count - cpStart);
        _cl.RemoveRange(ccStart, _cl.Count - ccStart);

        _nextFrameIsLoop = true;
        loop.Add(LowerBlock(fr.Body));
        outer.Add(new IrWhile(new IrLitBool(true), new IrBlock(loop)));
        ReleaseFrame(frame, outer);
        _frames.Pop();
        outs.Add(new IrBlock(outer));
    }

    /// <summary>
    /// Lowers a for-in statement, hoisting any pre- or post-allocations into the surrounding block.
    /// </summary>
    private void LowerForIn(IrForIn fi, List<IrStmt> outs)
    {
        int pStart = _pre.Count;
        int cStart = _cl.Count;

        if (fi.ArraySize >= 0)
        {
            IrExpr acol = Flatten(fi.Collection, false);

            for (int i = pStart; i < _pre.Count; i++)
                outs.Add(_pre[i]);
            _pre.RemoveRange(pStart, _pre.Count - pStart);

            string av = Tmp("__arr");
            outs.Add(new IrDeclVar(av, fi.Collection.Type, acol));

            int cCount = _cl.Count - cStart;
            for (int i = 0; i < cCount; i++)
                outs.Add(ReleaseStmt(new IrVar(_cl[cStart + i].Name, _cl[cStart + i].Type)));
            _cl.RemoveRange(cStart, cCount);

            var body = new List<IrStmt>();
            var frame = new Frame { Loop = true };
            _frames.Push(frame);
            IrExpr elem = new IrIndex(new IrVar(av, fi.Collection.Type), IndexVar, fi.ElemType);
            if (IsManaged(fi.ElemType))
            {
                body.Add(new IrDeclVar(fi.Var, fi.ElemType, Retain(elem)));
                RegisterOwner(fi.Var, fi.ElemType);
            }
            else body.Add(new IrDeclVar(fi.Var, fi.ElemType, elem));
            LowerBodyInto(fi.Body, body);
            ReleaseFrame(frame, body);
            _frames.Pop();
            outs.Add(CountedFor(new IrLitInt(fi.ArraySize), new IrBlock(body)));
            return;
        }

        IrExpr col = Consume(fi.Collection);

        for (int i = pStart; i < _pre.Count; i++)
            outs.Add(_pre[i]);
        _pre.RemoveRange(pStart, _pre.Count - pStart);

        bool colManaged = IsManaged(fi.Collection.Type);
        string cv = Tmp("__col");
        outs.Add(new IrDeclVar(cv, fi.Collection.Type, col));

        int c2Count = _cl.Count - cStart;
        for (int i = 0; i < c2Count; i++)
            outs.Add(ReleaseStmt(new IrVar(_cl[cStart + i].Name, _cl[cStart + i].Type)));
        _cl.RemoveRange(cStart, c2Count);

        var colVar = new IrVar(cv, fi.Collection.Type);
        var b2 = new List<IrStmt>();
        var f2 = new Frame { Loop = true };
        _frames.Push(f2);
        b2.Add(new IrDeclVar(fi.Var, fi.ElemType,
            new IrStaticCall(fi.GetCName, fi.ElemType, [colVar, IndexVar])));
        if (IsManaged(fi.ElemType)) RegisterOwner(fi.Var, fi.ElemType);
        LowerBodyInto(fi.Body, b2);
        ReleaseFrame(f2, b2);
        _frames.Pop();
        outs.Add(CountedFor(new IrStaticCall(fi.LenCName, IrType.Int, [colVar]), new IrBlock(b2)));
        if (colManaged) outs.Add(ReleaseStmt(colVar));
    }

    /// <summary>
    /// Lowers a try-catch statement, hoisting any pre- or post-allocations into the surrounding
    /// block.
    /// </summary>
    private void LowerTryCatch(IrTryCatch tc, List<IrStmt> outs)
    {
        string catchLbl = $"__catch_{tc.Seq}";
        string endLbl = $"__end_{tc.Seq}";

        var tryStmts = new List<IrStmt> { new IrDeclVar(HasErrorFlag, IrType.Bool, new IrLitBool(false)) };
        var prev = (_inTry, _catchLabel);
        _inTry = true; _catchLabel = catchLbl;
        var tryFrame = new Frame { Try = true };
        _frames.Push(tryFrame);
        LowerBodyInto(tc.Try, tryStmts);
        ReleaseFrame(tryFrame, tryStmts);
        _frames.Pop();
        (_inTry, _catchLabel) = prev;
        tryStmts.Add(IfThen(HasErrorVar, [new IrGoto(catchLbl)]));
        outs.Add(new IrBlock(tryStmts));

        outs.Add(new IrGoto(endLbl));
        outs.Add(new IrLabel(catchLbl));

        var catchStmts = new List<IrStmt>();
        var catchFrame = new Frame();
        _frames.Push(catchFrame);
        LowerBodyInto(tc.Catch, catchStmts);
        ReleaseFrame(catchFrame, catchStmts);
        _frames.Pop();
        outs.Add(new IrBlock(catchStmts));
        outs.Add(new IrLabel(endLbl));
    }

    /// <summary>
    /// Emits the error-branch check after a throwing call's Result is captured into res. Inside a
    /// try, routes to the catch label; otherwise propagates upward as a return.
    /// </summary>
    private void ThrowsCheck(string res, IrResultType rt, List<IrStmt> outs)
    {
        var branch = new List<IrStmt>();
        if (_inTry)
        {
            outs.Add(new IrAssign(HasErrorVar, AssignOp.Assign, ResultHasErrorOf(res, rt)));
            ReleaseForExit(branch, f => f.Try);
            branch.Add(new IrGoto(_catchLabel));
            outs.Add(IfThen(HasErrorVar, branch));
        }
        else
        {
            ReleaseForExit(branch, _ => false);
            branch.Add(new IrReturn(ErrorResult()));
            outs.Add(IfThen(ResultHasErrorOf(res, rt), branch));
        }
    }

    /// <summary>
    /// Releases owners out to the catch or function boundary, then jumps to the handler.
    /// </summary>
    private void LowerThrow(List<IrStmt> outs)
    {
        if (_inTry)
        {
            ReleaseForExit(outs, f => f.Try);
            outs.Add(new IrGoto(_catchLabel));
        }
        else if (_resultType != null)
        {
            ReleaseForExit(outs, _ => false);
            outs.Add(new IrReturn(ErrorResult()));
        }
    }

    #endregion

    #region Expression flattening

    /// <summary>
    /// Returns a simple IrExpr; hoists managed producers in borrow position into temps. Mirrors the
    /// emitter's former EmitExprH.
    /// </summary>
    private IrExpr Flatten(IrExpr e, bool owned)
    {
        if (e is IrNewInit ni)
        {
            var v = LowerNewInit(ni);
            if (!owned) _cl.Add((v.Name, v.Type));
            return v;
        }

        if (e is IrTernary tern) return FlattenTernary(tern, owned);

        IrExpr inline = e switch
        {
            IrStaticCall sc => sc with { Args = FlattenArgs(sc.Args) },
            IrInstanceCall ic => ic with { Recv = Flatten(ic.Recv, false), Args = FlattenArgs(ic.Args) },
            IrThrowsCall tc => tc with { Args = FlattenArgs(tc.Args) },
            IrThrowsInstanceCall ti => ti with { Recv = Flatten(ti.Recv, false), Args = FlattenArgs(ti.Args) },
            IrNew n => n with { Args = FlattenArgs(n.Args) },
            IrCast c => c with { Value = Flatten(c.Value, true) },
            IrFieldLoad fl => fl with { Obj = Flatten(fl.Obj, false) },
            IrIndex ix => ix with { Obj = Flatten(ix.Obj, false), Idx = Flatten(ix.Idx, false) },
            IrBinOp b => b with { Left = Flatten(b.Left, false), Right = Flatten(b.Right, false) },
            IrUnaryOp u => u with { Operand = Flatten(u.Operand, false) },
            IrPostfix pf => pf with { Operand = Flatten(pf.Operand, false) },
            IrAddrOf a => a with { Target = Flatten(a.Target, false) },
            IrDeref d => d with { Ptr = Flatten(d.Ptr, false) },
            IrIndirectCall ic2 => ic2 with { Target = Flatten(ic2.Target, false), Args = FlattenArgs(ic2.Args) },
            IrUnionConstruct uc => uc with { Args = FlattenArgs(uc.Args) },
            IrUnionField uf => uf with { Union = Flatten(uf.Union, false) },
            _ => e   // literals, IrVar, IrSelfExpr, IrArrayLit, IrFuncRef
        };

        if (IsProducer(e))
            return owned || _inUnsafe ? inline : Hoist(inline, e.Type);
        return inline;
    }

    private List<IrExpr> FlattenArgs(List<IrExpr> args)
    {
        var result = new List<IrExpr>(args.Count);
        for (int i = 0; i < args.Count; i++)
            result.Add(Flatten(args[i], false));
        return result;
    }

    /// <summary>
    /// A ternary evaluates one arm, so an arm's hoists must never spill into the unconditional pre.
    /// Arms with nothing to sequence stay inline; otherwise both materialise into a temp via
    /// if/else, owned at +1 and released by the caller's frame.
    /// </summary>
    private IrExpr FlattenTernary(IrTernary t, bool owned)
    {
        IrExpr cond = Flatten(t.Cond, false);
        bool managed = IsManaged(t.Type) && !_inUnsafe;

        int thenPreStart = _pre.Count;
        int thenClStart = _cl.Count;
        IrExpr tv = managed ? Consume(t.Then) : Flatten(t.Then, owned);
        int thenPreCount = _pre.Count - thenPreStart;
        int thenClCount = _cl.Count - thenClStart;

        int elsePreStart = _pre.Count;
        int elseClStart = _cl.Count;
        IrExpr ev = managed ? Consume(t.Else) : Flatten(t.Else, owned);
        int elsePreCount = _pre.Count - elsePreStart;
        int elseClCount = _cl.Count - elseClStart;

        // Fast path: no conditional sequencing needed - a pure C conditional expression.
        if (!managed && thenPreCount == 0 && thenClCount == 0 && elsePreCount == 0 && elseClCount == 0)
        {
            _pre.RemoveRange(thenPreStart, _pre.Count - thenPreStart);
            _cl.RemoveRange(thenClStart, _cl.Count - thenClStart);
            return t with { Cond = cond, Then = tv, Else = ev };
        }

        string tmp = Tmp("__tern");
        var tgt = new IrVar(tmp, t.Type);
        _pre.Insert(thenPreStart, new IrDeclVar(tmp, t.Type, null));
        thenPreStart++;
        elsePreStart++;

        var thenStmts = new List<IrStmt>(thenPreCount + 1 + thenClCount);
        for (int i = 0; i < thenPreCount; i++)
            thenStmts.Add(_pre[thenPreStart + i]);
        thenStmts.Add(new IrAssign(tgt, AssignOp.Assign, tv));
        for (int i = 0; i < thenClCount; i++)
            thenStmts.Add(ReleaseStmt(new IrVar(_cl[thenClStart + i].Name, _cl[thenClStart + i].Type)));

        var elseStmts = new List<IrStmt>(elsePreCount + 1 + elseClCount);
        for (int i = 0; i < elsePreCount; i++)
            elseStmts.Add(_pre[elsePreStart + i]);
        elseStmts.Add(new IrAssign(tgt, AssignOp.Assign, ev));
        for (int i = 0; i < elseClCount; i++)
            elseStmts.Add(ReleaseStmt(new IrVar(_cl[elseClStart + i].Name, _cl[elseClStart + i].Type)));

        _pre.RemoveRange(thenPreStart, _pre.Count - thenPreStart);
        _cl.RemoveRange(thenClStart, _cl.Count - thenClStart);

        _pre.Add(new IrIf(cond, new IrBlock(thenStmts), new IrBlock(elseStmts)));

        if (managed && !owned) _cl.Add((tmp, t.Type));   // borrow: release when the statement ends
        return tgt;
    }

    private IrExpr Consume(IrExpr e)
    {
        IrExpr s = Flatten(e, true);
        return IsManaged(e.Type) && !IsProducer(e) && !_inUnsafe ? Retain(s) : s;
    }

    private IrVar Hoist(IrExpr inline, IrType t)
    {
        string tmp = Tmp("__a");
        _pre.Add(new IrDeclVar(tmp, t, inline));
        _cl.Add((tmp, t));
        return new IrVar(tmp, t);
    }

    private IrExpr FlattenThrows(IrExpr e)
    {
        return e switch
        {
            IrThrowsCall tc => new IrStaticCall(tc.CName, tc.Type, FlattenArgs(tc.Args)) { Span = tc.Span },
            IrThrowsInstanceCall ti => new IrInstanceCall(Flatten(ti.Recv, false), ti.CName, ti.Type, FlattenArgs(ti.Args)) { Span = ti.Span },
            _ => e
        };
    }


    /// <summary>
    /// Lowers a collection initializer into an alloc followed by Add-per-element calls. Returns the
    /// new collection temp (a +1 producer).
    /// </summary>
    private IrVar LowerNewInit(IrNewInit ni)
    {
        string v = Tmp("__ci");
        var ct = IrTypes.ClassRef(ni.ClassName);
        int acStart = _cl.Count;
        var args = FlattenArgs(ni.Args);

        int acCount = _cl.Count - acStart;
        _pre.Add(new IrDeclVar(v, ct, new IrNew(ni.ClassName, args)));

        for (int i = 0; i < acCount; i++)
            _pre.Add(ReleaseStmt(new IrVar(_cl[acStart + i].Name, _cl[acStart + i].Type)));
        _cl.RemoveRange(acStart, acCount);

        foreach (var el in ni.Inits)
        {
            int ecStart = _cl.Count;
            IrExpr es = Flatten(el, true);
            int ecCount = _cl.Count - ecStart;

            if (IsProducer(el) && IsManaged(el.Type))
            {
                string e2 = Tmp("__e");
                _pre.Add(new IrDeclVar(e2, el.Type, es));
                _pre.Add(new IrExprStmt(new IrStaticCall(ni.AddCName, IrType.Void, [new IrVar(v, ct), new IrVar(e2, el.Type)])));
                _pre.Add(ReleaseStmt(new IrVar(e2, el.Type)));
            }
            else
                _pre.Add(new IrExprStmt(new IrStaticCall(ni.AddCName, IrType.Void, [new IrVar(v, ct), es])));

            for (int i = 0; i < ecCount; i++)
                _pre.Add(ReleaseStmt(new IrVar(_cl[ecStart + i].Name, _cl[ecStart + i].Type)));
            _cl.RemoveRange(ecStart, ecCount);
        }
        return new IrVar(v, ct);
    }

    #endregion
}
