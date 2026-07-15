using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace Appa;

internal enum SymKind { Class, Field, Method, FreeFunc, Operator }

// The closed vocabulary of compiler runtime roles. A libgata symbol annotated
// @intrinsic(<role>) fills the role; the compiler emits the bound C name. This
// enum IS the compiler-runtime contract surface.
internal static class Roles
{
    public const string Alloc          = "alloc";
    public const string Retain         = "retain";
    public const string Release        = "release";
    public const string ObjHeader      = "obj_header";
    public const string ObjInit        = "obj_init";
    public const string StringifyInt   = "stringify_int";
    public const string StringifyFloat = "stringify_float";
    public const string StringifyChar  = "stringify_char";

    // The environment floor's C names, bound to their @extern declaration in
    // libgata (see Sys.g/Mem.g/Console.g) so the compiler never hardcodes them.
    public const string EnvDebug       = "env_debug";
    public const string EnvPanic       = "env_panic";
    public const string EnvProcCreate  = "env_proc_create";
    public const string EnvProcHide    = "env_proc_hide";
    public const string EnvThreadSpawn = "env_thread_spawn";
    public const string EnvRead        = "env_read";
    public const string EnvAlloc       = "env_alloc";

    public static readonly FrozenSet<string> All = FrozenSet.ToFrozenSet(
    [
        Alloc, Retain, Release, ObjHeader, ObjInit,
        StringifyInt, StringifyFloat, StringifyChar,
        EnvDebug, EnvPanic, EnvProcCreate, EnvProcHide, EnvThreadSpawn, EnvRead, EnvAlloc
    ]);
}

// The closed vocabulary of compiler builtin types. A libgata class or native type
// declaration annotated @builtin(<name>) fills the slot; the compiler resolves the
// name from this table instead of comparing type names against a literal string.
internal static class BuiltinTypes
{
    public const string String  = "String";
    public const string Process = "Process";
    public const string Thread  = "Thread";

    public static readonly FrozenSet<string> All = FrozenSet.ToFrozenSet([String, Process, Thread]);
}

/// <summary>
/// The signature of a method or free function as collected from the AST.
/// </summary>
internal record MethodSig(
    string           ReturnType,
    List<Param>      Params,
    bool             IsStatic,
    bool             IsThrows,
    bool             IsEntry,
    List<Annotation> Annotations,
    bool             IsExtern = false
);

// Identity of a class member: its owning class plus its name. A typed key rather
// than a "Class.name" string — owner and name are never concatenated or re-split,
// and field/method/operator live in separate tables so kinds can't collide.
/// <summary>
/// Identifies a class member by its owning class and member name.
/// </summary>
internal readonly record struct MemberKey(string Owner, string Name);

// A declared symbol. Its C name is assigned once — by the Mangler, after all
// declarations are collected (overload-ness is known then) — and read by both the
// definition and every call site, so they cannot disagree.
/// <summary>
/// A single declared symbol with its kind, type, and optionally its method signature.
/// </summary>
internal sealed record Symbol(string Name, SymKind Kind, string Type, string? Owner, MethodSig? Sig)
{
    public string CName  { get; set; } = "";
    public string Module { get; set; } = "";
}

/// <summary>
/// Declaration registry for classes, fields, methods, free functions, operators, enums, and unions.
/// Populated by SymbolCollector during pass 1; read by the type resolver during pass 2.
/// </summary>
internal sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol> _classes = [];
    private readonly Dictionary<MemberKey, Symbol> _fields = [];
    private readonly Dictionary<MemberKey, List<Symbol>> _methods = [];
    private readonly Dictionary<string, List<Symbol>> _funcs = [];
    private readonly Dictionary<MemberKey, List<Symbol>> _operators = [];

    // Every accepted primitive spelling.
    public static readonly FrozenSet<string> Primitives = PrimTypes.Spellings;

    // Result_T typedefs needed by throws functions: name -> inner Gata type.
    public Dictionary<string, string> ResultTypedefs { get; } = [];

    // Declaring source files seen during collection.
    public HashSet<string> Modules { get; } = [];

    // role -> bound C symbol name, from @intrinsic annotations.
    public Dictionary<string, string> Intrinsics { get; } = [];

    // builtin type name -> bound Gata declaration name, from @builtin annotations.
    public Dictionary<string, string> Builtins { get; } = [];

    /// <summary>
    /// Returns the IR type for a given builtin name (String/Process/Thread) if libgata
    /// declared it via @builtin, or null if unbound. The single place that maps these
    /// names to their IR shape - callers never hardcode the mapping themselves.
    /// </summary>
    public IrType? ResolveBuiltinType(string name)
    {
        if (!Builtins.ContainsKey(name)) return null;
        return name switch
        {
            BuiltinTypes.String => IrType.String,
            BuiltinTypes.Process or BuiltinTypes.Thread => new IrPtrType(IrType.Void),
            _ => null
        };
    }

    /// <summary>
    /// Returns the C name bound to the given intrinsic role, or null if unbound.
    /// </summary>
    public string? IntrinsicOrNull(string role)
    {
        return Intrinsics.TryGetValue(role, out var n) ? n : null;
    }

    #region Registration

    /// <summary>
    /// Registers a class declaration from the given source file.
    /// </summary>
    public void RegisterClass(string name, string module)
    {
        _classes[name] = new Symbol(name, SymKind.Class, name, null, null) { CName = Mangler.Class(name), Module = module };
    }

    /// <summary>
    /// Registers a field on the named class.
    /// </summary>
    public void RegisterField(string cls, string field, string type)
    {
        _fields[new(cls, field)] = new Symbol(field, SymKind.Field, type, cls, null);
    }

    /// <summary>
    /// Registers a method overload on the named class.
    /// </summary>
    public void RegisterMethod(string cls, string name, MethodSig sig)
    {
        Bucket(_methods, new(cls, name)).Add(new Symbol(name, SymKind.Method, sig.ReturnType, cls, sig));
    }

    /// <summary>
    /// Registers a free function overload from the given source file.
    /// </summary>
    public void RegisterFreeFunc(string name, MethodSig sig, string module)
    {
        Bucket(_funcs, name).Add(new Symbol(name, SymKind.FreeFunc, sig.ReturnType, null, sig) { Module = module });
    }

    /// <summary>
    /// Registers an operator overload on the named class. Every operator except 'as' has
    /// exactly one declaration per (class, symbol) in a well-formed program (the caller is
    /// responsible for rejecting duplicates); 'as' alone can have several, one per distinct
    /// return type, since it has no parameter to distinguish overloads by. CNames are assigned
    /// later in AssignCNames, once every overload for the bucket is known.
    /// </summary>
    public void RegisterOperator(string cls, string op, string returnType, List<Param> @params)
    {
        Bucket(_operators, new(cls, op)).Add(new Symbol(op, SymKind.Operator, returnType, cls,
            new MethodSig(returnType, @params, IsStatic: false, IsThrows: false, IsEntry: false, Annotations: [])));
    }

    /// <summary>
    /// Records that a throws function returns the given type, ensuring a Result typedef is emitted.
    /// </summary>
    public void RegisterThrows(string returnType)
    {
        string c = PrimTypes.Canon(returnType);
        ResultTypedefs.TryAdd($"Result_{c}", c);
    }

    private static List<Symbol> Bucket<K>(Dictionary<K, List<Symbol>> d, K key) where K : notnull
    {
        ref var l = ref CollectionsMarshal.GetValueRefOrAddDefault(d, key, out bool exists);
        if (!exists) l = [];
        return l!;
    }

    /// <summary>
    /// Assigns C names to all methods and free functions once all declarations are collected.
    /// </summary>
    public void AssignCNames()
    {
        foreach (var (key, list) in _methods)
        {
            bool ov = list.Count > 1;
            var span = CollectionsMarshal.AsSpan(list);
            for (int i = 0; i < span.Length; i++)
            {
                span[i].CName = Mangler.Method(key.Owner, key.Name, span[i].Sig!.Params, ov);
            }
        }
        foreach (var (name, list) in _funcs)
        {
            bool ov = list.Count > 1;
            var span = CollectionsMarshal.AsSpan(list);
            for (int i = 0; i < span.Length; i++)
            {
                var s = span[i];
                s.CName = Mangler.FreeFunc(name, s.Sig!.Params, ov, s.Sig.IsEntry, s.Sig.IsExtern);
            }
        }
        foreach (var ((file, name), list) in _privateFuncs)
        {
            bool ov = list.Count > 1;
            string token = Mangler.FileToken(file);
            var span = CollectionsMarshal.AsSpan(list);
            for (int i = 0; i < span.Length; i++)
            {
                span[i].CName = Mangler.PrivateFreeFunc(token, name, span[i].Sig!.Params, ov);
            }
        }
        foreach (var (key, list) in _operators)
        {
            bool ov = list.Count > 1;
            var span = CollectionsMarshal.AsSpan(list);
            for (int i = 0; i < span.Length; i++)
            {
                span[i].CName = Mangler.Operator(key.Owner, key.Name, span[i].Sig!.Params, span[i].Sig!.ReturnType, ov);
            }
        }
    }

    #endregion

    #region Enums and unions

    // Enum types: name -> member names. Globally visible like primitives.
    public Dictionary<string, HashSet<string>> Enums { get; } = [];

    /// <summary>
    /// Registers an enum type and its member names.
    /// </summary>
    public void RegisterEnum(string name, IEnumerable<string> members)
    {
        Enums[name] = [.. members];
    }

    public bool IsEnum(string name)
    {
        return Enums.ContainsKey(name);
    }

    /// <summary>
    /// Returns true if the name is a declared enum type.
    /// </summary>
    public bool IsEnum(ReadOnlySpan<char> name)
    {
        return Enums.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(name);
    }

    /// <summary>
    /// Returns true if the member belongs to the named enum.
    /// </summary>
    public bool IsEnumMember(string e, string m)
    {
        return Enums.TryGetValue(e, out var ms) && ms.Contains(m);
    }

    // Union types: name -> variant list. Globally visible and not generic.
    public Dictionary<string, List<UnionVariant>> Unions { get; } = [];

    /// <summary>
    /// Registers a union type and its variants.
    /// </summary>
    public void RegisterUnion(string name, List<UnionVariant> variants)
    {
        Unions[name] = variants;
    }

    public bool IsUnion(string name)
    {
        return Unions.ContainsKey(name);
    }

    /// <summary>
    /// Returns true if the name is a declared union type.
    /// </summary>
    public bool IsUnion(ReadOnlySpan<char> name)
    {
        return Unions.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(name);
    }

    /// <summary>
    /// Returns the variant list for the named union, or null if not declared.
    /// </summary>
    public List<UnionVariant>? UnionDef(string name)
    {
        return Unions.GetValueOrDefault(name);
    }

    #endregion

    #region Lookup

    public bool IsClass(string name)
    {
        return _classes.ContainsKey(name);
    }

    /// <summary>
    /// Returns true if the name is a declared class.
    /// </summary>
    public bool IsClass(ReadOnlySpan<char> name)
    {
        return _classes.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(name);
    }

    /// <summary>
    /// Returns the source file that declared the named class, or null if not found.
    /// </summary>
    public string? ClassModule(string name)
    {
        return _classes.TryGetValue(name, out var s) ? s.Module : null;
    }

    /// <summary>
    /// Returns the source file that declared the named class, or null if not found.
    /// </summary>
    public string? ClassModule(ReadOnlySpan<char> name)
    {
        return _classes.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var s) ? s.Module : null;
    }

    /// <summary>
    /// Returns the last registered overload of the named method, or null if not found.
    /// </summary>
    public Symbol? LookupMethod(string cls, string method)
    {
        return _methods.TryGetValue(new(cls, method), out var l) ? l[^1] : null;
    }

    /// <summary>
    /// Returns the last registered overload of the named free function, or null if not found.
    /// </summary>
    public Symbol? LookupFreeFunc(string name)
    {
        return _funcs.TryGetValue(name, out var l) ? l[^1] : null;
    }

    /// <summary>
    /// Returns the last registered overload of the given operator on the class, or null if not
    /// found. Every operator except 'as' has at most one overload in a well-formed program, so
    /// this is the whole answer for them; for 'as', callers that need to pick among several
    /// return-type overloads should use OperatorOverloads instead.
    /// </summary>
    public Symbol? LookupOperator(string cls, string op)
    {
        return _operators.TryGetValue(new(cls, op), out var l) ? l[^1] : null;
    }

    /// <summary>
    /// Returns the overload of the given operator with the given parameter count, or null.
    /// Unary and binary '-' share a bucket, so arity is what tells them apart.
    /// </summary>
    public Symbol? LookupOperator(string cls, string op, int arity)
    {
        if (!_operators.TryGetValue(new(cls, op), out var l)) return null;
        for (int i = l.Count - 1; i >= 0; i--)
            if (l[i].Sig!.Params.Count == arity) return l[i];
        return null;
    }

    /// <summary>
    /// Returns all overloads of the named operator on the given class.
    /// </summary>
    public IReadOnlyList<Symbol> OperatorOverloads(string cls, string op)
    {
        return _operators.TryGetValue(new(cls, op), out var l) ? l : [];
    }

    /// <summary>
    /// Returns true if the named operator has more than one overload on the given class.
    /// </summary>
    public bool IsOverloadedOperator(string cls, string op)
    {
        return OperatorOverloads(cls, op).Count > 1;
    }

    /// <summary>
    /// Returns all overloads of the named method on the given class.
    /// </summary>
    public IReadOnlyList<Symbol> MethodOverloads(string cls, string method)
    {
        return _methods.TryGetValue(new(cls, method), out var l) ? l : [];
    }

    /// <summary>
    /// Returns all overloads of the named free function.
    /// </summary>
    public IReadOnlyList<Symbol> FuncOverloads(string name)
    {
        return _funcs.TryGetValue(name, out var l) ? l : [];
    }

    /// <summary>
    /// Returns true if the named method has more than one overload.
    /// </summary>
    public bool IsOverloadedMethod(string cls, string method)
    {
        return MethodOverloads(cls, method).Count > 1;
    }

    /// <summary>
    /// Returns the distinct method names declared directly on the given class/module,
    /// for "did you mean" suggestions when a lookup misses.
    /// </summary>
    public IEnumerable<string> MethodNames(string cls)
    {
        foreach (var key in _methods.Keys)
            if (key.Owner == cls) yield return key.Name;
    }

    /// <summary>
    /// Returns true if the named free function has more than one overload.
    /// </summary>
    public bool IsOverloadedFunc(string name)
    {
        return FuncOverloads(name).Count > 1;
    }

    /// <summary>
    /// Returns the declared type of the named field, or null if not found.
    /// </summary>
    public string? FieldType(string cls, string field)
    {
        return _fields.TryGetValue(new(cls, field), out var s) ? s.Type : null;
    }

    /// <summary>
    /// Returns true if the named field exists on the given class.
    /// </summary>
    public bool IsField(string cls, string field)
    {
        return _fields.ContainsKey(new(cls, field));
    }

    #endregion

    #region Visibility

    // Class/method members declared private — accessible only from the declaring type.
    public HashSet<MemberKey> PrivateMembers { get; } = [];

    /// <summary>
    /// Returns true if the named member on the given owner was declared private.
    /// </summary>
    public bool IsPrivateMember(string owner, string member)
    {
        return PrivateMembers.Contains(new(owner, member));
    }

    // File-local free functions — registered per declaring file so unrelated files may
    // reuse a name, and mangled uniquely so they never clash in the C output.
    private readonly Dictionary<(string File, string Name), List<Symbol>> _privateFuncs = [];

    /// <summary>
    /// Registers a file-local (private) free function from the given source file.
    /// </summary>
    public void RegisterPrivateFunc(string file, string name, MethodSig sig)
    {
        Bucket(_privateFuncs, (file, name)).Add(new Symbol(name, SymKind.FreeFunc, sig.ReturnType, null, sig) { Module = file });
    }

    /// <summary>
    /// Returns the last registered overload of the named file-local function, or null if not found.
    /// </summary>
    public Symbol? LookupPrivateFunc(string file, string name)
    {
        return _privateFuncs.TryGetValue((file, name), out var l) ? l[^1] : null;
    }

    /// <summary>
    /// Returns all overloads of the named file-local function.
    /// </summary>
    public IReadOnlyList<Symbol> PrivateFuncOverloads(string file, string name)
    {
        return _privateFuncs.TryGetValue((file, name), out var l) ? l : [];
    }

    #endregion

    #region Type utilities

    /// <summary>
    /// Returns the C type string for a Gata type name.
    /// </summary>
    public string CType(string t)
    {
        if (string.IsNullOrEmpty(t) || t == "void") return "void";
        if (t.Length > 0 && t[t.Length - 1] == '*') return t;
        if (PrimTypes.IsPrim(t)) return PrimTypes.ToC(t);
        if ((t == BuiltinTypes.Process || t == BuiltinTypes.Thread) && Builtins.ContainsKey(t)) return "void*";
        return $"{Mangler.Class(t)}*";
    }

    #endregion
}
