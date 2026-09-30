namespace Appa;

using System.Text;

internal sealed class Emitter(IrModule module, DiagnosticBag diag)
{
    private readonly DiagnosticBag _diag = diag;
    private readonly CodeWriter _sharedH = new();
    private readonly CodeWriter _kPre = new();
    private readonly CodeWriter _kTypes = new();
    private readonly CodeWriter _kFwd = new();
    private readonly CodeWriter _kFuncs = new();
    private readonly CodeWriter _kBoot = new();
    private readonly CodeWriter _uPre = new();
    private readonly CodeWriter _uTypes = new();
    private readonly CodeWriter _uFwd = new();
    private readonly CodeWriter _uFunc = new();

    // Per-writer type dedup. Each distinct (writer, key) is emitted exactly once
    // into that translation unit.
    private readonly HashSet<EmitKey> _emitted = [];

    /// <summary>
    /// One declaration in one translation unit. Writer identity is the translation unit, so two
    /// units may each carry their own copy of the same typedef.
    /// </summary>
    private readonly record struct EmitKey(CodeWriter Writer, char Kind, string Name);

    // ARC-managed classes: every non-module Gata class carries a refcount header
    // and a generated destructor.
    private readonly ManagedTypes _managed = new(module);

    // Roles for which no @intrinsic binding was found. Each role is reported once.
    private readonly HashSet<string> _missingRoles = [];

    // The C struct behind a String value, named by every string literal in the program.
    private readonly string _stringStruct = IrType.String.ToCType().TrimEnd('*');

    /// <summary>
    /// Returns true the first time the given key is seen for the given writer, suppressing
    /// duplicate emission within a single translation unit.
    /// </summary>
    private bool FirstInto(CodeWriter w, char kind, string name) => _emitted.Add(new EmitKey(w, kind, name));

    /// <summary>
    /// Returns true if the IR type participates in reference counting: a managed class reference,
    /// or a union whose live variant may hold one.
    /// </summary>
    private bool IsManaged(IrType t) => _managed.IsManaged(t);

    /// <summary>
    /// Returns the C statement retaining one value of the given type: the runtime intrinsic for a
    /// class reference, the union's generated retain for a managed union.
    /// </summary>
    private string RetainCall(IrType t, string operand)
    {
        return t is IrUnionType ut
            ? $"{Mangler.UnionRetain(ut.Name)}({operand});"
            : $"{Intrinsic(Roles.Retain)}({operand});";
    }

    /// <summary>
    /// The releasing counterpart of <see cref="RetainCall"/>.
    /// </summary>
    private string ReleaseCall(IrType t, string operand)
    {
        return t is IrUnionType ut
            ? $"{Mangler.UnionRelease(ut.Name)}({operand});"
            : $"{Intrinsic(Roles.Release)}({operand});";
    }

    /// <summary>
    /// Emits all sections and returns them for Layout to compose into files.
    /// </summary>
    public EmitOutput Build()
    {
        EmitRefCountMode();
        EmitForwardTypedefs();
        EmitEnums();
        EmitAggregateTypes();
        EmitIntrinsicProtos();
        EmitUnionArc();
        EmitUnionEq();
        EmitResultTypedefs();

        foreach (var nb in module.NativeBlocks) EmitNativeBlock(nb);
        foreach (var nt in module.NativeTypes) EmitNativeType(nt);
        foreach (var cls in module.Classes) EmitClass(cls);
        foreach (var fn in module.FreeFunctions) EmitFreeFunc(fn);

        foreach (var proc in module.Processes)
        {
            EmitProcessState(proc);
            foreach (var t in proc.Threads)
                EmitThread(t, proc);
        }

        // a hosted build's main() calls the user entry, if there is one
        string? userEntry = null;
        foreach (var fn in module.FreeFunctions)
        {
            if (fn.IsEntry && fn.Vis == Visibility.User)
            {
                userEntry = fn.CName;
                break;
            }
        }

        return new EmitOutput(
            _sharedH.ToString(),
            _kPre.ToString(), _kTypes.ToString(), _kFwd.ToString(), _kFuncs.ToString(), _kBoot.ToString(),
            _uPre.ToString(), _uTypes.ToString(), _uFwd.ToString(), _uFunc.ToString(),
            module.Processes, module.HasKernelRealm, module.HasUserRealm, userEntry);
    }

    #region Reference-counting mode

    /// <summary>
    /// Tells the runtime whether its reference counts have to be atomic, by defining
    /// GATA_RC_ATOMIC in the shared header when this program contains any concurrency.
    /// </summary>
    private void EmitRefCountMode()
    {
        bool concurrent = module.Processes.Count > 0;
        _sharedH.Lines(
            concurrent
                ? "// This program declares processes, so reference counts must be atomic."
                : "// No process is declared, so no two contexts can hold one reference.",
            "#ifndef GATA_RC_ATOMIC",
            $"#define GATA_RC_ATOMIC {(concurrent ? 1 : 0)}",
            "#endif",
            "");
    }

    #endregion

    #region Forward typedefs

    /// <summary>
    /// Forward-declares every Gata class struct in the shared header so any file can use a class
    /// pointer before its full struct is defined.
    /// </summary>
    private void EmitForwardTypedefs()
    {
        bool any = false;
        foreach (var cls in module.Classes)
        {
            if (!FirstInto(_sharedH, 'T', cls.Name)) continue;
            _sharedH.Line($"typedef struct {cls.CName} {cls.CName};");
            any = true;
        }
        if (any) _sharedH.Line("");
    }

    #endregion

    #region Enums and unions

    /// <summary>
    /// Emits a C typedef enum for every declared Gata enum type into the shared header.
    /// </summary>
    private void EmitEnums()
    {
        foreach (var e in module.Enums)
        {
            var members = new List<string>();
            foreach (var m in e.Members)
            {
                string name = Mangler.EnumMember(e.Name, m.Name);
                members.Add(m.CValue == null ? name : $"{name} = {m.CValue}");
            }
            _sharedH.Line($"typedef enum {{ {string.Join(", ", members)} }} {e.CName};");
        }
        if (module.Enums.Count > 0) _sharedH.Line("");
    }

    /// <summary>
    /// Emits one tagged-union struct into the shared header: a tag integer plus a C union of
    /// per-variant payload structs. Called by EmitAggregateTypes once every type this union stores
    /// by value is already defined.
    /// </summary>
    private void EmitUnion(IrUnion u)
    {
        using (_sharedH.Block("typedef struct {", $"}} {u.CName};"))
        {
            _sharedH.Line("int __tag;");

            // an all-tags union (every variant empty) gets no payload at all
            if (u.Variants.Any(v => v.Fields.Count > 0))
            {
                using (_sharedH.Block("union {", "} payload;"))
                {
                    foreach (var v in u.Variants)
                    {
                        if (v.Fields.Count == 0) continue;

                        var sb = new StringBuilder("struct { ");
                        foreach (var f in v.Fields)
                            sb.Append($"{f.Type.ToCType()} {Mangler.Member(f.Name)}; ");
                        sb.Append($"}} {Mangler.Member(v.Name)};");
                        _sharedH.Line(sb.ToString());
                    }
                }
            }
        }
    }

    #endregion

    #region Fixed-array types

    /// <summary>
    /// Emits the C struct wrapper for one fixed-array type. Called by EmitAggregateTypes once the
    /// element type is already defined.
    /// </summary>
    private void EmitArrayType(IrArrayType a)
    {
        _sharedH.Line($"typedef struct {{ {a.Elem.ToCType()} _[{a.Size}]; }} {a.ToCType()};");
    }

    #endregion

    #region Result types

    /// <summary>
    /// Emits Result_T struct typedefs for every throws function return type, forward-declaring any
    /// class pointer types they reference so the shared header stays self-contained.
    /// </summary>
    private void EmitResultTypedefs()
    {
        var forwarded = new HashSet<string>();
        foreach (var (_, innerType) in module.Symbols.ResultTypedefs)
        {
            if (module.Symbols.IsClass(innerType))
            {
                if (forwarded.Add(innerType) && FirstInto(_sharedH, 'T', innerType))
                {
                    string cn = Mangler.Class(innerType);
                    _sharedH.Line($"typedef struct {cn} {cn};");
                }
            }
        }
        if (forwarded.Count > 0) _sharedH.Line("");

        foreach (var (resultType, innerType) in module.Symbols.ResultTypedefs)
        {
            string ct = module.Symbols.CType(innerType);
            if (FirstInto(_sharedH, 'S', resultType))
                _sharedH.Line($"typedef struct {{ {ct} value; bool has_error; }} {resultType};");
        }
        if (module.Symbols.ResultTypedefs.Count > 0) _sharedH.Line("");
    }

    #endregion

    #region Function pointer types

    /// <summary>
    /// Emits the C typedef for one function-pointer type. Called by EmitAggregateTypes once every
    /// type named in the signature is already defined.
    /// </summary>
    private void EmitFuncPtrType(IrFuncPtrType f)
    {
        string ps = f.Params.Count == 0 ? "void" : string.Join(", ", f.Params.Select(p => p.ToCType()));
        _sharedH.Line($"typedef {f.Ret.ToCType()} (*{f.ToCType()})({ps});");
    }

    #endregion

    #region Aggregate type ordering

    /// <summary>
    /// Emits every fixed-array, function-pointer, and union typedef in dependency order.
    /// </summary>
    private void EmitAggregateTypes()
    {
        // cname -> the thing to emit under that name.
        var pending = new Dictionary<string, object>();
        foreach (var a in module.ArrayTypes)
        {
            if (a.Size > 0) pending.TryAdd(a.ToCType(), a);
        }
        foreach (var f in module.FuncPtrTypes) pending.TryAdd(f.ToCType(), f);
        foreach (var u in module.Unions) pending.TryAdd(u.CName, u);

        if (pending.Count == 0) return;

        // Names currently on the DFS stack. A cycle among these types means a struct that
        // contains itself, which the resolver already rejects. Breaking here just stops
        // this pass from recursing forever on IR it was handed anyway.
        var visiting = new HashSet<string>();
        bool any = false;
        foreach (var name in pending.Keys.ToList()) any |= Emit(name);
        if (any) _sharedH.Line("");

        bool Emit(string cname)
        {
            if (!pending.TryGetValue(cname, out var item)) return false;
            if (!visiting.Add(cname)) return false;
            foreach (var dep in DependenciesOf(item)) Emit(dep);
            visiting.Remove(cname);

            // Re-check: a cycle can bring us back here after the dependency walk.
            if (!pending.Remove(cname)) return false;
            switch (item)
            {
                case IrArrayType a: EmitArrayType(a); break;
                case IrFuncPtrType f: EmitFuncPtrType(f); break;
                case IrUnion u: EmitUnion(u); break;
            }
            FirstInto(_sharedH, 'S', cname);
            return true;
        }
    }

    /// <summary>
    /// Yields the C type names an aggregate needs defined before it can be emitted: its element
    /// type, its signature types, or its variant field types.
    /// </summary>
    private static IEnumerable<string> DependenciesOf(object item)
    {
        switch (item)
        {
            case IrArrayType a:
                yield return a.Elem.ToCType();
                break;
            case IrFuncPtrType f:
                yield return f.Ret.ToCType();
                foreach (var p in f.Params) yield return p.ToCType();
                break;
            case IrUnion u:
                foreach (var v in u.Variants)
                {
                    foreach (var fld in v.Fields) yield return fld.Type.ToCType();
                }
                break;
        }
    }

    /// <summary>
    /// Emits the retain/release pair for every managed union: the tag decides what to count, so the
    /// pair is per-type and generated like a class destructor. By value, so retain composes in
    /// expression position. Prototypes first, as a union may hold one.
    /// </summary>
    private void EmitUnionArc()
    {
        var managed = module.Unions.Where(u => _managed.IsManagedUnion(u.Name)).ToList();
        if (managed.Count == 0) return;

        foreach (var u in managed)
        {
            _sharedH.Line($"static inline {u.CName} {Mangler.UnionRetain(u.Name)}({u.CName} _v);");
            _sharedH.Line($"static inline void {Mangler.UnionRelease(u.Name)}({u.CName} _v);");
        }
        _sharedH.Line("");

        foreach (var u in managed)
        {
            EmitUnionArcBody(u, retain: true);
            EmitUnionArcBody(u, retain: false);
        }
    }

    /// <summary>
    /// Emits one half of a managed union's retain/release pair. The two differ only in the
    /// per-field call and the return, so they share this body rather than drifting apart.
    /// </summary>
    private void EmitUnionArcBody(IrUnion u, bool retain)
    {
        string name = retain ? Mangler.UnionRetain(u.Name) : Mangler.UnionRelease(u.Name);
        string sig = retain ? $"static inline {u.CName} {name}({u.CName} _v)" : $"static inline void {name}({u.CName} _v)";

        using (_sharedH.Block($"{sig} {{"))
        {
            using (_sharedH.Block("switch (_v.__tag) {", "}"))
            {
                for (int i = 0; i < u.Variants.Count; i++)
                {
                    var v = u.Variants[i];
                    var managedFields = v.Fields.Where(f => IsManaged(f.Type)).ToList();
                    if (managedFields.Count == 0) continue;

                    // The variant index, not the tag enumerator: __tag is a plain int, and every
                    // other site that writes or tests it uses the index too.
                    var sb = new StringBuilder();
                    sb.Append("case ").Append(i).Append(": ");
                    foreach (var f in managedFields)
                    {
                        string operand = $"_v.payload.{Mangler.Member(v.Name)}.{Mangler.Member(f.Name)}";
                        sb.Append(retain ? RetainCall(f.Type, operand) : ReleaseCall(f.Type, operand)).Append(' ');
                    }
                    sb.Append("break;");
                    _sharedH.Line(sb.ToString());
                }

                // Variants holding nothing managed land here. Always emitted: a switch whose
                // every case was skipped above would otherwise be an empty statement.
                _sharedH.Line("default: break;");
            }
            if (retain) _sharedH.Line("return _v;");
        }
        _sharedH.Blank();
    }

    /// <summary>
    /// Emits each union's structural equality: tags first, then one comparison per field of the
    /// live variant, by whatever '==' already means for that field's own type. memcmp would be
    /// wrong and slow. It reads the payload's inactive members and padding.
    /// </summary>
    private void EmitUnionEq()
    {
        if (module.Unions.Count == 0) return;
        foreach (var u in module.Unions)
            _sharedH.Line($"static inline bool {Mangler.UnionEq(u.Name)}({u.CName} _a, {u.CName} _b);");
        _sharedH.Line("");

        foreach (var u in module.Unions)
        {
            if (EqEmittableIn(u, Visibility.Kernel)) EmitUnionEqBody(u, _kFuncs);
            if (EqEmittableIn(u, Visibility.User)) EmitUnionEqBody(u, _uFunc);
        }
    }

    /// <summary>
    /// True if every '==' this union's equality calls is declared in the given realm. A class
    /// inside 'user { }' is emitted only into uproc.c, so a kernel-side body would call an
    /// undeclared function, a warning on the pinned gcc 7, fatal on anything newer.
    /// </summary>
    private bool EqEmittableIn(IrUnion u, Visibility realm)
    {
        return Visit(u, []);

        bool Visit(IrUnion union, HashSet<string> seen)
        {
            if (!seen.Add(union.Name)) return true;
            return union.Variants.All(v => v.Fields.All(f => Reachable(f.Type)));
        }

        bool Reachable(IrType t)
        {
            switch (t)
            {
                case IrArrayType a: return Reachable(a.Elem);
                case IrUnionType nested:
                    return UnionByName(nested.Name) is not { } n || Visit(n, []);
                case IrClassRef cr when ClassEqOperator(cr.ClassName) != null:
                    var vis = ClassByName(cr.ClassName)!.Vis;
                    return realm == Visibility.Kernel ? vis != Visibility.User : vis != Visibility.Kernel;
                default: return true;
            }
        }
    }

    private void EmitUnionEqBody(IrUnion u, CodeWriter w)
    {
        using (w.Block($"static inline bool {Mangler.UnionEq(u.Name)}({u.CName} _a, {u.CName} _b) {{"))
        {
            w.Line("if (_a.__tag != _b.__tag) return false;");
            using (w.Block("switch (_a.__tag) {", "}"))
            {
                for (int i = 0; i < u.Variants.Count; i++)
                {
                    var v = u.Variants[i];
                    if (v.Fields.Count == 0) continue;

                    var terms = new List<string>();
                    foreach (var f in v.Fields)
                    {
                        string field = $"payload.{Mangler.Member(v.Name)}.{Mangler.Member(f.Name)}";
                        terms.Add(EqTerm(f.Type, "_a." + field, "_b." + field));
                    }

                    w.Line($"case {i}: return {string.Join(" && ", terms)};");
                }

                // Payload-free variants, and any variant whose fields all compared trivially.
                w.Line("default: return true;");
            }
        }
        w.Blank();
    }

    /// <summary>
    /// Returns a C expression comparing two values of the given type, applying the same rule that
    /// '==' on that type would apply on its own.
    /// </summary>
    private string EqTerm(IrType t, string a, string b)
    {
        switch (t)
        {
            case IrUnionType ut:
                return $"{Mangler.UnionEq(ut.Name)}({a}, {b})";

            case IrArrayType arr when arr.Size > 0:
            {
                var terms = new List<string>(arr.Size);
                for (int i = 0; i < arr.Size; i++)
                    terms.Add(EqTerm(arr.Elem, $"{a}._[{i}]", $"{b}._[{i}]"));
                return terms.Count == 0 ? "true" : $"({string.Join(" && ", terms)})";
            }

            case IrClassRef cr when ClassEqOperator(cr.ClassName) is { } opCName:
                return $"{opCName}({a}, {b})";

            default:
                return $"({a} == {b})";
        }
    }

    private Dictionary<string, IrClass>? _classIndex;
    private Dictionary<string, IrUnion>? _unionIndex;

    private IrClass? ClassByName(string name)
    {
        _classIndex ??= BuildIndex(module.Classes, c => c.Name);
        return _classIndex.GetValueOrDefault(name);
    }

    private IrUnion? UnionByName(string name)
    {
        _unionIndex ??= BuildIndex(module.Unions, u => u.Name);
        return _unionIndex.GetValueOrDefault(name);
    }

    private static Dictionary<string, T> BuildIndex<T>(List<T> items, Func<T, string> key)
    {
        var d = new Dictionary<string, T>();
        foreach (var i in items) d.TryAdd(key(i), i);
        return d;
    }

    /// <summary>
    /// Returns the CName of the class's bool-returning '==' overload, or null if it declares none -
    /// in which case its references compare by address, as they do anywhere else.
    /// </summary>
    private string? ClassEqOperator(string className)
    {
        var cls = ClassByName(className);
        if (cls == null) return null;

        foreach (var op in cls.Operators)
        {
            if (op.Op == "==" && op.Params.Count == 1 && op.ReturnType is IrPrimType { CName: "bool" })
                return op.CName;
        }
        return null;
    }

    #endregion

    #region Native blocks

    /// <summary>
    /// Emits a native block into the appropriate preamble, types, or boot section based on the
    /// block's section tag, then routes to the kernel or user writer by visibility.
    /// </summary>
    private void EmitNativeBlock(IrNativeBlock nb)
    {
        string t = TrimC(nb.C);
        var (kw, uw) = nb.Section switch
        {
            NativeSection.Preamble => (_kPre, _uPre),
            NativeSection.Boot => (_kBoot, (CodeWriter?)null),
            _ => (_kTypes, _uTypes),
        };
        // boot blocks have no user-side counterpart, so uw can be null
        if (nb.Vis != Visibility.User)
        {
            kw.Line(t);
            kw.Line("");
        }
        if (nb.Vis != Visibility.Kernel && uw != null)
        {
            uw.Line(t);
            uw.Line("");
        }
    }

    /// <summary>
    /// Emits a native type struct and typedef into the appropriate writer. Duplicate emission
    /// within a writer is suppressed via FirstInto.
    /// </summary>
    private void EmitNativeType(IrNativeType nt)
    {
        void EmitTo(CodeWriter w, string body)
        {
            if (!FirstInto(w, 'N', nt.Name)) return;
            w.Line($"typedef struct {nt.CName} {nt.CName};");
            using (w.Block($"struct {nt.CName} {{", "};"))
                w.Line(TrimC(body));
            w.Blank();
        }
        switch (nt.Vis)
        {
            case Visibility.Kernel: EmitTo(_kTypes, nt.C); break;
            case Visibility.User: EmitTo(_uTypes, nt.C); break;
            default: EmitTo(_sharedH, nt.C); break;
        }
    }

    #endregion

    #region Classes

    /// <summary>
    /// Dispatches a class to the appropriate emitter: module, library class, or concrete class.
    /// </summary>
    private void EmitClass(IrClass cls)
    {
        if (cls.IsModule)
        {
            EmitModule(cls);
            return;
        }

        if (!cls.IsLib)
        {
            bool isKernel = cls.Vis == Visibility.Kernel;
            EmitConcreteClass(cls, isKernel ? _kTypes : _uTypes,
                                    isKernel ? _kFwd : _uFwd,
                                    isKernel ? _kFuncs : _uFunc, isLib: false);
            return;
        }

        bool toKernel = cls.Vis != Visibility.User;
        bool toUser = cls.Vis != Visibility.Kernel;

        // one copy in shared.h when it can go there, otherwise one per unit that needs it
        if (CanLiveInSharedHeader(cls) && toKernel && toUser)
        {
            EmitLibClass(cls);
            return;
        }
        if (toKernel) EmitConcreteClass(cls, _kTypes, _kFwd, _kFuncs, isLib: true);
        if (toUser) EmitConcreteClass(cls, _uTypes, _uFwd, _uFunc, isLib: true);
    }

    /// <summary>
    /// Emits a module class as per-file static-inline functions with no struct or allocator.
    /// </summary>
    private void EmitModule(IrClass cls)
    {
        bool toKernel = cls.Vis != Visibility.User;
        bool toUser = cls.Vis != Visibility.Kernel;
        if (toKernel) EmitModuleInto(cls, _kTypes, _kFuncs);
        if (toUser) EmitModuleInto(cls, _uTypes, _uFunc);
    }

    /// <summary>
    /// Emits forward declarations and method bodies for a module into the given writers.
    /// </summary>
    private void EmitModuleInto(IrClass cls, CodeWriter types, CodeWriter funcs)
    {
        foreach (var m in cls.Methods) types.Line($"static inline {MethodSig(m)};");
        types.Line("");
        foreach (var m in cls.Methods) EmitFunctionBody(m, funcs, isLib: true);
    }

    /// <summary>
    /// Emits a concrete class into the given writers. Library classes use static-inline functions;
    /// context classes use regular linkage with separate forward declarations.
    /// </summary>
    private void EmitConcreteClass(IrClass cls, CodeWriter types, CodeWriter fwd, CodeWriter funcs, bool isLib)
    {
        string prefix = isLib ? "static inline " : "";

        if (FirstInto(types, 'T', cls.Name))
        {
            types.Line($"typedef struct {cls.CName} {cls.CName};");
            types.Line("");
        }

        if (FirstInto(types, 'S', cls.Name))
        {
            using (types.Block($"struct {cls.CName} {{", "};"))
            {
                EmitObjHeader(types);
                foreach (var rf in cls.RawFields) types.Line(TrimC(rf.C));
                foreach (var f in cls.Fields)
                    types.Line($"{f.Type.ToCType()} {Mangler.Member(f.Name)}; /* field */");
            }
            types.Blank();
        }

        if (isLib)
        {
            foreach (var m in cls.Methods) types.Line($"{prefix}{MethodSig(m)};");
            foreach (var o in cls.Operators) types.Line($"{prefix}{OperatorSig(o)};");
            if (NeedsDtor(cls)) types.Line($"{prefix}{DtorSig(cls)};");
            types.Line($"{prefix}{AllocatorSig(cls)};");
            types.Line("");
        }
        else
        {
            fwd.Line($"{AllocatorSig(cls)};");
            var init = InitOf(cls);
            if (init != null) types.Line($"{MethodSig(init)};");
            if (NeedsDtor(cls)) types.Line($"{DtorSig(cls)};");
        }

        EmitAllocator(cls, isLib ? funcs : types, isLib);

        foreach (var m in cls.Methods)
        {
            if (!isLib) fwd.Line($"{MethodSig(m)};");
            EmitFunctionBody(m, funcs, isLib);
        }
        foreach (var o in cls.Operators)
        {
            if (!isLib) fwd.Line($"{OperatorSig(o)};");
            EmitOperatorBody(o, funcs, isLib);
        }
        EmitDtor(cls, funcs, isLib);
    }

    /// <summary>
    /// Emits a fully self-contained library class into the shared header.
    /// </summary>
    private void EmitLibClass(IrClass cls)
    {
        var w = _sharedH;

        if (FirstInto(w, 'T', cls.Name))
        {
            w.Line($"typedef struct {cls.CName} {cls.CName};");
            w.Line("");
        }

        if (FirstInto(w, 'S', cls.Name))
        {
            using (w.Block($"struct {cls.CName} {{", "};"))
            {
                EmitObjHeader(w);
                foreach (var rf in cls.RawFields) w.Line(rf.C);
                foreach (var f in cls.Fields)
                    w.Line($"{f.Type.ToCType()} {Mangler.Member(f.Name)}; /* field */");
            }
            w.Blank();
        }

        foreach (var m in cls.Methods) w.Line($"static inline {MethodSig(m)};");
        foreach (var o in cls.Operators) w.Line($"static inline {OperatorSig(o)};");
        if (NeedsDtor(cls)) w.Line($"static inline {DtorSig(cls)};");
        w.Line($"static inline {AllocatorSig(cls)};");
        w.Line("");

        foreach (var m in cls.Methods) EmitFunctionBody(m, w, isLib: true);
        foreach (var o in cls.Operators) EmitOperatorBody(o, w, isLib: true);
        EmitDtor(cls, w, isLib: true);
        EmitAllocator(cls, w, isLib: true);
    }

    /// <summary>
    /// Returns true if a library class is fully self-contained and can live in the shared header.
    /// </summary>
    private static bool CanLiveInSharedHeader(IrClass cls)
    {
        // every member has to be pure native C that never touches a managed type
        foreach (var m in cls.Methods)
        {
            if (m.Body != null) return false;
            if (ReferencesRuntime(m.ReturnType) || MentionsString(m.Native)) return false;
            if (m.Params.Any(p => ReferencesRuntime(p.Type))) return false;
        }

        foreach (var o in cls.Operators)
        {
            if (o.Body != null) return false;
            if (ReferencesRuntime(o.ReturnType) || MentionsString(o.Native)) return false;
            if (o.Params.Any(p => ReferencesRuntime(p.Type))) return false;
        }

        foreach (var rf in cls.RawFields)
        {
            if (MentionsString(rf.C)) return false;
        }

        if (cls.FieldInits.Count > 0) return false;

        foreach (var f in cls.Fields)
        {
            if (ReferencesRuntime(f.Type)) return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true if the type references any ARC-managed class or pointer to one.
    /// </summary>
    private static bool ReferencesRuntime(IrType t)
    {
        return t switch
        {
            IrClassRef => true,
            IrPtrType p => ReferencesRuntime(p.Inner),
            _ => false
        };
    }

    /// <summary>
    /// Returns true if the raw C text mentions the gata_String type or string runtime helpers.
    /// </summary>
    private static bool MentionsString(string? c)
    {
        return c != null && (c.Contains("gata_String") || c.Contains("gata_str_"));
    }

    #endregion

    #region Allocators and destructors

    /// <summary>
    /// Emits the allocator function for the given class into the target writer.
    /// </summary>
    private void EmitAllocator(IrClass cls, CodeWriter w, bool isLib)
    {
        string prefix = isLib ? "static inline " : "";
        string dtorArg = NeedsDtor(cls) ? Mangler.Dtor(cls.Name) : "0";
        using (w.Block($"{prefix}{AllocatorSig(cls)} {{"))
        {
            w.Line($"{cls.CName}* __o = ({cls.CName}*){Intrinsic(Roles.Alloc)}(sizeof({cls.CName}));");
            w.Line($"*__o = ({cls.CName}){{0}};");
            w.Line($"{Intrinsic(Roles.ObjInit)}(__o, {dtorArg});");
            foreach (var f in cls.Fields)
            {
                if (!cls.FieldInits.TryGetValue(f.Name, out var init)) continue;
                using var line = w.Open();
                line.Buffer.Append("__o->").Append(Mangler.Member(f.Name)).Append(" = ");
                Write(init, line.Buffer);
                line.Buffer.Append(';');
            }

            var ctor = InitOf(cls);
            if (cls.HasInit && ctor != null)
            {
                string args = string.Concat(ctor.Params.Select(p => ", " + Mangler.Local(p.Name)));
                w.Line($"{ctor.CName}(__o{args});");
            }
            w.Line("return __o;");
        }
        w.Blank();
    }

    /// <summary>
    /// Emits the destructor for the given class if it owns managed references or declares a
    /// finalizer.
    /// </summary>
    private void EmitDtor(IrClass cls, CodeWriter w, bool isLib)
    {
        if (!NeedsDtor(cls)) return;
        string prefix = isLib ? "static inline " : "";
        using (w.Block($"{prefix}{DtorSig(cls)} {{"))
        {
            w.Line($"{cls.CName}* self = ({cls.CName}*)_vp;");
            var deinit = DeinitOf(cls);
            if (deinit != null) w.Line($"{deinit.CName}(self);");

            // the user's _deinit runs first, while every field is still alive
            foreach (var f in cls.Fields)
            {
                if (IsManaged(f.Type)) w.Line(ReleaseCall(f.Type, $"self->{Mangler.Member(f.Name)}"));
            }
        }
        w.Blank();
    }

    /// <summary>
    /// Returns true if the class requires a destructor due to managed fields or a user finalizer.
    /// </summary>
    private bool NeedsDtor(IrClass cls)
    {
        if (DeinitOf(cls) != null) return true;

        foreach (var f in cls.Fields)
        {
            if (IsManaged(f.Type)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the _deinit method of the class, or null if none is declared.
    /// </summary>
    private static IrFunction? DeinitOf(IrClass cls) => MethodNamed(cls, Lifecycle.Deinit);

    /// <summary>
    /// Returns the _init method of the class, or null if none is declared.
    /// </summary>
    private static IrFunction? InitOf(IrClass cls) => MethodNamed(cls, Lifecycle.Init);

    private static IrFunction? MethodNamed(IrClass cls, string name)
    {
        foreach (var mm in cls.Methods)
        {
            if (mm.Name == name) return mm;
        }
        return null;
    }

    /// <summary>
    /// Emits the ARC object header field as the first struct member.
    /// </summary>
    private void EmitObjHeader(CodeWriter w)
    {
        w.Line($"{Intrinsic(Roles.ObjHeader)} __gata_obj; /* arc header */");
    }

    #endregion

    #region Signatures

    /// <summary>
    /// Returns the C type for a parameter, adding one level of pointer indirection for ref
    /// parameters.
    /// </summary>
    private static string ParamCType(IrParam p)
    {
        return p.IsRef ? $"{p.Type.ToCType()}*" : p.Type.ToCType();
    }

    /// <summary>
    /// Returns the full C function signature for a method, including the implicit self parameter.
    /// </summary>
    private static string MethodSig(IrFunction m)
    {
        string ret = m.IsThrows ? IrTypes.Result(m.ReturnType).ToCType() : m.ReturnType.ToCType();
        string? self = !m.IsStatic && m.OwnerClass != null ? $"{Mangler.Class(m.OwnerClass)}* self" : null;
        return $"{ret} {m.CName}({ParamList(self, m.Params)})";
    }

    /// <summary>
    /// The comma-separated C parameter list, with the self parameter first when there is one.
    /// </summary>
    private static string ParamList(string? self, List<IrParam> ps)
    {
        var parts = new List<string>();
        if (self != null) parts.Add(self);
        foreach (var p in ps) parts.Add($"{ParamCType(p)} {Mangler.Local(p.Name)}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// The full C signature for an operator overload, with a self parameter for every operator
    /// except a static "as", a factory, where self does not exist yet. Internal so tests can
    /// assert the emitted shape directly.
    /// </summary>
    internal static string OperatorSig(IrOperator o)
    {
        string? self = o.IsStatic ? null : $"{Mangler.Class(o.OwnerClass)}* self";
        return $"{o.ReturnType.ToCType()} {o.CName}({ParamList(self, o.Params)})";
    }

    /// <summary>
    /// Returns the C allocator signature, threading through any constructor parameters.
    /// </summary>
    private static string AllocatorSig(IrClass cls)
    {
        var init = InitOf(cls);
        string ps = init != null && init.Params.Count > 0 ? ParamList(null, init.Params) : "void";
        return $"{cls.CName}* {Mangler.Allocator(cls.Name)}({ps})";
    }

    /// <summary>
    /// Returns the C signature for the destructor of a class.
    /// </summary>
    private static string DtorSig(IrClass cls)
    {
        return $"void {Mangler.Dtor(cls.Name)}(void* _vp)";
    }

    /// <summary>
    /// Returns the full C function signature for a free function.
    /// </summary>
    private static string FuncSig(IrFunction fn)
    {
        string ret = fn.IsThrows ? IrTypes.Result(fn.ReturnType).ToCType() : fn.ReturnType.ToCType();
        return $"{ret} {fn.CName}({ParamList(null, fn.Params)})";
    }

    #endregion

    #region Free functions

    /// <summary>
    /// Emits a free function into the translation units its flags call for: an entry function into
    /// its own realm, which lets a Hosted user entry become program.c's main(). A library function
    /// static-inline into both. Anything else into its realm.
    /// </summary>
    private void EmitFreeFunc(IrFunction fn)
    {
        if (fn.IsEntry)
        {
            var (entryFwd, entryFuncs) = fn.Vis == Visibility.User ? (_uFwd, _uFunc) : (_kFwd, _kFuncs);
            entryFwd.Line($"void {fn.CName}(void);");
            entryFuncs.Line($"void {fn.CName}(void)");
            EmitBlock(fn.Body!, entryFuncs);
            entryFuncs.Line("");
            return;
        }

        if (fn.IsLib)
        {
            _kFwd.Line($"static inline {FuncSig(fn)};");
            _uFwd.Line($"static inline {FuncSig(fn)};");
            if (fn.Body == null)
            {
                EmitLibFreeFuncNative(fn, _kFuncs);
                EmitLibFreeFuncNative(fn, _uFunc);
            }
            else
            {
                _kFuncs.Line($"static inline {FuncSig(fn)}");
                EmitBlock(fn.Body, _kFuncs);
                _kFuncs.Line("");
                _uFunc.Line($"static inline {FuncSig(fn)}");
                EmitBlock(fn.Body, _uFunc);
                _uFunc.Line("");
            }
            return;
        }

        bool isKernel = fn.Vis == Visibility.Kernel;
        var fwd = isKernel ? _kFwd : _uFwd;
        var funcs = isKernel ? _kFuncs : _uFunc;
        fwd.Line($"{FuncSig(fn)};");
        if (fn.Body == null)
        {
            EmitNative(funcs, FuncSig(fn), fn.Native);
        }
        else
        {
            funcs.Line($"{FuncSig(fn)}");
            EmitBlock(fn.Body, funcs);
            funcs.Line("");
        }
    }

    /// <summary>
    /// Emits a native library free function into the given writer.
    /// </summary>
    private void EmitLibFreeFuncNative(IrFunction fn, CodeWriter w)
    {
        EmitNative(w, $"static inline {FuncSig(fn)}", fn.Native);
    }

    /// <summary>
    /// Emits a signature line followed by a native C body in braces.
    /// </summary>
    private static void EmitNative(CodeWriter w, string sig, string? native)
    {
        w.Line(sig);
        using (w.Braces())
            w.Line(TrimC(native ?? ""));
        w.Blank();
    }


    /// <summary>
    /// Emits a process's variables as statics in its realm's translation unit, plus the generated
    /// function that assigns them.
    /// </summary>
    private void EmitProcessState(IrProcess proc)
    {
        if (proc.State.Count == 0) return;

        bool kernel = proc.StateInit?.Vis == Visibility.Kernel;
        var types = kernel ? _kTypes : _uTypes;
        foreach (var v in proc.State)
            types.Line($"static {v.Type.ToCType()} {v.CName};");
        if (proc.StateInit != null) types.Line($"static volatile int {GateName(proc)} = 0;");
        types.Blank();

        if (proc.StateInit is not { Body: { } body } init) return;
        var w = kernel ? _kFuncs : _uFunc;
        w.Line($"static void {init.CName}(void)");
        EmitBlock(body, w);
        w.Blank();
        w.Line($"static void {EnterName(proc)}(void)");
        using (w.Braces())
        {
            w.Line("int _st = 0;");
            w.Line($"if (__atomic_compare_exchange_n(&{GateName(proc)}, &_st, 1, 0, " + "__ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE))");
            using (w.Braces())
            {
                w.Line($"{init.CName}();");
                w.Line($"__atomic_store_n(&{GateName(proc)}, 2, __ATOMIC_RELEASE);");
                w.Line("return;");
            }
            w.Line("// Another thread got there first; wait for it to finish publishing.");
            w.Line($"while (__atomic_load_n(&{GateName(proc)}, __ATOMIC_ACQUIRE) != 2) {{ }}");
        }
        w.Blank();
    }

    /// <summary>
    /// The gate a process's threads race on to decide which one initialises its state.
    /// </summary>
    private static string GateName(IrProcess proc) => $"{proc.StateInit!.CName}_gate";

    /// <summary>
    /// The function each of a process's threads calls before its own body.
    /// </summary>
    private static string EnterName(IrProcess proc) => $"{proc.StateInit!.CName}_enter";

    /// <summary>
    /// Emits the entry function for a thread into its realm writer, with a forward declaration
    /// alongside it.
    /// </summary>
    private void EmitThread(IrThread t, IrProcess owner)
    {
        if (t.EntryFunc is not { } entry) return;
        bool kernel = entry.Vis == Visibility.Kernel;
        (kernel ? _kFwd : _uFwd).Line($"void {entry.CName}(void* arg);");
        var w = kernel ? _kFuncs : _uFunc;
        w.Line($"void {entry.CName}(void* arg)");
        using (w.Braces())
        {
            if (owner.StateInit != null) w.Line($"{EnterName(owner)}();");
            foreach (var s in entry.Body!.Stmts) EmitStmt(s, w);
        }
        w.Blank();
    }

    #endregion

    #region Blocks and statements

    /// <summary>
    /// Emits a function body, either native C text or a lowered IR block, into the given writer.
    /// </summary>
    private void EmitFunctionBody(IrFunction m, CodeWriter w, bool isLib)
    {
        string prefix = isLib ? "static inline " : "";
        if (m.Body == null)
        {
            EmitNative(w, $"{prefix}{MethodSig(m)}", m.Native);
            return;
        }
        w.Line($"{prefix}{MethodSig(m)}");
        EmitBlock(m.Body, w);
        w.Line("");
    }

    /// <summary>
    /// Emits an operator body, either native C text or a lowered IR block, into the given writer.
    /// </summary>
    private void EmitOperatorBody(IrOperator o, CodeWriter w, bool isLib)
    {
        string prefix = isLib ? "static inline " : "";
        if (o.Body == null)
        {
            EmitNative(w, $"{prefix}{OperatorSig(o)}", o.Native);
            return;
        }
        w.Line($"{prefix}{OperatorSig(o)}");
        EmitBlock(o.Body, w);
        w.Line("");
    }

    /// <summary>
    /// Emits every statement in a block inside a C brace pair.
    /// </summary>
    private void EmitBlock(IrBlock b, CodeWriter w)
    {
        using var _ = w.Braces();
        foreach (var s in b.Stmts) EmitStmt(s, w);
    }

    /// <summary>
    /// Dispatches a single IR statement to its C emission handler.
    /// </summary>
    private void EmitStmt(IrStmt s, CodeWriter w)
    {
        switch (s)
        {
            case IrGoto g: w.Line($"goto {g.Label};"); break;
            case IrLabel l: w.Line($"{l.Name}:;"); break;
            case IrNativeStmt ns: w.Line(TrimC(ns.C)); break;
            case IrBlock b: EmitBlock(b, w); break;
            case IrUnsafeBlock u: EmitBlock(u.Body, w); break;
            case IrDeclVar dv: EmitDeclVar(dv, w); break;
            case IrAssign a:
            {
                using var line = w.Open();
                WriteAssign(a, line.Buffer);
                line.Buffer.Append(';');
                break;
            }
            case IrExprStmt es:
            {
                using var line = w.Open();
                Write(es.Expr, line.Buffer);
                line.Buffer.Append(';');
                break;
            }
            case IrReturn { Value: null }: w.Line("return;"); break;
            case IrReturn rs:
            {
                using var line = w.Open();
                line.Buffer.Append("return ");
                Write(rs.Value!, line.Buffer);
                line.Buffer.Append(';');
                break;
            }
            case IrBreak:         w.Line("break;"); break;
            case IrContinue:      w.Line("continue;"); break;
            case IrDebug d:       w.Line($"{module.Symbols.FloorName(Roles.EnvDebug)}({NoTrigraphs(d.Raw)});"); break;
            case IrPanic p:       w.Line($"{module.Symbols.FloorName(Roles.EnvPanic)}({NoTrigraphs(p.Raw)});"); break;
            case IrIf ifs:        EmitIf(ifs, w); break;
            case IrWhile ws:
                using (var line = w.Open())
                {
                    line.Buffer.Append("while (");
                    WriteCond(ws.Cond, line.Buffer);
                    line.Buffer.Append(')');
                }
                EmitBlock(ws.Body, w);
                break;
            case IrFor fr: EmitFor(fr, w); break;
            default: throw new System.Diagnostics.UnreachableException($"[Emitter] unhandled IrStmt: {s.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits a local variable declaration with an appropriate default when no initializer is given.
    /// </summary>
    private void EmitDeclVar(IrDeclVar dv, CodeWriter w)
    {
        using var line = w.Open();
        WriteDecl(dv, line.Buffer, withDefault: true);
        line.Buffer.Append(';');
    }

    /// <summary>
    /// Writes a declaration without its terminator. A statement declaration with no initializer
    /// still takes a default, so no local is read before it is written. A for-init does not, which
    /// is the only reason this is a parameter.
    /// </summary>
    private void WriteDecl(IrDeclVar dv, StringBuilder sb, bool withDefault)
    {
        sb.Append(dv.Type.ToCType()).Append(' ').Append(Mangler.Local(dv.Name));
        if (dv.Init != null)
        {
            sb.Append(" = ");
            Write(dv.Init, sb);
        }
        else if (!withDefault) return;
        else if (dv.Type is IrArrayType or IrUnionType) sb.Append(" = {0}");
        else if (IsManaged(dv.Type)) sb.Append(" = NULL");
    }

    /// <summary>
    /// Writes an assignment without its terminator.
    /// </summary>
    private void WriteAssign(IrAssign a, StringBuilder sb)
    {
        Write(a.Target, sb);
        sb.Append(' ').Append(a.Op.Sym()).Append(' ');
        Write(a.Value, sb);
    }

    private void EmitIf(IrIf ifs, CodeWriter w)
    {
        using (var line = w.Open())
        {
            line.Buffer.Append("if (");
            WriteCond(ifs.Cond, line.Buffer);
            line.Buffer.Append(')');
        }
        EmitBlock(ifs.Then, w);
        if (ifs.Else != null)
        {
            w.Line("else");
            EmitBlock(ifs.Else, w);
        }
    }

    /// <summary>
    /// Emits a C-style for loop from the IR for node.
    /// </summary>
    private void EmitFor(IrFor fr, CodeWriter w)
    {
        using (var line = w.Open())
        {
            var sb = line.Buffer;
            sb.Append("for (");
            switch (fr.Init)
            {
                case IrDeclVar dv: WriteDecl(dv, sb, withDefault: false); break;
                case IrAssign aa: WriteAssign(aa, sb); break;
                case IrExprStmt e: Write(e.Expr, sb); break;
            }
            sb.Append("; ");
            if (fr.Cond != null) WriteCond(fr.Cond, sb);
            sb.Append("; ");
            switch (fr.Step)
            {
                case IrAssign sa: WriteAssign(sa, sb); break;
                case IrExprStmt e: Write(e.Expr, sb); break;
                case null: break;
                default: throw new InvalidOperationException($"[Emitter] for-step must be an assignment or expression, got {fr.Step.GetType().Name}");
            }
            sb.Append(')');
        }
        EmitBlock(fr.Body, w);
    }

    #endregion

    #region Expressions

    /// <summary>
    /// Writes an IR expression into the buffer. Every node kind must be fully resolved before
    /// reaching this method. Unrecognised nodes throw.
    /// </summary>
    private void Write(IrExpr e, StringBuilder sb)
    {
        switch (e)
        {
            case IrLitInt li:
                if (li.CText != null) sb.Append(li.CText);
                else sb.Append(li.Value);
                break;
            case IrLitChar lc: sb.Append(lc.Codepoint); break;
            case IrLitFloat lf: sb.Append(lf.Raw); break;
            case IrLitBool lb: sb.Append(lb.Value ? "true" : "false"); break;
            case IrLitString ls:
                sb.Append("GATA_STRLIT(").Append(_stringStruct).Append(", ").Append(NoTrigraphs(ls.Raw)).Append(')');
                break;
            case IrLitNull: sb.Append("NULL"); break;
            case IrEnumConst ec: sb.Append(Mangler.EnumMember(ec.EnumName, ec.Member)); break;
            case IrVar { IsRef: true } vr: sb.Append("(*").Append(Mangler.Local(vr.Name)).Append(')'); break;
            case IrVar v: sb.Append(Mangler.Local(v.Name)); break;
            case IrGlobal g: sb.Append(g.CName); break;
            case IrSelfExpr: sb.Append("self"); break;

            case IrFieldLoad fl:
                Write(fl.Obj, sb);
                sb.Append(fl.Obj.Type is IrUnionType or IrResultType ? "." : "->").Append(Mangler.Member(fl.Field));
                break;

            case IrIndex ix:
            {
                bool boxed = ix.Obj.Type is IrArrayType;
                if (boxed) sb.Append('(');
                Write(ix.Obj, sb);
                sb.Append(boxed ? ")._[" : "[");
                Write(ix.Idx, sb);
                sb.Append(']');
                break;
            }

            case IrStaticCall sc:
                sb.Append(sc.CName).Append('(');
                WriteArgs(sc.Args, sb, null);
                sb.Append(')');
                break;

            case IrInstanceCall ic:
                sb.Append(ic.CName).Append('(');
                WriteArgs(ic.Args, sb, ic.Recv);
                sb.Append(')');
                break;

            case IrBinOp bo:
            {
                var narrowed = NarrowTo(bo.Type);
                if (narrowed != null) sb.Append("((").Append(narrowed).Append(')');
                sb.Append('(');
                Write(bo.Left, sb);
                sb.Append(' ').Append(bo.Op.Sym()).Append(' ');
                Write(bo.Right, sb);
                sb.Append(')');
                if (narrowed != null) sb.Append(')');
                break;
            }

            case IrTernary tn:
                sb.Append('(');
                Write(tn.Cond, sb);
                sb.Append(" ? ");
                Write(tn.Then, sb);
                sb.Append(" : ");
                Write(tn.Else, sb);
                sb.Append(')');
                break;

            case IrUnaryOp uo:
            {
                var narrowed = NarrowTo(uo.Type);
                if (narrowed != null) sb.Append("((").Append(narrowed).Append(')');
                sb.Append('(').Append(uo.Op.Sym());
                Write(uo.Operand, sb);
                sb.Append(')');
                if (narrowed != null) sb.Append(')');
                break;
            }

            case IrPostfix pf:
                sb.Append('(');
                Write(pf.Operand, sb);
                sb.Append(pf.Op.Sym()).Append(')');
                break;

            case IrCast c:
                sb.Append("((").Append(c.To.ToCType()).Append(')');
                Write(c.Value, sb);
                sb.Append(')');
                break;

            case IrNew n:
                sb.Append(Mangler.Allocator(n.ClassName)).Append('(');
                WriteArgs(n.Args, sb, null);
                sb.Append(')');
                break;

            case IrArrayLit al:
                sb.Append('(').Append(al.ArrType.ToCType()).Append("){ { ");
                WriteArgs(al.Elems, sb, null);
                sb.Append(" } }");
                break;

            case IrAddrOf ao:
                sb.Append("(&");
                Write(ao.Target, sb);
                sb.Append(')');
                break;

            case IrDeref dr:
                sb.Append("(*");
                Write(dr.Ptr, sb);
                sb.Append(')');
                break;

            case IrSizeof so: sb.Append("sizeof(").Append(so.Of.ToCType()).Append(')'); break;

            case IrStructLit sl:
                sb.Append('(').Append(sl.StructType.ToCType()).Append("){ ");
                for (int i = 0; i < sl.Fields.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('.').Append(sl.Fields[i].Field).Append(" = ");
                    Write(sl.Fields[i].Value, sb);
                }
                sb.Append(" }");
                break;

            case IrDefault df:
            {
                bool aggregate = IsAggregate(df.Of);
                sb.Append(aggregate ? "(" : "((").Append(df.Of.ToCType()).Append(aggregate ? "){ 0 }" : ")0)");
                break;
            }

            case IrFuncRef fr: sb.Append(fr.CName); break;

            case IrIndirectCall ic2:
                sb.Append('(');
                Write(ic2.Target, sb);
                sb.Append(")(");
                WriteArgs(ic2.Args, sb, null);
                sb.Append(')');
                break;

            case IrUnionConstruct uc: WriteUnionConstruct(uc, sb); break;

            case IrUnionField uf:
                Write(uf.Union, sb);
                sb.Append(".payload.").Append(UnionVariantName(uf.Union.Type, uf.VariantIndex))
                  .Append('.').Append(Mangler.Member(uf.Field));
                break;

            default:
                throw new System.Diagnostics.UnreachableException($"[Emitter] unhandled IrExpr: {e.GetType().Name}");
        }
    }

    /// <summary>
    /// Pins an integer operator result to the type the front end gave it, which C would otherwise
    /// have chosen for itself.
    /// </summary>
    private void WriteCond(IrExpr e, StringBuilder sb)
    {
        // a comparison already has its own parens from the if/while, don't double them
        if (e is not IrBinOp { Type: IrPrimType { CName: "bool" } } bo)
        {
            Write(e, sb);
            return;
        }
        Write(bo.Left, sb);
        sb.Append(' ').Append(bo.Op.Sym()).Append(' ');
        Write(bo.Right, sb);
    }

    /// <summary>
    /// The C type an operator result is narrowed to, or null when the type is boolean or not
    /// numeric and C's own choice already agrees.
    /// </summary>
    private static string? NarrowTo(IrType t) => t is IrPrimType p && p.IsNumeric && p.CName != "bool" ? p.ToCType() : null;

    /// <summary>
    /// Writes a comma-separated argument list, with an optional leading receiver.
    /// </summary>
    private void WriteArgs(List<IrExpr> args, StringBuilder sb, IrExpr? receiver)
    {
        if (receiver != null) Write(receiver, sb);
        for (int i = 0; i < args.Count; i++)
        {
            if (receiver != null || i > 0) sb.Append(", ");
            Write(args[i], sb);
        }
    }

    /// <summary>
    /// Writes a union construction, building the tag and payload compound literal.
    /// </summary>
    private void WriteUnionConstruct(IrUnionConstruct uc, StringBuilder sb)
    {
        var variant = module.UnionNamed(uc.T.Name).Variants[uc.VariantIndex];
        sb.Append('(').Append(uc.T.ToCType()).Append("){ .__tag = ").Append(uc.VariantIndex);
        if (variant.Fields.Count == 0)
        {
            sb.Append(" }");
            return;
        }

        sb.Append(", .payload.").Append(Mangler.Member(variant.Name)).Append(" = { ");
        int n = Math.Min(variant.Fields.Count, uc.Args.Count);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('.').Append(Mangler.Member(variant.Fields[i].Name)).Append(" = ");
            Write(uc.Args[i], sb);
        }
        sb.Append(" } }");
    }

    /// <summary>
    /// Returns the struct field name for a union variant at the given index.
    /// </summary>
    private string UnionVariantName(IrType unionType, int idx)
    {
        return unionType is IrUnionType ut ? Mangler.Member(module.UnionNamed(ut.Name).Variants[idx].Name) : "?";
    }

    #endregion

    #region Intrinsic prototypes

    /// <summary>
    /// Emits a static-inline prototype into the shared header for every free function annotated
    /// with an intrinsic role binding. Skips duplicates via FirstInto.
    /// </summary>
    private void EmitIntrinsicProtos()
    {
        bool any = false;
        foreach (var fn in module.FreeFunctions)
        {
            bool hasIntrinsic = fn.Annotations.Any(a => a is IntrinsicAnnotation);
            if (hasIntrinsic && FirstInto(_sharedH, 'P', fn.CName))
            {
                _sharedH.Line($"static inline {FuncSig(fn)};");
                any = true;
            }
        }
        if (any) _sharedH.Line("");
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Escapes '?' in a string literal being handed to C.
    /// </summary>
    private static string NoTrigraphs(string raw) => raw.Replace("?", "\\?");

    /// <summary>
    /// Strips uniform leading indentation from raw C text so embedded native bodies re-indent
    /// correctly at whatever depth the writer is currently at.
    /// </summary>
    private static string TrimC(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var lines = raw.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
        }

        // the smallest indent any non-blank line has
        int minI = int.MaxValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
            minI = Math.Min(minI, n);
        }
        if (minI == int.MaxValue) minI = 0;

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) sb.AppendLine();
            else sb.Append(line.Length > minI ? line[minI..] : line).AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns true for the IR types that lower to a C struct rather than a scalar. Fixed arrays,
    /// unions, and throws Results are all wrapped in a struct by the emitter. Class references are
    /// pointers, and everything else is a primitive.
    /// </summary>
    private static bool IsAggregate(IrType t) => t is IrArrayType or IrUnionType or IrResultType;

    /// <summary>
    /// Resolves a compiler runtime role to the C symbol name bound via an intrinsic annotation.
    /// Emits a diagnostic and returns a placeholder comment if no binding exists.
    /// </summary>
    private string Intrinsic(string role)
    {
        var n = module.Symbols.IntrinsicOrNull(role);
        if (n != null) return n;
        if (_missingRoles.Add(role))
            _diag.Error(Codes.MissingIntrinsic, "<runtime>", TextSpan.None, $"no libgata symbol provides @intrinsic({role})");
        return $"/*MISSING_INTRINSIC:{role}*/";
    }

    #endregion
}
