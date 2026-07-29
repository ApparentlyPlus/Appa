using System.Runtime.InteropServices;

namespace Appa;

internal record CollectionResult(SymbolTable Sym, HashSet<string> HasInit, HashSet<string> PreDefinedStructs,
                                                    HashSet<string> OpaqueFieldClasses, DiagnosticBag Diag);

internal sealed class SymbolCollector(DiagnosticBag diag)
{
    private readonly SymbolTable _sym = new();
    private readonly HashSet<string> _hasInit = [];
    private readonly HashSet<string> _declaredTypes = [];
    private readonly Dictionary<string, HashSet<string>> _declaredFieldNames = [];
    private readonly Dictionary<string, HashSet<string>> _declaredMethodNames = [];
    private readonly Dictionary<string, HashSet<string>> _declaredMethodSigs = [];
    private readonly Dictionary<string, HashSet<string>> _declaredOperatorSigs = [];
    private readonly HashSet<string> _declaredFuncs = [];
    private readonly HashSet<string> _declaredFuncSigs = [];
    private readonly HashSet<(string File, string Sig)>  _declaredPrivateFuncSigs  = [];
    private readonly HashSet<string> _externFuncs = [];
    private readonly HashSet<string> _preDefinedStructs = [];
    private readonly HashSet<string> _opaqueFieldClasses = [];

    /// <summary>
    /// Runs pass 1 over all programs and returns the populated symbol table.
    /// </summary>
    public CollectionResult Collect(List<(string path, Program prog)> programs)
    {
        var span = CollectionsMarshal.AsSpan(programs);
        for (int i = 0; i < span.Length; i++)
        {
            var (path, prog) = span[i];
            foreach (var item in prog.Items) P1Top(item, path);
        }
        _sym.AssignCNames();
        return new CollectionResult(_sym, _hasInit, _preDefinedStructs, _opaqueFieldClasses, diag);
    }

    /// <summary>
    /// Bind any @intrinsic(role) annotations to the C name the symbol is emitted under. Validates
    /// the role and rejects double-binding. @builtin(name) is bound the same way when allowBuiltin
    /// is set (classes and native types only).
    /// </summary>
    private void BindIntrinsics(Annotation[]? anns, string cName, string file, TextSpan span,
        bool allowKeep = false, bool allowBuiltin = false, bool allowShadows = false)
    {
        if (anns == null) return;
        foreach (var a in anns)
        {
            if (a is ShadowsAnnotation)
            {
                if (!allowShadows)
                    diag.Error(Codes.WrongAnnotationKind, file, span,
                        "'@shadows' has no effect here; it belongs on a declaration inside a realm or process");
                continue;
            }
            if (a is KeepAnnotation)
            {
                if (!allowKeep)
                    diag.Error(Codes.WrongAnnotationKind, file, span, "'@keep' has no effect here; it only matters on a free function or a class");
                continue;
            }
            if (a is BuiltinAnnotation ba)
            {
                if (!allowBuiltin)
                {
                    diag.Error(Codes.WrongAnnotationKind, file, span, "'@builtin' has no effect here; it only matters on a class or native type");
                    continue;
                }
                if (!BuiltinTypes.All.Contains(ba.Name))
                    diag.Error(Codes.UnknownIntrinsic, file, span, $"unknown @builtin type '{ba.Name}'");
                else if (_sym.Builtins.TryGetValue(ba.Name, out var prevB) && prevB != cName)
                    diag.Error(Codes.DuplicateIntrinsic, file, span, $"@builtin({ba.Name}) is already bound to '{prevB}'");
                else
                    _sym.Builtins[ba.Name] = cName;
                continue;
            }
            if (a is not IntrinsicAnnotation ia)
            {
                diag.Error(Codes.WrongAnnotationKind, file, span, "only '@intrinsic' is valid here, not '@preamble'");
                continue;
            }
            if (!Roles.All.Contains(ia.Role))
                diag.Error(Codes.UnknownIntrinsic, file, span, $"unknown @intrinsic role '{ia.Role}'");
            else if (_sym.Intrinsics.TryGetValue(ia.Role, out var prev) && prev != cName)
                diag.Error(Codes.DuplicateIntrinsic, file, span, $"@intrinsic({ia.Role}) is already bound to '{prev}'");
            else
                _sym.Intrinsics[ia.Role] = cName;
        }
    }

    /// <summary>
    /// Dispatches a single top-level item to the appropriate P1 handler.
    /// </summary>
    private void P1Top(TopLevel item, string file)
    {
        switch (item)
        {
            case NativeBlock nb:
                if (nb.Annotations != null)
                {
                    foreach (var a in nb.Annotations)
                        if (a is IntrinsicAnnotation)
                            diag.Error(Codes.WrongAnnotationKind, file, nb.Span, "only '@preamble' is valid here, not '@intrinsic'");
                }
                ScanNativeForStructs(nb.Body.C);
                break;
            case NativeTypeDecl nd:
                P1NativeType(nd, file);
                break;
            case ClassDecl cd:
                P1Class(cd, file);
                break;
            case ContextDecl ctx:
                foreach (var i in ctx.Items) P1Top(i, file);
                break;
            case ProcessDecl proc:
                foreach (var i in proc.Items) P1Top(i, file);
                break;
            case FuncDecl fd:
                P1Func(fd, file);
                break;
            case ExternFuncDecl ed:
                P1Extern(ed, file);
                break;
            case EnumDecl ed:
                if (!_declaredTypes.Add(ed.Name))
                    diag.Error(Codes.DuplicateName, file, ed.Span, $"type '{Mangler.DisplayName(ed.Name)}' is already declared");
                var enumNames = new string[ed.Members.Length];
                for (int i = 0; i < enumNames.Length; i++) enumNames[i] = ed.Members[i].Name;
                _sym.RegisterEnum(ed.Name, enumNames);
                break;
            case UnionDecl ud:
                if (!_declaredTypes.Add(ud.Name))
                    diag.Error(Codes.DuplicateName, file, ud.Span, $"type '{Mangler.DisplayName(ud.Name)}' is already declared");
                _sym.RegisterUnion(ud.Name, [.. ud.Variants]);
                break;
        }
    }

    /// <summary>
    /// Registers a class and all its fields, methods, and operators.
    /// </summary>
    private void P1Class(ClassDecl cd, string file)
    {
        // Reported but still registered, so the resolver can go on finding it.
        if (!_declaredTypes.Add(cd.Name))
            diag.Error(Codes.DuplicateName, file, cd.Span, $"type '{Mangler.DisplayName(cd.Name)}' is already declared");

        _sym.RegisterClass(cd.Name, file);
        if (cd.IsModule) _sym.Modules.Add(cd.Name);

        // @builtin binds to the readable Gata name, which is what the resolver compares against
        BindIntrinsics(cd.Annotations, cd.Name, file, cd.Span, allowKeep: true, allowBuiltin: true, allowShadows: true);

        var fieldNames = _declaredFieldNames.TryGetValue(cd.Name, out var fs)  ? fs : (_declaredFieldNames[cd.Name]  = []);
        var methodNames = _declaredMethodNames.TryGetValue(cd.Name, out var ms) ? ms : (_declaredMethodNames[cd.Name] = []);
        var methodSigs = _declaredMethodSigs.TryGetValue(cd.Name, out var ss)  ? ss : (_declaredMethodSigs[cd.Name]  = []);
        var operatorSigs = _declaredOperatorSigs.TryGetValue(cd.Name, out var os) ? os : (_declaredOperatorSigs[cd.Name] = []);

        foreach (var m in cd.Members)
        {
            switch (m)
            {
                case FieldsBlock:
                    _opaqueFieldClasses.Add(cd.Name);
                    break;
                case FieldDecl fd:
                    if (cd.IsModule)
                    {
                        diag.Error(Codes.ModuleField, file, fd.Span,
                            $"module '{Mangler.DisplayName(cd.Name)}' cannot declare the field '{fd.Name}' - modules are stateless; use a class for instance state");
                        break;
                    }
                    if (!fieldNames.Add(fd.Name) || methodNames.Contains(fd.Name))
                        diag.Error(Codes.DuplicateName, file, fd.Span,
                            $"'{Mangler.DisplayName(cd.Name)}' already declares a member '{fd.Name}'");

                    _sym.RegisterField(cd.Name, fd.Name,
                        fd.Type ?? TypeResolver.InferFieldTypeSpec(fd.Init) ?? new NamedSpec("int", fd.Span));
                    
                    if ((fd.Modifiers & Modifiers.Public) == 0) _sym.PrivateMembers.Add(new(cd.Name, fd.Name));
                    break;
                case MethodDecl md:
                    string mSigKey = md.Name + "/" + Mangler.OverloadSuffix(md.Params);
                    if (fieldNames.Contains(md.Name))
                    {
                        diag.Error(Codes.DuplicateName, file, md.Span,
                            $"'{Mangler.DisplayName(cd.Name)}' already declares a member '{md.Name}'");
                        break;
                    }
                    if (!methodSigs.Add(mSigKey))
                    {
                        diag.Error(Codes.DuplicateName, file, md.Span,
                            $"'{Mangler.DisplayName(cd.Name)}' already declares '{md.Name}' with the same parameter types");
                        break;
                    }

                    methodNames.Add(md.Name);
                    var sig = new MethodSig(md.ReturnType, [.. md.Params],
                        (md.Modifiers & Modifiers.Static) != 0 || cd.IsModule, md.Throws, md.IsEntry, [.. md.Annotations]);

                    _sym.RegisterMethod(cd.Name, md.Name, sig);

                    if ((md.Modifiers & Modifiers.Public) == 0) _sym.PrivateMembers.Add(new(cd.Name, md.Name));

                    BindIntrinsics(md.Annotations, Mangler.Method(cd.Name, md.Name, md.Params, overloaded: false), file, md.Span);

                    if (md.Name is Lifecycle.Init or Lifecycle.Deinit && md.Throws)
                        diag.Error(Codes.LifecycleThrows, file, md.Span,
                            $"'{md.Name}' cannot be 'throws'; it is called by generated allocator/destructor code that cannot handle a Result");

                    if (md.Name == Lifecycle.Init) _hasInit.Add(cd.Name);

                    if (md.Throws) _sym.RegisterThrows(md.ReturnType);
                    break;
                case OperatorDecl od:
                {
                    TypeSpec retType = od.ReturnType
                        ?? new NamedSpec(OperatorRules.DefaultReturn(od.Op, cd.Name), od.Span);

                    string opSigKey = od.Op == "as" && od.Params.Length == 1
                        ? "as/param/" + od.Params[0].Type.ToSpecString()
                        : od.Op + "/" + od.Params.Length;
                    if (!operatorSigs.Add(opSigKey))
                    {
                        diag.Error(Codes.DuplicateName, file, od.Span,
                            od.Op == "as" && od.Params.Length == 1
                                ? $"'{Mangler.DisplayName(cd.Name)}' already declares a conversion from '{Mangler.DisplayName(od.Params[0].Type.ToSpecString())}'"
                                : $"'{Mangler.DisplayName(cd.Name)}' already declares operator '{od.Op}'");
                        break;
                    }
                    
                    _sym.RegisterOperator(cd.Name, od.Op, retType, [.. od.Params]);

                    if ((od.Modifiers & Modifiers.Public) == 0) _sym.PrivateMembers.Add(new(cd.Name, $"operator {od.Op}"));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Registers a free function, including private and generic-template cases.
    /// </summary>
    private void P1Func(FuncDecl fd, string file)
    {
        if ((fd.Modifiers & Modifiers.Static) != 0)
            diag.Error(Codes.StaticOnFreeFunc, file, fd.Span,
                $"'static' has no meaning on the free function '{Mangler.DisplayName(fd.Name)}' — it is never an instance member");

        if (fd.GenericParams.Length > 0) return;

        var sig = new MethodSig(fd.ReturnType, [.. fd.Params], true, fd.Throws, fd.IsEntry, [.. fd.Annotations]);
        if ((fd.Modifiers & Modifiers.Private) != 0)
        {
            if (!_declaredPrivateFuncSigs.Add((file, fd.Name + "/" + Mangler.OverloadSuffix(fd.Params))))
            {
                diag.Error(Codes.DuplicateName, file, fd.Span,
                    $"private function '{Mangler.DisplayName(fd.Name)}' is already declared in this file with the same parameter types");
                return;
            }
            _sym.RegisterPrivateFunc(file, fd.Name, sig);
            if (fd.Throws) _sym.RegisterThrows(fd.ReturnType);
            return;
        }

        if (!_declaredFuncSigs.Add(fd.Name + "/" + Mangler.OverloadSuffix(fd.Params) + (fd.IsEntry ? "/entry" : "")))
        {
            diag.Error(Codes.DuplicateName, file, fd.Span,
                $"function '{Mangler.DisplayName(fd.Name)}' is already declared with the same parameter types");
            return;
        }
        _declaredFuncs.Add(fd.Name);
        _sym.RegisterFreeFunc(fd.Name, sig, file);
        BindIntrinsics(fd.Annotations, Mangler.FreeFunc(fd.Name, fd.Params, overloaded: false, fd.IsEntry, isExtern: false),
            file, fd.Span, allowKeep: true, allowShadows: !fd.IsEntry);
        if (fd.Throws) _sym.RegisterThrows(fd.ReturnType);
    }

    /// <summary>
    /// Registers a native type declaration as a pre-defined C struct.
    /// </summary>
    private void P1NativeType(NativeTypeDecl nd, string file)
    {
        if (!_declaredTypes.Add(nd.Name))
            diag.Error(Codes.DuplicateName, file, nd.Span, $"type '{Mangler.DisplayName(nd.Name)}' is already declared");
        _sym.RegisterClass(nd.Name, file);
        _preDefinedStructs.Add(nd.Name);
        BindIntrinsics(nd.Annotations, Mangler.Class(nd.Name), file, nd.Span, allowBuiltin: true, allowShadows: true);
    }

    /// <summary>
    /// Scans raw C text for struct/typedef names and adds them to _preDefinedStructs.
    /// </summary>
    private void ScanNativeForStructs(string raw)
    {
        foreach (var name in NativeC.ScanStructs(raw))
            _preDefinedStructs.Add(name);
    }

    /// <summary>
    /// Registers an extern function forward declaration.
    /// </summary>
    private void P1Extern(ExternFuncDecl ed, string file)
    {
        // Re-declaring the same extern across files is harmless; clashing with a
        // defined Gata function is not.
        if (_declaredFuncs.Contains(ed.Name))
        {
            if (!_externFuncs.Contains(ed.Name))
                diag.Error(Codes.DuplicateName, file, ed.Span, $"'{Mangler.DisplayName(ed.Name)}' is already declared as a function");
        }
        else
        {
            _declaredFuncs.Add(ed.Name);
            _externFuncs.Add(ed.Name);
        }
        var sig = new MethodSig(ed.ReturnType, [.. ed.Params], true, false, false, [], IsExtern: true);
        _sym.RegisterFreeFunc(ed.Name, sig, file);
        BindIntrinsics(ed.Annotations, ed.Name, file, ed.Span, allowShadows: true);
    }
}
