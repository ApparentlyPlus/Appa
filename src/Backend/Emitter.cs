namespace Appa;

sealed class Emitter(IrModule module, DiagnosticBag diag)
{
    readonly DiagnosticBag _diag = diag;
    readonly CodeWriter _sharedH = new();
    readonly CodeWriter _kPre = new();
    readonly CodeWriter _kTypes = new();
    readonly CodeWriter _kFwd = new();
    readonly CodeWriter _kFuncs = new();
    readonly CodeWriter _kBoot = new();
    readonly CodeWriter _uPre = new();
    readonly CodeWriter _uTypes = new();
    readonly CodeWriter _uFwd = new();
    readonly CodeWriter _uFunc = new();

    // Per-writer type dedup. Each distinct (writer, key) is emitted exactly once
    // into that translation unit. Keys are namespaced T: (forward typedef),
    // S: (struct or aggregate def), FP: (function-pointer typedef).
    readonly Dictionary<CodeWriter, HashSet<(char Kind, string Name)>> _emitted = [];

    // ARC-managed classes: every non-module Gata class carries a refcount header
    // and a generated destructor.
    readonly HashSet<string> _managed = InitializeManaged(module);

    // Roles for which no @intrinsic binding was found; each role is reported once.
    readonly HashSet<string> _missingRoles = [];

    /// <summary>
    /// Populates and returns the set of ARC-managed class names from the module.
    /// </summary>
    private static HashSet<string> InitializeManaged(IrModule module)
    {
        var set = new HashSet<string>(module.Classes.Count);
        foreach (var c in module.Classes)
        {
            if (!c.IsModule) set.Add(c.Name);
        }
        return set;
    }

    /// <summary>
    /// Returns true the first time the given key is seen for the given writer,
    /// suppressing duplicate emission within a single translation unit.
    /// </summary>
    bool FirstInto(CodeWriter w, char kind, string name)
    {
        if (!_emitted.TryGetValue(w, out var s)) _emitted[w] = s = [];
        return s.Add((kind, name));
    }

    /// <summary>
    /// Returns true if the IR type is a reference to an ARC-managed class.
    /// </summary>
    bool IsManaged(IrType t) => t is IrClassRef cr && _managed.Contains(cr.ClassName);

    /// <summary>
    /// Emits all sections and returns them for Layout to compose into files.
    /// </summary>
    public EmitOutput Build()
    {
        EmitForwardTypedefs();
        EmitEnums();
        EmitUnions();
        EmitFuncPtrTypedefs();
        EmitArrayTypes();
        EmitIntrinsicProtos();
        EmitResultTypedefs();

        var nativeBlocks = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.NativeBlocks);
        for (int i = 0; i < nativeBlocks.Length; i++) EmitNativeBlock(nativeBlocks[i]);

        var nativeTypes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.NativeTypes);
        for (int i = 0; i < nativeTypes.Length; i++) EmitNativeType(nativeTypes[i]);

        var classes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.Classes);
        for (int i = 0; i < classes.Length; i++) EmitClass(classes[i]);

        var freeFuncs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.FreeFunctions);
        for (int i = 0; i < freeFuncs.Length; i++) EmitFreeFunc(freeFuncs[i]);

        var processes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.Processes);
        for (int i = 0; i < processes.Length; i++)
        {
            var proc = processes[i];
            var threads = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(proc.Threads);
            for (int j = 0; j < threads.Length; j++)
            {
                EmitThread(threads[j]);
            }
        }

        return new EmitOutput(
            _sharedH.ToString(),
            _kPre.ToString(), _kTypes.ToString(), _kFwd.ToString(), _kFuncs.ToString(), _kBoot.ToString(),
            _uPre.ToString(), _uTypes.ToString(), _uFwd.ToString(), _uFunc.ToString(),
            module.Processes, module.HasKernelRealm, module.HasUserRealm);
    }

    #region Forward typedefs

    /// <summary>
    /// Forward-declares every Gata class struct in the shared header so any file
    /// can use a class pointer before its full struct is defined.
    /// </summary>
    void EmitForwardTypedefs()
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
    void EmitEnums()
    {
        var enums = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.Enums);
        for (int i = 0; i < enums.Length; i++)
        {
            var e = enums[i];
            var sb = new System.Text.StringBuilder();
            sb.Append("typedef enum { ");
            var members = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(e.Members);
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
    /// Emits a tagged-union struct for every declared Gata union type into the shared header.
    /// Each union becomes a tag integer plus a C union of per-variant payload structs.
    /// </summary>
    void EmitUnions()
    {
        var unions = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.Unions);
        for (int i = 0; i < unions.Length; i++)
        {
            var u = unions[i];
            using (_sharedH.Block("typedef struct {", $"}} {u.CName};"))
            {
                _sharedH.Line("int __tag;");
                
                bool hasFields = false;
                var variants = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(u.Variants);
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
                            
                            var sb = new System.Text.StringBuilder();
                            sb.Append("struct { ");
                            var fields = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(v.Fields);
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
        if (module.Unions.Count > 0) _sharedH.Line("");
    }

    #endregion

    #region Fixed-array types

    /// <summary>
    /// Emits a C struct wrapper for each distinct fixed-array type used in the module.
    /// Ordered by nesting depth so array-of-array element types are defined first.
    /// </summary>
    void EmitArrayTypes()
    {
        var list = new List<IrArrayType>(module.ArrayTypes.Count);
        for (int i = 0; i < module.ArrayTypes.Count; i++)
        {
            var a = module.ArrayTypes[i];
            if (a.Size > 0) list.Add(a);
        }

        if (list.Count == 0) return;

        list.Sort(new ArrayTypeDepthComparer());

        bool any = false;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
        for (int i = 0; i < span.Length; i++)
        {
            var a = span[i];
            string cn = a.ToCType();
            if (!FirstInto(_sharedH, 'S', cn)) continue;
            _sharedH.Line($"typedef struct {{ {a.Elem.ToCType()} _[{a.Size}]; }} {cn};");
            any = true;
        }
        if (any) _sharedH.Line("");
    }

    /// <summary>
    /// Comparer to sort fixed-array types by their nesting depth.
    /// </summary>
    struct ArrayTypeDepthComparer : IComparer<IrArrayType>
    {
        public readonly int Compare(IrArrayType? x, IrArrayType? y)
        {
            if (x == null) return y == null ? 0 : -1;
            if (y == null) return 1;
            return Depth(x).CompareTo(Depth(y));
        }

        static int Depth(IrType t) => t is IrArrayType a ? 1 + Depth(a.Elem) : 0;
    }

    #endregion

    #region Result types

    /// <summary>
    /// Emits Result_T struct typedefs for every throws function return type, forward-declaring
    /// any class pointer types they reference so the shared header stays self-contained.
    /// </summary>
    void EmitResultTypedefs()
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
    /// Emits a C function-pointer typedef for each distinct function-pointer type used in
    /// the module. Emitted after enums, unions, and array types so any referenced types
    /// are already visible.
    /// </summary>
    void EmitFuncPtrTypedefs()
    {
        bool any = false;
        var funcPtrs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.FuncPtrTypes);
        for (int i = 0; i < funcPtrs.Length; i++)
        {
            var f = funcPtrs[i];
            string cn = f.ToCType();
            if (!FirstInto(_sharedH, 'F', cn)) continue;

            var sb = new System.Text.StringBuilder();
            sb.Append("typedef ").Append(f.Ret.ToCType()).Append(" (*").Append(cn).Append(")(");
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
            any = true;
        }
        if (any) _sharedH.Line("");
    }

    #endregion

    #region Native blocks

    /// <summary>
    /// Emits a native block into the appropriate preamble, types, or boot section based
    /// on the block's section tag, then routes to the kernel or user writer by visibility.
    /// </summary>
    void EmitNativeBlock(IrNativeBlock nb)
    {
        string kt = TrimC(nb.KernelC), ut = TrimC(nb.UserC);
        var (kw, uw) = nb.Section switch
        {
            NativeSection.Preamble => (_kPre, _uPre),
            NativeSection.Boot     => (_kBoot, (CodeWriter?)null),
            _                      => (_kTypes, _uTypes),
        };
        static void Put(CodeWriter? w, string body) { if (w != null) { w.Line(body); w.Line(""); } }
        switch (nb.Vis)
        {
            case Visibility.Kernel: Put(kw, kt); break;
            case Visibility.User: Put(uw, ut); break;
            default: Put(kw, kt); Put(uw, ut); break;
        }
    }

    /// <summary>
    /// Emits a native type struct and typedef into the appropriate writer. When kernel and
    /// user bodies are identical the type goes to the shared header; otherwise each realm
    /// gets its own copy. Duplicate emission within a writer is suppressed via FirstInto.
    /// </summary>
    void EmitNativeType(IrNativeType nt)
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
            case Visibility.Kernel: EmitTo(_kTypes, nt.KernelC); break;
            case Visibility.User: EmitTo(_uTypes, nt.UserC);   break;
            default:
                if (nt.KernelC == nt.UserC)
                    EmitTo(_sharedH, nt.KernelC);
                else
                {
                    EmitTo(_kTypes, nt.KernelC);
                    EmitTo(_uTypes, nt.UserC);
                }
                break;
        }
    }

    #endregion

    #region Classes

    /// <summary>
    /// Dispatches a class to the appropriate emitter: module, library class, or concrete class.
    /// </summary>
    void EmitClass(IrClass cls)
    {
        if (cls.IsModule) { EmitModule(cls); return; }

        if (!cls.IsLib)
        {
            bool isKernel = cls.Vis == Visibility.Kernel;
            EmitConcreteClass(cls, isKernel ? _kTypes : _uTypes,
                                    isKernel ? _kFwd   : _uFwd,
                                    isKernel ? _kFuncs : _uFunc, isKernel, isLib: false);
            return;
        }

        bool toKernel = cls.Vis != Visibility.User;
        bool toUser   = cls.Vis != Visibility.Kernel;

        if (CanLiveInSharedHeader(cls) && toKernel && toUser)
            EmitLibClass(cls);
        else
        {
            if (toKernel) EmitConcreteClass(cls, _kTypes, _kFwd, _kFuncs, isKernel: true,  isLib: true);
            if (toUser)   EmitConcreteClass(cls, _uTypes, _uFwd, _uFunc,  isKernel: false, isLib: true);
        }
    }

    /// <summary>
    /// Emits a module class as per-file static-inline functions with no struct or allocator.
    /// </summary>
    void EmitModule(IrClass cls)
    {
        bool toKernel = cls.Vis != Visibility.User;
        bool toUser   = cls.Vis != Visibility.Kernel;
        if (toKernel) EmitModuleInto(cls, _kTypes, _kFuncs, isKernel: true);
        if (toUser)   EmitModuleInto(cls, _uTypes, _uFunc,  isKernel: false);
    }

    /// <summary>
    /// Emits forward declarations and method bodies for a module into the given writers.
    /// </summary>
    void EmitModuleInto(IrClass cls, CodeWriter types, CodeWriter funcs, bool isKernel)
    {
        foreach (var m in cls.Methods) types.Line($"static inline {MethodSig(m)};");
        types.Line("");
        foreach (var m in cls.Methods) EmitFunctionBody(m, funcs, isLib: true, isKernel);
    }

    /// <summary>
    /// Emits a concrete class into the given writers. Library classes use static-inline
    /// functions; context classes use regular linkage with separate forward declarations.
    /// </summary>
    void EmitConcreteClass(IrClass cls, CodeWriter types, CodeWriter fwd,
                           CodeWriter funcs, bool isKernel, bool isLib)
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
                foreach (var rf in cls.RawFields) types.Line(TrimC(isKernel ? rf.Kernel : rf.User));
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
            EmitFunctionBody(m, funcs, isLib, isKernel);
        }
        foreach (var o in cls.Operators)
        {
            if (!isLib) fwd.Line($"{OperatorSig(o)};");
            EmitOperatorBody(o, funcs, isLib, isKernel);
        }
        EmitDtor(cls, funcs, isLib);
    }

    /// <summary>
    /// Emits a fully self-contained library class into the shared header.
    /// </summary>
    void EmitLibClass(IrClass cls)
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
                foreach (var rf in cls.RawFields) w.Line(rf.Kernel);
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

        foreach (var m in cls.Methods)   EmitFunctionBody(m, w, isLib: true, isKernel: true);
        foreach (var o in cls.Operators) EmitOperatorBody(o, w, isLib: true, isKernel: true);
        EmitDtor(cls, w, isLib: true);
        EmitAllocator(cls, w, isLib: true);
    }

    /// <summary>
    /// Returns true if a library class is fully self-contained and can live in the shared header.
    /// </summary>
    /// <summary>
    /// Returns true if a library class is fully self-contained and can live in the shared header.
    /// </summary>
    bool CanLiveInSharedHeader(IrClass cls)
    {
        var methods = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cls.Methods);
        for (int i = 0; i < methods.Length; i++)
        {
            var m = methods[i];
            if (m.Body != null || m.NativeKernel != m.NativeUser) return false;
            if (ReferencesRuntime(m.ReturnType) || MentionsString(m.NativeKernel)) return false;
            
            var ps = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(m.Params);
            for (int j = 0; j < ps.Length; j++)
            {
                if (ReferencesRuntime(ps[j].Type)) return false;
            }
        }
        
        var operators = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cls.Operators);
        for (int i = 0; i < operators.Length; i++)
        {
            var o = operators[i];
            if (o.Body != null || o.NativeKernel != o.NativeUser) return false;
            if (ReferencesRuntime(o.ReturnType) || MentionsString(o.NativeKernel)) return false;
            
            var ps = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(o.Params);
            for (int j = 0; j < ps.Length; j++)
            {
                if (ReferencesRuntime(ps[j].Type)) return false;
            }
        }
        
        var rawFields = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cls.RawFields);
        for (int i = 0; i < rawFields.Length; i++)
        {
            var rf = rawFields[i];
            if (rf.Kernel != rf.User || MentionsString(rf.Kernel)) return false;
        }
        
        if (cls.FieldInits.Count > 0) return false;
        
        var fields = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cls.Fields);
        for (int i = 0; i < fields.Length; i++)
        {
            if (ReferencesRuntime(fields[i].Type)) return false;
        }
        
        return true;
    }

    /// <summary>
    /// Returns true if the type references any ARC-managed class or pointer to one.
    /// </summary>
    static bool ReferencesRuntime(IrType t) => t switch
    {
        IrClassRef    => true,
        IrPtrType p   => ReferencesRuntime(p.Inner),
        _             => false
    };

    /// <summary>
    /// Returns true if the raw C text mentions the gata_String type or string runtime helpers.
    /// </summary>
    static bool MentionsString(string? c) =>
        c != null && (c.Contains("gata_String") || c.Contains("gata_str_"));

    #endregion

    #region Allocators and destructors

    /// <summary>
    /// Emits the allocator function for the given class into the target writer.
    /// </summary>
    void EmitAllocator(IrClass cls, CodeWriter w, bool isLib)
    {
        string prefix = isLib ? "static inline " : "";
        string dtorArg = NeedsDtor(cls) ? Mangler.Dtor(cls.Name) : "0";
        using (w.Block($"{prefix}{AllocatorSig(cls)} {{"))
        {
            w.Line($"{cls.CName}* _o = ({cls.CName}*){Intrinsic(Roles.Alloc)}(sizeof({cls.CName}));");
            w.Line($"if (_o) {Intrinsic(Roles.ObjInit)}(_o, {dtorArg});");
            foreach (var f in cls.Fields)
                if (IsManaged(f.Type) && !cls.FieldInits.ContainsKey(f.Name))
                    w.Line($"if (_o) _o->{f.Name} = NULL;");
            foreach (var f in cls.Fields)
                if (cls.FieldInits.TryGetValue(f.Name, out var init))
                    w.Line($"if (_o) _o->{f.Name} = {EmitExpr(init)};");
            if (cls.HasInit)
            {
                var args = string.Join(", ", new[] { "_o" }.Concat((InitOf(cls)?.Params ?? []).Select(p => p.Name)));
                w.Line($"if (_o) {InitOf(cls)!.CName}({args});");
            }
            w.Line("return _o;");
        }
        w.Blank();
    }

    /// <summary>
    /// Emits the destructor for the given class if it owns managed references or declares a finalizer.
    /// </summary>
    void EmitDtor(IrClass cls, CodeWriter w, bool isLib)
    {
        if (!NeedsDtor(cls)) return;
        string prefix = isLib ? "static inline " : "";
        using (w.Block($"{prefix}{DtorSig(cls)} {{"))
        {
            w.Line($"{cls.CName}* self = ({cls.CName}*)_vp;");
            if (DeinitOf(cls) != null) w.Line($"{DeinitOf(cls)!.CName}(self);");
            foreach (var f in cls.Fields)
                if (IsManaged(f.Type)) w.Line($"{Intrinsic(Roles.Release)}(self->{f.Name});");
        }
        w.Blank();
    }

    /// <summary>
    /// Returns true if the class requires a destructor due to managed fields or a user finalizer.
    /// </summary>
    bool NeedsDtor(IrClass cls)
    {
        if (DeinitOf(cls) != null) return true;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cls.Fields);
        for (int i = 0; i < span.Length; i++)
        {
            if (IsManaged(span[i].Type)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the _deinit method of the class, or null if none is declared.
    /// </summary>
    static IrFunction? DeinitOf(IrClass cls)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cls.Methods);
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i].Name == "_deinit") return span[i];
        }
        return null;
    }

    /// <summary>
    /// Returns the _init method of the class, or null if none is declared.
    /// </summary>
    static IrFunction? InitOf(IrClass cls)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cls.Methods);
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i].Name == "_init") return span[i];
        }
        return null;
    }

    /// <summary>
    /// Emits the ARC object header field as the first struct member.
    /// </summary>
    void EmitObjHeader(CodeWriter w) =>
        w.Line($"{Intrinsic(Roles.ObjHeader)} __gata_obj; /* arc header */");

    #endregion

    #region Signatures

    /// <summary>
    /// Returns the C type for a parameter, adding one level of pointer indirection for ref parameters.
    /// </summary>
    static string ParamCType(IrParam p) =>
        p.IsRef ? $"{p.Type.ToCType()}*" : p.Type.ToCType();

    /// <summary>
    /// Returns the full C function signature for a method, including the implicit self parameter.
    /// </summary>
    /// <summary>
    /// Returns the full C function signature for a method, including the implicit self parameter.
    /// </summary>
    string MethodSig(IrFunction m)
    {
        string ret = m.IsThrows ? new IrResultType(m.ReturnType).ToCType() : m.ReturnType.ToCType();
        var sb = new System.Text.StringBuilder();
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
            sb.Append(ParamCType(p)).Append(' ').Append(p.Name);
            hasParams = true;
        }
        
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Returns the full C function signature for an operator overload, including the self parameter.
    /// </summary>
    string OperatorSig(IrOperator o)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(o.ReturnType.ToCType()).Append(' ').Append(o.CName).Append('(');
        sb.Append(Mangler.Class(o.OwnerClass)).Append("* self");
        for (int i = 0; i < o.Params.Count; i++)
        {
            var p = o.Params[i];
            sb.Append(", ").Append(ParamCType(p)).Append(' ').Append(p.Name);
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Returns the C allocator signature, threading through any constructor parameters.
    /// </summary>
    string AllocatorSig(IrClass cls)
    {
        var init = InitOf(cls);
        var sb = new System.Text.StringBuilder();
        sb.Append(cls.CName).Append("* ").Append(Mangler.Allocator(cls.Name)).Append('(');
        if (init != null && init.Params.Count > 0)
        {
            for (int i = 0; i < init.Params.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var p = init.Params[i];
                sb.Append(ParamCType(p)).Append(' ').Append(p.Name);
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
    string DtorSig(IrClass cls) => $"void {Mangler.Dtor(cls.Name)}(void* _vp)";

    /// <summary>
    /// Returns the full C function signature for a free function.
    /// </summary>
    string FuncSig(IrFunction fn)
    {
        string ret = fn.IsThrows ? new IrResultType(fn.ReturnType).ToCType() : fn.ReturnType.ToCType();
        var sb = new System.Text.StringBuilder();
        sb.Append(ret).Append(' ').Append(fn.CName).Append('(');
        for (int i = 0; i < fn.Params.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var p = fn.Params[i];
            sb.Append(ParamCType(p)).Append(' ').Append(p.Name);
        }
        sb.Append(')');
        return sb.ToString();
    }

    #endregion

    #region Free functions

    /// <summary>
    /// Emits a free function into the appropriate translation unit sections based on its flags.
    /// Entry functions go to the kernel; library functions are static-inline into both units;
    /// all others are forwarded and emitted into the realm they belong to.
    /// </summary>
    void EmitFreeFunc(IrFunction fn)
    {
        if (fn.IsEntry)
        {
            _kFwd.Line($"void {fn.CName}(void);");
            _kFuncs.Line($"void {fn.CName}(void)");
            EmitBlock(fn.Body!, _kFuncs);
            _kFuncs.Line("");
            return;
        }

        if (fn.IsLib)
        {
            _kFwd.Line($"static inline {FuncSig(fn)};");
            _uFwd.Line($"static inline {FuncSig(fn)};");
            if (fn.Body == null)
            {
                EmitLibFreeFuncNative(fn, _kFuncs, isKernel: true);
                EmitLibFreeFuncNative(fn, _uFunc,  isKernel: false);
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
            string body = TrimC(isKernel ? fn.NativeKernel ?? "" : fn.NativeUser ?? "");
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
    /// Emits a native library free function into the given writer for the given realm.
    /// </summary>
    void EmitLibFreeFuncNative(IrFunction fn, CodeWriter w, bool isKernel)
    {
        string body = TrimC(isKernel ? fn.NativeKernel ?? "" : fn.NativeUser ?? "");
        w.Line($"static inline {FuncSig(fn)}");
        using (w.Braces()) w.Line(body); w.Blank();
    }

    /// <summary>
    /// Emits the entry function for a thread into its realm writer.
    /// </summary>
    void EmitThread(IrThread t)
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
    void EmitFunctionBody(IrFunction m, CodeWriter w, bool isLib, bool isKernel)
    {
        string prefix = isLib ? "static inline " : "";
        if (m.Body == null)
        {
            string body = TrimC(isKernel ? m.NativeKernel ?? "" : m.NativeUser ?? "");
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
    void EmitOperatorBody(IrOperator o, CodeWriter w, bool isLib, bool isKernel)
    {
        string prefix = isLib ? "static inline " : "";
        if (o.Body == null)
        {
            string body = TrimC(isKernel ? o.NativeKernel ?? "" : o.NativeUser ?? "");
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
    void EmitBlock(IrBlock b, CodeWriter w)
    {
        using var _ = w.Braces();
        foreach (var s in b.Stmts) EmitStmt(s, w);
    }

    /// <summary>
    /// Dispatches a single IR statement to its C emission handler.
    /// </summary>
    void EmitStmt(IrStmt s, CodeWriter w)
    {
        switch (s)
        {
            case IrRaw r:         w.Line(r.Code); break;
            case IrNativeStmt ns: w.Line(TrimC(ns.KernelC)); break;
            case IrBlock b:       EmitBlock(b, w); break;
            case IrUnsafeBlock u: EmitBlock(u.Body, w); break;
            case IrDeclVar dv:    EmitDeclVar(dv, w); break;
            case IrAssign a:      w.Line($"{EmitExpr(a.Target)} {a.Op} {EmitExpr(a.Value)};"); break;
            case IrExprStmt es:   w.Line($"{EmitExpr(es.Expr)};"); break;
            case IrReturn rs:     w.Line(rs.Value == null ? "return;" : $"return {EmitExpr(rs.Value)};"); break;
            case IrBreak:         w.Line("break;"); break;
            case IrContinue:      w.Line("continue;"); break;
            case IrDebug d:       w.Line($"_env_dbg({d.Raw});"); break;
            case IrPanic p:       w.Line($"_env_panic({p.Raw});"); break;
            case IrIf ifs:        EmitIf(ifs, w); break;
            case IrWhile ws:      w.Line($"while ({EmitExpr(ws.Cond)})"); EmitBlock(ws.Body, w); break;
            case IrFor fr:        EmitFor(fr, w); break;
            default: throw new InvalidOperationException($"[Emitter] unhandled IrStmt: {s.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits a local variable declaration with an appropriate default when no initializer is given.
    /// </summary>
    void EmitDeclVar(IrDeclVar dv, CodeWriter w)
    {
        if (dv.Init != null) { w.Line($"{dv.Type.ToCType()} {dv.Name} = {EmitExpr(dv.Init)};"); return; }
        w.Line(dv.Type is IrArrayType or IrUnionType ? $"{dv.Type.ToCType()} {dv.Name} = {{0}};"
             : IsManaged(dv.Type)                    ? $"{dv.Type.ToCType()} {dv.Name} = NULL;"
             :                                          $"{dv.Type.ToCType()} {dv.Name};");
    }

    /// <summary>
    /// Emits an if/else statement with optional else branch.
    /// </summary>
    void EmitIf(IrIf ifs, CodeWriter w)
    {
        w.Line($"if ({EmitExpr(ifs.Cond)})");
        EmitBlock(ifs.Then, w);
        if (ifs.Else != null) { w.Line("else"); EmitBlock(ifs.Else, w); }
    }

    /// <summary>
    /// Emits a C-style for loop from the IR for node.
    /// </summary>
    void EmitFor(IrFor fr, CodeWriter w)
    {
        string init = fr.Init switch
        {
            IrDeclVar dv => dv.Init != null
                ? $"{dv.Type.ToCType()} {dv.Name} = {EmitExpr(dv.Init)}"
                : $"{dv.Type.ToCType()} {dv.Name}",
            IrAssign aa  => $"{EmitExpr(aa.Target)} {aa.Op} {EmitExpr(aa.Value)}",
            IrExprStmt e => EmitExpr(e.Expr),
            _            => ""
        };
        string cond = fr.Cond != null ? EmitExpr(fr.Cond) : "";
        string step = fr.Step != null ? EmitExpr(fr.Step) : "";
        w.Line($"for ({init}; {cond}; {step})");
        EmitBlock(fr.Body, w);
    }

    #endregion

    #region Expressions

    /// <summary>
    /// Emits an IR expression and returns the corresponding C text. Every node kind
    /// must be fully resolved before reaching this method; unrecognised nodes throw.
    /// </summary>
    string EmitExpr(IrExpr e) => e switch
    {
        IrLitInt    li => li.CText ?? li.Value.ToString(),
        IrLitChar   lc => lc.Codepoint.ToString(),
        IrLitFloat  lf => lf.Raw,
        IrLitBool   lb => lb.Value ? "true" : "false",
        IrLitString ls => $"GATA_STRLIT({IrType.String.ToCType().TrimEnd('*')}, {ls.Raw})",
        IrLitNull      => "NULL",
        IrEnumConst ec => Mangler.EnumMember(ec.EnumName, ec.Member),
        IrVar      v   => v.IsRef ? $"(*{v.Name})" : v.Name,
        IrSelfExpr     => "self",
        IrFieldLoad fl => fl.Obj.Type is IrUnionType
                            ? $"{EmitExpr(fl.Obj)}.{fl.Field}"
                            : $"{EmitExpr(fl.Obj)}->{fl.Field}",
        IrIndex    ix  => ix.Obj.Type is IrArrayType
                            ? $"({EmitExpr(ix.Obj)})._[{EmitExpr(ix.Idx)}]"
                            : $"{EmitExpr(ix.Obj)}[{EmitExpr(ix.Idx)}]",
        IrStaticCall   sc  => $"{sc.CName}({string.Join(", ", sc.Args.Select(EmitExpr))})",
        IrInstanceCall ic  => $"{ic.CName}({string.Join(", ", new[] { EmitExpr(ic.Recv) }.Concat(ic.Args.Select(EmitExpr)))})",
        IrBinOp    bo => $"({EmitExpr(bo.Left)} {bo.Op} {EmitExpr(bo.Right)})",
        IrTernary  tn => $"({EmitExpr(tn.Cond)} ? {EmitExpr(tn.Then)} : {EmitExpr(tn.Else)})",
        IrUnaryOp  uo => $"{uo.Op}{EmitExpr(uo.Operand)}",
        IrPostfix  pf => $"{EmitExpr(pf.Operand)}{pf.Op}",
        IrCast     c  => $"(({c.To.ToCType()}){EmitExpr(c.Value)})",
        IrNew      n  => $"{Mangler.Allocator(n.ClassName)}({string.Join(", ", n.Args.Select(EmitExpr))})",
        IrArrayLit al => $"({al.ArrType.ToCType()}){{ {{ {string.Join(", ", al.Elems.Select(EmitExpr))} }} }}",
        IrAddrOf   ao => $"(&{EmitExpr(ao.Target)})",
        IrDeref    dr => $"(*{EmitExpr(dr.Ptr)})",
        IrSizeof   so => $"sizeof({so.Of.ToCType()})",
        IrDefault  df => $"(({df.Of.ToCType()})0)",
        IrFuncRef  fr => fr.CName,
        IrIndirectCall  ic2 => $"({EmitExpr(ic2.Target)})({string.Join(", ", ic2.Args.Select(EmitExpr))})",
        IrUnionConstruct uc => EmitUnionConstruct(uc),
        IrUnionField     uf => $"{EmitExpr(uf.Union)}.payload.{UnionVariantName(uf.Union.Type, uf.VariantIndex)}.{uf.Field}",
        _ => throw new InvalidOperationException($"[Emitter] Unhandled IrExpr: {e.GetType().Name}")
    };

    /// <summary>
    /// Emits a union construction expression, building the tag and payload compound literal.
    /// </summary>
    string EmitUnionConstruct(IrUnionConstruct uc)
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
    string UnionVariantName(IrType unionType, int idx) =>
        unionType is IrUnionType ut ? module.Unions.First(u => u.Name == ut.Name).Variants[idx].Name : "?";

    #endregion

    #region Intrinsic prototypes

    /// <summary>
    /// Emits a static-inline prototype into the shared header for every free function
    /// annotated with an intrinsic role binding. Skips duplicates via FirstInto.
    /// </summary>
    void EmitIntrinsicProtos()
    {
        bool any = false;
        var funcs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(module.FreeFunctions);
        for (int i = 0; i < funcs.Length; i++)
        {
            var fn = funcs[i];
            
            bool hasIntrinsic = false;
            var anns = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fn.Annotations);
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
    /// Strips uniform leading indentation from raw C text so embedded native bodies
    /// re-indent correctly at whatever depth the writer is currently at.
    /// </summary>
    static string TrimC(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        
        ReadOnlySpan<char> textSpan = raw.AsSpan();
        int minI = int.MaxValue;
        
        // Pass 1: Find minimum indentation
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
        
        // Pass 2: Re-indent lines
        var sb = new System.Text.StringBuilder();
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
        while (sb.Length > 0 && char.IsWhiteSpace(sb[sb.Length - 1]))
        {
            sb.Length--;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolves a compiler runtime role to the C symbol name bound via an intrinsic annotation.
    /// Emits a diagnostic and returns a placeholder comment if no binding exists.
    /// </summary>
    string Intrinsic(string role)
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
