namespace Appa;

using System.Text;
using System.Runtime.InteropServices;

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
    // into that translation unit. Keys are namespaced T: (forward typedef),
    // S: (struct or aggregate def), FP: (function-pointer typedef).
    private readonly Dictionary<CodeWriter, HashSet<(char Kind, string Name)>> _emitted = [];

    // ARC-managed classes: every non-module Gata class carries a refcount header
    // and a generated destructor.
    private readonly ManagedTypes _managed = new(module);

    // Roles for which no @intrinsic binding was found; each role is reported once.
    private readonly HashSet<string> _missingRoles = [];

    /// <summary>
    /// Returns true the first time the given key is seen for the given writer, suppressing
    /// duplicate emission within a single translation unit.
    /// </summary>
    private bool FirstInto(CodeWriter w, char kind, string name)
    {
        if (!_emitted.TryGetValue(w, out var s)) _emitted[w] = s = [];
        return s.Add((kind, name));
    }

    /// <summary>
    /// Returns true if the IR type participates in reference counting - a managed class reference,
    /// or a union whose live variant may hold one.
    /// </summary>
    private bool IsManaged(IrType t)
    {
        return _managed.IsManaged(t);
    }

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
        EmitForwardTypedefs();
        EmitEnums();
        EmitAggregateTypes();
        EmitIntrinsicProtos();
        EmitUnionArc();
        EmitUnionEq();
        EmitResultTypedefs();

        var nativeBlocks = CollectionsMarshal.AsSpan(module.NativeBlocks);
        for (int i = 0; i < nativeBlocks.Length; i++) EmitNativeBlock(nativeBlocks[i]);

        var nativeTypes = CollectionsMarshal.AsSpan(module.NativeTypes);
        for (int i = 0; i < nativeTypes.Length; i++) EmitNativeType(nativeTypes[i]);

        var classes = CollectionsMarshal.AsSpan(module.Classes);
        for (int i = 0; i < classes.Length; i++) EmitClass(classes[i]);

        var freeFuncs = CollectionsMarshal.AsSpan(module.FreeFunctions);
        for (int i = 0; i < freeFuncs.Length; i++) EmitFreeFunc(freeFuncs[i]);

        var processes = CollectionsMarshal.AsSpan(module.Processes);
        for (int i = 0; i < processes.Length; i++)
        {
            var proc = processes[i];
            var threads = CollectionsMarshal.AsSpan(proc.Threads);
            for (int j = 0; j < threads.Length; j++)
            {
                EmitThread(threads[j]);
            }
        }

        string? userEntryCName = null;
        foreach (var fn in module.FreeFunctions)
            if (fn.IsEntry && fn.Vis == Visibility.User) { userEntryCName = fn.CName; break; }

        return new EmitOutput(
            _sharedH.ToString(),
            _kPre.ToString(), _kTypes.ToString(), _kFwd.ToString(), _kFuncs.ToString(), _kBoot.ToString(),
            _uPre.ToString(), _uTypes.ToString(), _uFwd.ToString(), _uFunc.ToString(),
            module.Processes, module.HasKernelRealm, module.HasUserRealm, userEntryCName);
    }

    #region Forward typedefs

    /// <summary>
    /// Forward-declares every Gata class struct in the shared header so any file can use a class
    /// pointer before its full struct is defined.
    /// </summary>
    private void EmitForwardTypedefs()
    {
        bool any = false;
        foreach (var cls in module.Classes)
            if (FirstInto(_sharedH, 'T', cls.Name))
            {
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
        var enums = CollectionsMarshal.AsSpan(module.Enums);
        for (int i = 0; i < enums.Length; i++)
        {
            var e = enums[i];
            var sb = new StringBuilder();
            sb.Append("typedef enum { ");
            var members = CollectionsMarshal.AsSpan(e.Members);
            for (int j = 0; j < members.Length; j++)
            {
                if (j > 0) sb.Append(", ");
                var m = members[j];
                sb.Append(Mangler.EnumMember(e.Name, m.Name));
                if (m.CValue != null)
                {
                    sb.Append(" = ").Append(m.CValue);
                }
            }
            sb.Append(" } ").Append(e.CName).Append(';');
            _sharedH.Line(sb.ToString());
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

            bool hasFields = false;
            var variants = CollectionsMarshal.AsSpan(u.Variants);
            for (int j = 0; j < variants.Length; j++)
            {
                if (variants[j].Fields.Count > 0) { hasFields = true; break; }
            }

            if (hasFields)
            {
                using (_sharedH.Block("union {", "} payload;"))
                {
                    for (int j = 0; j < variants.Length; j++)
                    {
                        var v = variants[j];
                        if (v.Fields.Count == 0) continue;

                        var sb = new StringBuilder();
                        sb.Append("struct { ");
                        var fields = CollectionsMarshal.AsSpan(v.Fields);
                        for (int k = 0; k < fields.Length; k++)
                        {
                            var f = fields[k];
                            sb.Append(f.Type.ToCType()).Append(' ').Append(f.Name).Append("; ");
                        }
                        sb.Append("} ").Append(v.Name).Append(';');
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
        var sb = new StringBuilder();
        sb.Append("typedef ").Append(f.Ret.ToCType()).Append(" (*").Append(f.ToCType()).Append(")(");
        if (f.Params.Count == 0)
        {
            sb.Append("void");
        }
        else
        {
            for (int j = 0; j < f.Params.Count; j++)
            {
                if (j > 0) sb.Append(", ");
                sb.Append(f.Params[j].ToCType());
            }
        }
        sb.Append(");");
        _sharedH.Line(sb.ToString());
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
            if (a.Size > 0) pending.TryAdd(a.ToCType(), a);
        foreach (var f in module.FuncPtrTypes) pending.TryAdd(f.ToCType(), f);
        foreach (var u in module.Unions) pending.TryAdd(u.CName, u);

        if (pending.Count == 0) return;

        // Names currently on the DFS stack. A cycle among these types means a struct that
        // contains itself, which the resolver already rejects; breaking here just stops
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
                    foreach (var fld in v.Fields)
                        yield return fld.Type.ToCType();
                break;
        }
    }

    /// <summary>
    /// Emits the retain/release pair for every managed union: the tag decides what to count, so the
    /// pair is per-type and generated like a class destructor. By value, so retain composes in
    /// expression position; prototypes first, as a union may hold one.
    /// </summary>
    private void EmitUnionArc()
    {
        var managed = new List<IrUnion>();
        foreach (var u in module.Unions)
            if (_managed.IsManagedUnion(u.Name)) managed.Add(u);
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
                    var managedFields = new List<IrParam>();
                    foreach (var f in v.Fields)
                        if (IsManaged(f.Type)) managedFields.Add(f);
                    if (managedFields.Count == 0) continue;

                    // The variant index, not the tag enumerator: __tag is a plain int, and every
                    // other site that writes or tests it uses the index too.
                    var sb = new StringBuilder();
                    sb.Append("case ").Append(i).Append(": ");
                    foreach (var f in managedFields)
                    {
                        string operand = $"_v.payload.{v.Name}.{f.Name}";
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
    /// wrong, not just slow - it reads the payload's inactive members and padding.
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
    /// undeclared function - a warning on the pinned gcc 7, fatal on anything newer.
    /// </summary>
    private bool EqEmittableIn(IrUnion u, Visibility realm)
    {
        return Visit(u, []);

        bool Visit(IrUnion union, HashSet<string> seen)
        {
            if (!seen.Add(union.Name)) return true;
            foreach (var v in union.Variants)
                foreach (var f in v.Fields)
                    if (!Reachable(f.Type)) return false;
            return true;
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

    /// <summary>
    /// Emits one union's equality body into the given writer.
    /// </summary>
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

                    var terms = new List<string>(v.Fields.Count);
                    foreach (var f in v.Fields)
                        terms.Add(EqTerm(f.Type,
                            $"_a.payload.{v.Name}.{f.Name}", $"_b.payload.{v.Name}.{f.Name}"));

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

    /// <summary>
    /// Returns the declared class of that name, or null.
    /// </summary>
    private IrClass? ClassByName(string name)
    {
        _classIndex ??= BuildIndex(module.Classes, c => c.Name);
        return _classIndex.GetValueOrDefault(name);
    }

    /// <summary>
    /// Returns the declared union of that name, or null.
    /// </summary>
    private IrUnion? UnionByName(string name)
    {
        _unionIndex ??= BuildIndex(module.Unions, u => u.Name);
        return _unionIndex.GetValueOrDefault(name);
    }

    private static Dictionary<string, T> BuildIndex<T>(List<T> items, Func<T, string> key)
    {
        var d = new Dictionary<string, T>(items.Count);
        foreach (var i in items) d.TryAdd(key(i), i);
        return d;
    }

    /// <summary>
    /// Returns the CName of the class's bool-returning '==' overload, or null if it declares none -
    /// in which case its references compare by address, as they do anywhere else.
    /// </summary>
    private string? ClassEqOperator(string className)
    {
        if (ClassByName(className) is not { } cls) return null;
        foreach (var op in cls.Operators)
            if (op.Op == "==" && op.Params.Count == 1 && op.ReturnType is IrPrimType { CName: "bool" })
                return op.CName;
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
            NativeSection.Boot     => (_kBoot, (CodeWriter?)null),
            _                      => (_kTypes, _uTypes),
        };
        static void Put(CodeWriter? w, string body) { if (w != null) { w.Line(body); w.Line(""); } }
        switch (nb.Vis)
        {
            case Visibility.Kernel: Put(kw, t); break;
            case Visibility.User: Put(uw, t); break;
            default: Put(kw, t); Put(uw, t); break;
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
            case Visibility.User: EmitTo(_uTypes, nt.C);   break;
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
        if (cls.IsModule) { EmitModule(cls); return; }

        if (!cls.IsLib)
        {
            bool isKernel = cls.Vis == Visibility.Kernel;
            EmitConcreteClass(cls, isKernel ? _kTypes : _uTypes,
                                    isKernel ? _kFwd   : _uFwd,
                                    isKernel ? _kFuncs : _uFunc, isLib: false);
            return;
        }

        bool toKernel = cls.Vis != Visibility.User;
        bool toUser   = cls.Vis != Visibility.Kernel;

        if (CanLiveInSharedHeader(cls) && toKernel && toUser)
            EmitLibClass(cls);
        else
        {
            if (toKernel) EmitConcreteClass(cls, _kTypes, _kFwd, _kFuncs, isLib: true);
            if (toUser)   EmitConcreteClass(cls, _uTypes, _uFwd, _uFunc,  isLib: true);
        }
    }

    /// <summary>
    /// Emits a module class as per-file static-inline functions with no struct or allocator.
    /// </summary>
    private void EmitModule(IrClass cls)
    {
        bool toKernel = cls.Vis != Visibility.User;
        bool toUser   = cls.Vis != Visibility.Kernel;
        if (toKernel) EmitModuleInto(cls, _kTypes, _kFuncs);
        if (toUser)   EmitModuleInto(cls, _uTypes, _uFunc);
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
    private void EmitConcreteClass(IrClass cls, CodeWriter types, CodeWriter fwd,
                           CodeWriter funcs, bool isLib)
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
                    types.Line($"{f.Type.ToCType()} {f.Name}; /* field */");
            }
            types.Blank();
        }

        if (isLib)
        {
            foreach (var m in cls.Methods)   types.Line($"{prefix}{MethodSig(m)};");
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
                    w.Line($"{f.Type.ToCType()} {f.Name}; /* field */");
            }
            w.Blank();
        }

        foreach (var m in cls.Methods)   w.Line($"static inline {MethodSig(m)};");
        foreach (var o in cls.Operators) w.Line($"static inline {OperatorSig(o)};");
        if (NeedsDtor(cls)) w.Line($"static inline {DtorSig(cls)};");
        w.Line($"static inline {AllocatorSig(cls)};");
        w.Line("");

        foreach (var m in cls.Methods)   EmitFunctionBody(m, w, isLib: true);
        foreach (var o in cls.Operators) EmitOperatorBody(o, w, isLib: true);
        EmitDtor(cls, w, isLib: true);
        EmitAllocator(cls, w, isLib: true);
    }

    /// <summary>
    /// Returns true if a library class is fully self-contained and can live in the shared header.
    /// </summary>
    private static bool CanLiveInSharedHeader(IrClass cls)
    {
        var methods = CollectionsMarshal.AsSpan(cls.Methods);
        for (int i = 0; i < methods.Length; i++)
        {
            var m = methods[i];
            if (m.Body != null) return false;
            if (ReferencesRuntime(m.ReturnType) || MentionsString(m.Native)) return false;
            
            var ps = CollectionsMarshal.AsSpan(m.Params);
            for (int j = 0; j < ps.Length; j++)
            {
                if (ReferencesRuntime(ps[j].Type)) return false;
            }
        }
        
        var operators = CollectionsMarshal.AsSpan(cls.Operators);
        for (int i = 0; i < operators.Length; i++)
        {
            var o = operators[i];
            if (o.Body != null) return false;
            if (ReferencesRuntime(o.ReturnType) || MentionsString(o.Native)) return false;
            
            var ps = CollectionsMarshal.AsSpan(o.Params);
            for (int j = 0; j < ps.Length; j++)
            {
                if (ReferencesRuntime(ps[j].Type)) return false;
            }
        }
        
        var rawFields = CollectionsMarshal.AsSpan(cls.RawFields);
        for (int i = 0; i < rawFields.Length; i++)
        {
            var rf = rawFields[i];
            if (MentionsString(rf.C)) return false;
        }
        
        if (cls.FieldInits.Count > 0) return false;
        
        var fields = CollectionsMarshal.AsSpan(cls.Fields);
        for (int i = 0; i < fields.Length; i++)
        {
            if (ReferencesRuntime(fields[i].Type)) return false;
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
            w.Line($"{cls.CName}* _o = ({cls.CName}*){Intrinsic(Roles.Alloc)}(sizeof({cls.CName}));");
            w.Line($"if (_o) *_o = ({cls.CName}){{0}};");
            w.Line($"if (_o) {Intrinsic(Roles.ObjInit)}(_o, {dtorArg});");
            foreach (var f in cls.Fields)
                if (cls.FieldInits.TryGetValue(f.Name, out var init))
                    w.Line($"if (_o) _o->{f.Name} = {EmitExpr(init)};");
            if (cls.HasInit)
            {
                var args = string.Join(", ", new[] { "_o" }.Concat((InitOf(cls)?.Params ?? []).Select(p => Mangler.Local(p.Name))));
                w.Line($"if (_o) {InitOf(cls)!.CName}({args});");
            }
            w.Line("return _o;");
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
            if (DeinitOf(cls) != null) w.Line($"{DeinitOf(cls)!.CName}(self);");
            foreach (var f in cls.Fields)
                if (IsManaged(f.Type)) w.Line(ReleaseCall(f.Type, $"self->{f.Name}"));
        }
        w.Blank();
    }

    /// <summary>
    /// Returns true if the class requires a destructor due to managed fields or a user finalizer.
    /// </summary>
    private bool NeedsDtor(IrClass cls)
    {
        if (DeinitOf(cls) != null) return true;
        var span = CollectionsMarshal.AsSpan(cls.Fields);
        for (int i = 0; i < span.Length; i++)
        {
            if (IsManaged(span[i].Type)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the _deinit method of the class, or null if none is declared.
    /// </summary>
    private static IrFunction? DeinitOf(IrClass cls)
    {
        var span = CollectionsMarshal.AsSpan(cls.Methods);
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i].Name == Lifecycle.Deinit) return span[i];
        }
        return null;
    }

    /// <summary>
    /// Returns the _init method of the class, or null if none is declared.
    /// </summary>
    private static IrFunction? InitOf(IrClass cls)
    {
        var span = CollectionsMarshal.AsSpan(cls.Methods);
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i].Name == Lifecycle.Init) return span[i];
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
        string ret = m.IsThrows ? new IrResultType(m.ReturnType).ToCType() : m.ReturnType.ToCType();
        var sb = new StringBuilder();
        sb.Append(ret).Append(' ').Append(m.CName).Append('(');
        
        bool hasParams = false;
        if (!m.IsStatic && m.OwnerClass != null)
        {
            sb.Append(Mangler.Class(m.OwnerClass)).Append("* self");
            hasParams = true;
        }
        
        for (int i = 0; i < m.Params.Count; i++)
        {
            if (hasParams) sb.Append(", ");
            var p = m.Params[i];
            sb.Append(ParamCType(p)).Append(' ').Append(Mangler.Local(p.Name));
            hasParams = true;
        }
        
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// The full C signature for an operator overload, with a self parameter for every operator
    /// except a static "as" - a factory, where self does not exist yet. Internal so tests can
    /// assert the emitted shape directly.
    /// </summary>
    internal static string OperatorSig(IrOperator o)
    {
        var sb = new StringBuilder();
        sb.Append(o.ReturnType.ToCType()).Append(' ').Append(o.CName).Append('(');
        bool needsComma = false;
        if (!o.IsStatic)
        {
            sb.Append(Mangler.Class(o.OwnerClass)).Append("* self");
            needsComma = true;
        }
        for (int i = 0; i < o.Params.Count; i++)
        {
            var p = o.Params[i];
            if (needsComma) sb.Append(", ");
            sb.Append(ParamCType(p)).Append(' ').Append(Mangler.Local(p.Name));
            needsComma = true;
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Returns the C allocator signature, threading through any constructor parameters.
    /// </summary>
    private static string AllocatorSig(IrClass cls)
    {
        var init = InitOf(cls);
        var sb = new StringBuilder();
        sb.Append(cls.CName).Append("* ").Append(Mangler.Allocator(cls.Name)).Append('(');
        if (init != null && init.Params.Count > 0)
        {
            for (int i = 0; i < init.Params.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var p = init.Params[i];
                sb.Append(ParamCType(p)).Append(' ').Append(Mangler.Local(p.Name));
            }
        }
        else
        {
            sb.Append("void");
        }
        sb.Append(')');
        return sb.ToString();
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
        string ret = fn.IsThrows ? new IrResultType(fn.ReturnType).ToCType() : fn.ReturnType.ToCType();
        var sb = new StringBuilder();
        sb.Append(ret).Append(' ').Append(fn.CName).Append('(');
        for (int i = 0; i < fn.Params.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var p = fn.Params[i];
            sb.Append(ParamCType(p)).Append(' ').Append(Mangler.Local(p.Name));
        }
        sb.Append(')');
        return sb.ToString();
    }

    #endregion

    #region Free functions

    /// <summary>
    /// Emits a free function into the translation units its flags call for: an entry function into
    /// its own realm, which lets a Hosted user entry become program.c's main(); a library function
    /// static-inline into both; anything else into its realm.
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
                EmitBlock(fn.Body, _kFuncs); _kFuncs.Line("");
                _uFunc.Line($"static inline {FuncSig(fn)}");
                EmitBlock(fn.Body, _uFunc);  _uFunc.Line("");
            }
            return;
        }

        bool isKernel = fn.Vis == Visibility.Kernel;
        var fwd   = isKernel ? _kFwd   : _uFwd;
        var funcs = isKernel ? _kFuncs : _uFunc;
        fwd.Line($"{FuncSig(fn)};");
        if (fn.Body == null)
        {
            string body = TrimC(fn.Native ?? "");
            funcs.Line($"{FuncSig(fn)}");
            using (funcs.Braces()) funcs.Line(body); funcs.Blank();
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
        string body = TrimC(fn.Native ?? "");
        w.Line($"static inline {FuncSig(fn)}");
        using (w.Braces()) w.Line(body); w.Blank();
    }

    /// <summary>
    /// Emits the entry function for a thread into its realm writer.
    /// </summary>
    private void EmitThread(IrThread t)
    {
        if (t.EntryFunc is not { } entry) return;
        var w = entry.Vis == Visibility.Kernel ? _kFuncs : _uFunc;
        w.Line($"void {entry.CName}(void* arg)");
        EmitBlock(entry.Body!, w);
        w.Blank();
    }

    #endregion

    #region Blocks and statements

    /// <summary>
    /// Emits a function body — native C text or a lowered IR block — into the given writer.
    /// </summary>
    private void EmitFunctionBody(IrFunction m, CodeWriter w, bool isLib)
    {
        string prefix = isLib ? "static inline " : "";
        if (m.Body == null)
        {
            string body = TrimC(m.Native ?? "");
            w.Line($"{prefix}{MethodSig(m)}");
            using (w.Braces()) w.Line(body); w.Blank();
            return;
        }
        w.Line($"{prefix}{MethodSig(m)}");
        EmitBlock(m.Body, w);
        w.Line("");
    }

    /// <summary>
    /// Emits an operator body — native C text or a lowered IR block — into the given writer.
    /// </summary>
    private void EmitOperatorBody(IrOperator o, CodeWriter w, bool isLib)
    {
        string prefix = isLib ? "static inline " : "";
        if (o.Body == null)
        {
            string body = TrimC(o.Native ?? "");
            w.Line($"{prefix}{OperatorSig(o)}");
            using (w.Braces()) w.Line(body); w.Blank();
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
            case IrGoto g:        w.Line($"goto {g.Label};"); break;
            case IrLabel l:       w.Line($"{l.Name}:;"); break;
            case IrNativeStmt ns: w.Line(TrimC(ns.C)); break;
            case IrBlock b:       EmitBlock(b, w); break;
            case IrUnsafeBlock u: EmitBlock(u.Body, w); break;
            case IrDeclVar dv:    EmitDeclVar(dv, w); break;
            case IrAssign a:      w.Line($"{EmitExpr(a.Target)} {a.Op.Sym()} {EmitExpr(a.Value)};"); break;
            case IrExprStmt es:   w.Line($"{EmitExpr(es.Expr)};"); break;
            case IrReturn rs:     w.Line(rs.Value == null ? "return;" : $"return {EmitExpr(rs.Value)};"); break;
            case IrBreak:         w.Line("break;"); break;
            case IrContinue:      w.Line("continue;"); break;
            case IrDebug d:       w.Line($"{module.Symbols.FloorName(Roles.EnvDebug)}({NoTrigraphs(d.Raw)});"); break;
            case IrPanic p:       w.Line($"{module.Symbols.FloorName(Roles.EnvPanic)}({NoTrigraphs(p.Raw)});"); break;
            case IrIf ifs:        EmitIf(ifs, w); break;
            case IrWhile ws:      w.Line($"while ({EmitExpr(ws.Cond)})"); EmitBlock(ws.Body, w); break;
            case IrFor fr:        EmitFor(fr, w); break;
            default: throw new System.Diagnostics.UnreachableException($"[Emitter] unhandled IrStmt: {s.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits a local variable declaration with an appropriate default when no initializer is given.
    /// </summary>
    private void EmitDeclVar(IrDeclVar dv, CodeWriter w)
    {
        if (dv.Init != null) { w.Line($"{dv.Type.ToCType()} {Mangler.Local(dv.Name)} = {EmitExpr(dv.Init)};"); return; }
        w.Line(dv.Type is IrArrayType or IrUnionType ? $"{dv.Type.ToCType()} {Mangler.Local(dv.Name)} = {{0}};"
             : IsManaged(dv.Type)                    ? $"{dv.Type.ToCType()} {Mangler.Local(dv.Name)} = NULL;"
             :                                         $"{dv.Type.ToCType()} {Mangler.Local(dv.Name)};");
    }

    /// <summary>
    /// Emits an if/else statement with optional else branch.
    /// </summary>
    private void EmitIf(IrIf ifs, CodeWriter w)
    {
        w.Line($"if ({EmitExpr(ifs.Cond)})");
        EmitBlock(ifs.Then, w);
        if (ifs.Else != null) { w.Line("else"); EmitBlock(ifs.Else, w); }
    }

    /// <summary>
    /// Emits a C-style for loop from the IR for node.
    /// </summary>
    private void EmitFor(IrFor fr, CodeWriter w)
    {
        string init = fr.Init switch
        {
            IrDeclVar dv => dv.Init != null
                ? $"{dv.Type.ToCType()} {Mangler.Local(dv.Name)} = {EmitExpr(dv.Init)}"
                : $"{dv.Type.ToCType()} {Mangler.Local(dv.Name)}",
            IrAssign aa  => $"{EmitExpr(aa.Target)} {aa.Op.Sym()} {EmitExpr(aa.Value)}",
            IrExprStmt e => EmitExpr(e.Expr),
            _            => ""
        };
        string cond = fr.Cond != null ? EmitExpr(fr.Cond) : "";
        string step = fr.Step switch
        {
            IrAssign sa  => $"{EmitExpr(sa.Target)} {sa.Op.Sym()} {EmitExpr(sa.Value)}",
            IrExprStmt e => EmitExpr(e.Expr),
            null         => "",
            _            => throw new InvalidOperationException($"[Emitter] for-step must be an assignment or expression, got {fr.Step.GetType().Name}")
        };
        w.Line($"for ({init}; {cond}; {step})");
        EmitBlock(fr.Body, w);
    }

    #endregion

    #region Expressions

    /// <summary>
    /// Emits an IR expression and returns the corresponding C text. Every node kind must be fully
    /// resolved before reaching this method; unrecognised nodes throw.
    /// </summary>
    private string EmitExpr(IrExpr e)
    {
        return e switch
        {
            IrLitInt li => li.CText ?? li.Value.ToString(),
            IrLitChar lc => lc.Codepoint.ToString(),
            IrLitFloat lf => lf.Raw,
            IrLitBool lb => lb.Value ? "true" : "false",
            IrLitString ls => $"GATA_STRLIT({IrType.String.ToCType().TrimEnd('*')}, {NoTrigraphs(ls.Raw)})",
            IrLitNull => "NULL",
            IrEnumConst ec => Mangler.EnumMember(ec.EnumName, ec.Member),
            IrVar v => v.IsRef ? $"(*{Mangler.Local(v.Name)})" : Mangler.Local(v.Name),
            IrSelfExpr => "self",

            IrFieldLoad fl => fl.Obj.Type is IrUnionType or IrResultType
                                ? $"{EmitExpr(fl.Obj)}.{fl.Field}"
                                : $"{EmitExpr(fl.Obj)}->{fl.Field}",
            IrIndex ix => ix.Obj.Type is IrArrayType
                                ? $"({EmitExpr(ix.Obj)})._[{EmitExpr(ix.Idx)}]"
                                : $"{EmitExpr(ix.Obj)}[{EmitExpr(ix.Idx)}]",
            IrStaticCall sc => $"{sc.CName}({EmitArgs(sc.Args)})",
            IrInstanceCall ic => $"{ic.CName}({EmitArgs(ic.Args, EmitExpr(ic.Recv))})",
            IrBinOp bo => $"({EmitExpr(bo.Left)} {bo.Op.Sym()} {EmitExpr(bo.Right)})",
            IrTernary tn => $"({EmitExpr(tn.Cond)} ? {EmitExpr(tn.Then)} : {EmitExpr(tn.Else)})",
            IrUnaryOp uo => $"({uo.Op.Sym()}{EmitExpr(uo.Operand)})",
            IrPostfix pf => $"({EmitExpr(pf.Operand)}{pf.Op.Sym()})",
            IrCast c => $"(({c.To.ToCType()}){EmitExpr(c.Value)})",
            IrNew n => $"{Mangler.Allocator(n.ClassName)}({EmitArgs(n.Args)})",
            IrArrayLit al => $"({al.ArrType.ToCType()}){{ {{ {EmitArgs(al.Elems)} }} }}",
            IrAddrOf ao => $"(&{EmitExpr(ao.Target)})",
            IrDeref dr => $"(*{EmitExpr(dr.Ptr)})",
            IrSizeof so => $"sizeof({so.Of.ToCType()})",
            IrStructLit sl => $"({sl.StructType.ToCType()}){{ {EmitStructFields(sl.Fields)} }}",
            IrDefault df => IsAggregate(df.Of)
                ? $"({df.Of.ToCType()}){{ 0 }}"
                : $"(({df.Of.ToCType()})0)",
            IrFuncRef fr => fr.CName,
            IrIndirectCall ic2 => $"({EmitExpr(ic2.Target)})({EmitArgs(ic2.Args)})",
            IrUnionConstruct uc => EmitUnionConstruct(uc),
            IrUnionField uf => $"{EmitExpr(uf.Union)}.payload.{UnionVariantName(uf.Union.Type, uf.VariantIndex)}.{uf.Field}",
            _ => throw new System.Diagnostics.UnreachableException($"[Emitter] unhandled IrExpr: {e.GetType().Name}")
        };
    }

    /// <summary>
    /// Renders the designated initializer body of an IrStructLit
    /// </summary>
    private string EmitStructFields(List<(string Field, IrExpr Value)> fields)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('.').Append(fields[i].Field).Append(" = ").Append(EmitExpr(fields[i].Value));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renders a comma-separated argument list, with an optional leading receiver, without LINQ
    /// enumerator or intermediate array allocations.
    /// </summary>
    private string EmitArgs(List<IrExpr> args, string? first = null)
    {
        var sb = new StringBuilder();
        if (first != null) sb.Append(first);
        for (int i = 0; i < args.Count; i++)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(EmitExpr(args[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emits a union construction expression, building the tag and payload compound literal.
    /// </summary>
    private string EmitUnionConstruct(IrUnionConstruct uc)
    {
        var u = module.Unions.First(x => x.Name == uc.T.Name);
        var variant = u.Variants[uc.VariantIndex];
        if (variant.Fields.Count == 0)
            return $"({uc.T.ToCType()}){{ .__tag = {uc.VariantIndex} }}";
        var inits = variant.Fields.Zip(uc.Args, (f, a) => $".{f.Name} = {EmitExpr(a)}");
        return $"({uc.T.ToCType()}){{ .__tag = {uc.VariantIndex}, .payload.{variant.Name} = {{ {string.Join(", ", inits)} }} }}";
    }

    /// <summary>
    /// Returns the struct field name for a union variant at the given index.
    /// </summary>
    private string UnionVariantName(IrType unionType, int idx)
    {
        return unionType is IrUnionType ut ? module.Unions.First(u => u.Name == ut.Name).Variants[idx].Name : "?";
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
        var funcs = CollectionsMarshal.AsSpan(module.FreeFunctions);
        for (int i = 0; i < funcs.Length; i++)
        {
            var fn = funcs[i];
            
            bool hasIntrinsic = false;
            var anns = CollectionsMarshal.AsSpan(fn.Annotations);
            for (int j = 0; j < anns.Length; j++)
            {
                if (anns[j] is IntrinsicAnnotation)
                {
                    hasIntrinsic = true;
                    break;
                }
            }
            
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
    private static string NoTrigraphs(string raw) =>
        raw.Contains('?') ? raw.Replace("?", "\\?") : raw;

    /// <summary>
    /// Strips uniform leading indentation from raw C text so embedded native bodies re-indent
    /// correctly at whatever depth the writer is currently at.
    /// </summary>
    private static string TrimC(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        
        ReadOnlySpan<char> textSpan = raw.AsSpan();
        int minI = int.MaxValue;
        
        // Find minimum indentation
        int offset = 0;
        while (offset < textSpan.Length)
        {
            int next = textSpan[offset..].IndexOf('\n');
            ReadOnlySpan<char> line = next >= 0 ? textSpan.Slice(offset, next) : textSpan[offset..];
            offset += next >= 0 ? next + 1 : textSpan.Length - offset;

            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            if (MemoryExtensions.IsWhiteSpace(line)) continue;
            
            int i = 0; 
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i < minI) minI = i;
        }
        
        if (minI == int.MaxValue) minI = 0;
        
        // Re-indent lines
        var sb = new StringBuilder();
        offset = 0;
        while (offset < textSpan.Length)
        {
            int next = textSpan[offset..].IndexOf('\n');
            ReadOnlySpan<char> line = next >= 0 ? textSpan.Slice(offset, next) : textSpan[offset..];
            offset += next >= 0 ? next + 1 : textSpan.Length - offset;

            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            
            if (MemoryExtensions.IsWhiteSpace(line))
            {
                sb.AppendLine();
            }
            else
            {
                ReadOnlySpan<char> sliced = line.Length > minI ? line[minI..] : line;
                sb.Append(sliced).AppendLine();
            }
        }
        
        // Trim trailing newlines directly on the StringBuilder
        while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
        {
            sb.Length--;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns true for the IR types that lower to a C struct rather than a scalar. Fixed arrays,
    /// unions, and throws Results are all wrapped in a struct by the emitter; class references are
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
            _diag.Error(Codes.MissingIntrinsic, "<runtime>", TextSpan.None,
                $"no libgata symbol provides @intrinsic({role})");
        return $"/*MISSING_INTRINSIC:{role}*/";
    }

    #endregion
}
