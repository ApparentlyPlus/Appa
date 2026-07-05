using System.Runtime.InteropServices;

namespace Appa;

// Result of declaration collection: the populated SymbolTable and the auxiliary
// set the resolver needs.
internal record CollectionResult(SymbolTable Sym, HashSet<string> HasInit, HashSet<string> PreDefinedStructs,
                                                    HashSet<string> OpaqueFieldClasses, DiagnosticBag Diag);

// Pass 1 - declaration collection. Populates the SymbolTable with classes, fields,
// methods, operators, free functions, throws registrations, and @intrinsic bindings.
internal sealed class SymbolCollector(DiagnosticBag diag)
{
    private readonly SymbolTable _sym = new();
    private readonly HashSet<string> _hasInit = [];
    private readonly HashSet<string> _declaredClasses = [];
    private readonly Dictionary<string, HashSet<string>> _declaredFieldNames = [];
    private readonly Dictionary<string, HashSet<string>> _declaredMethodNames = [];
    private readonly Dictionary<string, HashSet<string>> _declaredMethodSigs = [];
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
    /// Bind any @intrinsic(role) annotations to the C name the symbol is emitted under.
    /// Validates the role and rejects double-binding. @builtin(name) is bound the same
    /// way when allowBuiltin is set (classes and native types only).
    /// </summary>
    private void BindIntrinsics(Annotation[]? anns, string cName, string file, TextSpan span,
        bool allowKeep = false, bool allowBuiltin = false)
    {
        if (anns == null) return;
        foreach (var a in anns)
        {
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
                ScanNativeForStructs(nb.Body.KernelC);
                ScanNativeForStructs(nb.Body.UserC);
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
            case FuncDecl fd:
                P1Func(fd, file);
                break;
            case ExternFuncDecl ed:
                P1Extern(ed, file);
                break;
            case EnumDecl ed:
                var enumNames = new string[ed.Members.Length];
                for (int i = 0; i < enumNames.Length; i++) enumNames[i] = ed.Members[i].Name;
                _sym.RegisterEnum(ed.Name, enumNames);
                break;
            case UnionDecl ud:
                _sym.RegisterUnion(ud.Name, [.. ud.Variants]);
                break;
        }
    }

    /// <summary>
    /// Registers a class and all its fields, methods, and operators.
    /// </summary>
    private void P1Class(ClassDecl cd, string file)
    {
        // Throw an error if the class name is already declared, but still register it so the resolver can find it.
        if (!_declaredClasses.Add(cd.Name))
            diag.Error(Codes.DuplicateName, file, cd.Span, $"type '{Mangler.DisplayName(cd.Name)}' is already declared");

        // Register the class in the symbol table, and if it's a module, add it to the modules set.
        _sym.RegisterClass(cd.Name, file);
        if (cd.IsModule) _sym.Modules.Add(cd.Name);

        // Bind any @builtin(name) annotation to this class's readable Gata name - the
        // resolver compares against this name throughout, not the (not-yet-assigned) C name.
        BindIntrinsics(cd.Annotations, cd.Name, file, cd.Span, allowBuiltin: true);


        var fieldNames = _declaredFieldNames.TryGetValue(cd.Name, out var fs)  ? fs : (_declaredFieldNames[cd.Name]  = []);
        var methodNames = _declaredMethodNames.TryGetValue(cd.Name, out var ms) ? ms : (_declaredMethodNames[cd.Name] = []);
        var methodSigs = _declaredMethodSigs.TryGetValue(cd.Name, out var ss)  ? ss : (_declaredMethodSigs[cd.Name]  = []);

        foreach (var m in cd.Members)
        {
            switch (m)
            {
                case FieldsBlock:
                    _opaqueFieldClasses.Add(cd.Name);
                    break;
                case FieldDecl fd when fd.Type != "__native__":
                    if (cd.IsModule)
                    {
                        diag.Error(Codes.UndefinedVariable, file, fd.Span,
                            $"module '{Mangler.DisplayName(cd.Name)}' cannot declare the field '{fd.Name}'");
                        break;
                    }
                    if (!fieldNames.Add(fd.Name) || methodNames.Contains(fd.Name))
                        diag.Error(Codes.DuplicateName, file, fd.Span,
                            $"'{Mangler.DisplayName(cd.Name)}' already declares a member '{fd.Name}'");
                    _sym.RegisterField(cd.Name, fd.Name, fd.Type ?? "");
                    
                    // Private by default. A member needs an explicit 'public' to be callable
                    // from outside its declaring class or module.
                    if (!fd.Modifiers.HasFlag(Modifiers.Public)) _sym.PrivateMembers.Add(new(cd.Name, fd.Name));
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
                            $"'{cd.Name}' already declares '{md.Name}' with the same parameter types");
                        break;
                    }

                    // Register the method in the symbol table, and if it's private, add it to the private members set.
                    methodNames.Add(md.Name);
                    var sig = new MethodSig(md.ReturnType ?? "void", [.. md.Params],
                        md.Modifiers.HasFlag(Modifiers.Static) || cd.IsModule, md.Throws, md.IsEntry, [.. md.Annotations]);

                    _sym.RegisterMethod(cd.Name, md.Name, sig);

                    // Private by default. A member needs an explicit 'public' to be callable from outside its declaring class or module.
                    if (!md.Modifiers.HasFlag(Modifiers.Public)) _sym.PrivateMembers.Add(new(cd.Name, md.Name));

                    // Bind any @intrinsic annotations to the C name the method is emitted under.
                    BindIntrinsics(md.Annotations, Mangler.Method(cd.Name, md.Name, md.Params, overloaded: false), file, md.Span);

                    // If the method is named "_init", add the class name to the _hasInit set so the resolver can check for missing initializers.
                    if (md.Name == "_init") _hasInit.Add(cd.Name);

                    // If the method throws, register its return type in the symbol table so the resolver can check for missing result typedefs.
                    if (md.Throws) _sym.RegisterThrows(md.ReturnType ?? "int");
                    break;
                case OperatorDecl od:
                    // Register the operator in the symbol table, using the operator name as the return type if it's not specified, 
                    // and using "void" for assignment operators.
                    _sym.RegisterOperator(cd.Name, od.Op,
                        od.ReturnType ?? (od.Op == "[]=" ? "void" : cd.Name), [.. od.Params]);
                    break;
            }
        }
    }

    /// <summary>
    /// Registers a free function, including private and generic-template cases.
    /// </summary>
    private void P1Func(FuncDecl fd, string file)
    {
        // `static` only means anything on a class/module method; it's a category
        // error on a free function, not a redundant-but-harmless spelling.
        if (fd.Modifiers.HasFlag(Modifiers.Static))
            diag.Error(Codes.StaticOnFreeFunc, file, fd.Span,
                $"'static' has no meaning on the free function '{fd.Name}' — it is never an instance member");

        if (fd.GenericParams.Length > 0) return;

        var sig = new MethodSig(fd.ReturnType ?? "void", [.. fd.Params], true, fd.Throws, fd.IsEntry, [.. fd.Annotations]);
        if (fd.Modifiers.HasFlag(Modifiers.Private))
        {
            if (!_declaredPrivateFuncSigs.Add((file, fd.Name + "/" + Mangler.OverloadSuffix(fd.Params))))
            {
                diag.Error(Codes.DuplicateName, file, fd.Span,
                    $"private function '{fd.Name}' is already declared in this file with the same parameter types");
                return;
            }
            _sym.RegisterPrivateFunc(file, fd.Name, sig);
            if (fd.Throws) _sym.RegisterThrows(fd.ReturnType ?? "int");
            return;
        }

        if (!_declaredFuncSigs.Add(fd.Name + "/" + Mangler.OverloadSuffix(fd.Params)))
        {
            diag.Error(Codes.DuplicateName, file, fd.Span,
                $"function '{fd.Name}' is already declared with the same parameter types");
            return;
        }
        _declaredFuncs.Add(fd.Name);
        _sym.RegisterFreeFunc(fd.Name, sig, file);
        BindIntrinsics(fd.Annotations, Mangler.FreeFunc(fd.Name, fd.Params, overloaded: false, fd.IsEntry, isExtern: false),
            file, fd.Span, allowKeep: true);
        if (fd.Throws) _sym.RegisterThrows(fd.ReturnType ?? "int");
    }

    /// <summary>
    /// Registers a native type declaration as a pre-defined C struct.
    /// </summary>
    private void P1NativeType(NativeTypeDecl nd, string file)
    {
        if (!_declaredClasses.Add(nd.Name))
            diag.Error(Codes.DuplicateName, file, nd.Span, $"type '{nd.Name}' is already declared");
        _sym.RegisterClass(nd.Name, file);
        _preDefinedStructs.Add(nd.Name);
        BindIntrinsics(nd.Annotations, Mangler.Class(nd.Name), file, nd.Span, allowBuiltin: true);
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
                diag.Error(Codes.DuplicateName, file, ed.Span, $"'{ed.Name}' is already declared as a function");
        }
        else
        {
            _declaredFuncs.Add(ed.Name);
            _externFuncs.Add(ed.Name);
        }
        var sig = new MethodSig(ed.ReturnType ?? "void", [.. ed.Params], true, false, false, [], IsExtern: true);
        _sym.RegisterFreeFunc(ed.Name, sig, file);
        BindIntrinsics(ed.Annotations, ed.Name, file, ed.Span);
    }
}
