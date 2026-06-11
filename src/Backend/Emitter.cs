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
    public EmitOutput Build() => throw new NotImplementedException();

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
                foreach (var rf in cls.RawFields) types.Line(rf.Kernel);
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

    #region Stubs

    /// <summary>
    /// Resolves a compiler runtime role to the C symbol bound via an intrinsic annotation.
    /// Implemented in a later commit.
    /// </summary>
    string Intrinsic(string role) => throw new NotImplementedException();

    /// <summary>
    /// Emits the body of a function or method into the given writer.
    /// Implemented in a later commit.
    /// </summary>
    void EmitFunctionBody(IrFunction m, CodeWriter w, bool isLib, bool isKernel) =>
        throw new NotImplementedException();

    /// <summary>
    /// Emits the body of an operator overload into the given writer.
    /// Implemented in a later commit.
    /// </summary>
    void EmitOperatorBody(IrOperator o, CodeWriter w, bool isLib, bool isKernel) =>
        throw new NotImplementedException();

    /// <summary>
    /// Emits the statements of a block into the given writer.
    /// Implemented in a later commit.
    /// </summary>
    void EmitBlock(IrBlock b, CodeWriter w) => throw new NotImplementedException();

    /// <summary>
    /// Emits an expression and returns its C text representation.
    /// Implemented in a later commit.
    /// </summary>
    string EmitExpr(IrExpr e) => throw new NotImplementedException();

    #endregion
}
