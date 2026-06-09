using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace Appa;

enum SymKind { Class, Field, Method, FreeFunc, Operator }

// The closed vocabulary of compiler runtime roles. A libgata symbol annotated
// @intrinsic(<role>) fills the role; the compiler emits the bound C name. This
// enum IS the compiler-runtime contract surface.
static class Roles
{
    public const string Alloc          = "alloc";
    public const string Retain         = "retain";
    public const string Release        = "release";
    public const string ObjHeader      = "obj_header";
    public const string ObjInit        = "obj_init";
    public const string StringifyInt   = "stringify_int";
    public const string StringifyFloat = "stringify_float";

    public static readonly FrozenSet<string> All = FrozenSet.ToFrozenSet(
    [
        Alloc, Retain, Release, ObjHeader, ObjInit,
        StringifyInt, StringifyFloat
    ]);
}

/// <summary>
/// The signature of a method or free function as collected from the AST.
/// </summary>
record MethodSig(
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
readonly record struct MemberKey(string Owner, string Name);

// A declared symbol. Its C name is assigned once — by the Mangler, after all
// declarations are collected (overload-ness is known then) — and read by both the
// definition and every call site, so they cannot disagree.
/// <summary>
/// A single declared symbol with its kind, type, and optionally its method signature.
/// </summary>
sealed record Symbol(string Name, SymKind Kind, string Type, string? Owner, MethodSig? Sig)
{
    public string CName  { get; set; } = "";
    public string Module { get; set; } = "";
}

/// <summary>
/// Declaration registry for classes, fields, methods, free functions, operators, enums, and unions.
/// Populated by SymbolCollector during pass 1; read by the type resolver during pass 2.
/// </summary>
sealed class SymbolTable
{
    readonly Dictionary<string, Symbol>          _classes   = new();
    readonly Dictionary<MemberKey, Symbol>       _fields    = new();
    readonly Dictionary<MemberKey, List<Symbol>> _methods   = new();
    readonly Dictionary<string, List<Symbol>>    _funcs     = new();
    readonly Dictionary<MemberKey, Symbol>       _operators = new();

    // Every accepted primitive spelling.
    public static readonly FrozenSet<string> Primitives = PrimTypes.Spellings;

    static readonly FrozenSet<string> KernelPassthrough = FrozenSet.ToFrozenSet(["Process", "Thread"]);

    // Result_T typedefs needed by throws functions: name -> inner Gata type.
    public Dictionary<string, string> ResultTypedefs { get; } = new();

    // Declaring source files seen during collection.
    public HashSet<string> Modules { get; } = new();

    // role -> bound C symbol name, from @intrinsic annotations.
    public Dictionary<string, string> Intrinsics { get; } = new();

    /// <summary>
    /// Returns the C name bound to the given intrinsic role, or null if unbound.
    /// </summary>
    public string? IntrinsicOrNull(string role) =>
        Intrinsics.TryGetValue(role, out var n) ? n : null;

    #region Registration

    /// <summary>
    /// Registers a class declaration from the given source file.
    /// </summary>
    public void RegisterClass(string name, string module) =>
        _classes[name] = new Symbol(name, SymKind.Class, name, null, null) { CName = Mangler.Class(name), Module = module };

    /// <summary>
    /// Registers a field on the named class.
    /// </summary>
    public void RegisterField(string cls, string field, string type) =>
        _fields[new(cls, field)] = new Symbol(field, SymKind.Field, type, cls, null);

    /// <summary>
    /// Registers a method overload on the named class.
    /// </summary>
    public void RegisterMethod(string cls, string name, MethodSig sig) =>
        Bucket(_methods, new(cls, name)).Add(new Symbol(name, SymKind.Method, sig.ReturnType, cls, sig));

    /// <summary>
    /// Registers a free function overload from the given source file.
    /// </summary>
    public void RegisterFreeFunc(string name, MethodSig sig, string module) =>
        Bucket(_funcs, name).Add(new Symbol(name, SymKind.FreeFunc, sig.ReturnType, null, sig) { Module = module });

    /// <summary>
    /// Registers an operator overload on the named class.
    /// </summary>
    public void RegisterOperator(string cls, string op, string returnType, List<Param> @params) =>
        _operators[new(cls, op)] = new Symbol(op, SymKind.Operator, returnType, cls,
            new MethodSig(returnType, @params, IsStatic: false, IsThrows: false, IsEntry: false, Annotations: []))
            { CName = Mangler.Operator(cls, op) };

    /// <summary>
    /// Records that a throws function returns the given type, ensuring a Result typedef is emitted.
    /// </summary>
    public void RegisterThrows(string returnType)
    {
        string c = PrimTypes.Canon(returnType);
        ResultTypedefs.TryAdd($"Result_{c}", c);
    }

    static List<Symbol> Bucket<K>(Dictionary<K, List<Symbol>> d, K key) where K : notnull =>
        d.TryGetValue(key, out var l) ? l : d[key] = [];

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
    }

    #endregion

    #region Enums and unions

    // Enum types: name -> member names. Globally visible like primitives.
    public Dictionary<string, HashSet<string>> Enums { get; } = new();

    /// <summary>
    /// Registers an enum type and its member names.
    /// </summary>
    public void RegisterEnum(string name, IEnumerable<string> members) => Enums[name] = [.. members];

    /// <summary>
    /// Returns true if the name is a declared enum type.
    /// </summary>
    public bool IsEnum(string name) => Enums.ContainsKey(name);

    /// <summary>
    /// Returns true if the member belongs to the named enum.
    /// </summary>
    public bool IsEnumMember(string e, string m) => Enums.TryGetValue(e, out var ms) && ms.Contains(m);

    // Union types: name -> variant list. Globally visible and not generic.
    public Dictionary<string, List<UnionVariant>> Unions { get; } = new();

    /// <summary>
    /// Registers a union type and its variants.
    /// </summary>
    public void RegisterUnion(string name, List<UnionVariant> variants) => Unions[name] = variants;

    /// <summary>
    /// Returns true if the name is a declared union type.
    /// </summary>
    public bool IsUnion(string name) => Unions.ContainsKey(name);

    /// <summary>
    /// Returns the variant list for the named union, or null if not declared.
    /// </summary>
    public List<UnionVariant>? UnionDef(string name) => Unions.GetValueOrDefault(name);

    #endregion

    #region Lookup

    /// <summary>
    /// Returns true if the name is a declared class.
    /// </summary>
    public bool IsClass(string name) => _classes.ContainsKey(name);

    /// <summary>
    /// Returns the source file that declared the named class, or null if not found.
    /// </summary>
    public string? ClassModule(string name) => _classes.TryGetValue(name, out var s) ? s.Module : null;

    /// <summary>
    /// Returns the last registered overload of the named method, or null if not found.
    /// </summary>
    public Symbol? LookupMethod(string cls, string method) =>
        _methods.TryGetValue(new(cls, method), out var l) ? l[^1] : null;

    /// <summary>
    /// Returns the last registered overload of the named free function, or null if not found.
    /// </summary>
    public Symbol? LookupFreeFunc(string name) =>
        _funcs.TryGetValue(name, out var l) ? l[^1] : null;

    /// <summary>
    /// Returns the operator symbol for the given class and operator token, or null if not found.
    /// </summary>
    public Symbol? LookupOperator(string cls, string op) =>
        _operators.GetValueOrDefault(new(cls, op));

    /// <summary>
    /// Returns all overloads of the named method on the given class.
    /// </summary>
    public IReadOnlyList<Symbol> MethodOverloads(string cls, string method) =>
        _methods.TryGetValue(new(cls, method), out var l) ? l : [];

    /// <summary>
    /// Returns all overloads of the named free function.
    /// </summary>
    public IReadOnlyList<Symbol> FuncOverloads(string name) =>
        _funcs.TryGetValue(name, out var l) ? l : [];

    /// <summary>
    /// Returns true if the named method has more than one overload.
    /// </summary>
    public bool IsOverloadedMethod(string cls, string method) => MethodOverloads(cls, method).Count > 1;

    /// <summary>
    /// Returns true if the named free function has more than one overload.
    /// </summary>
    public bool IsOverloadedFunc(string name) => FuncOverloads(name).Count > 1;

    /// <summary>
    /// Returns the declared type of the named field, or null if not found.
    /// </summary>
    public string? FieldType(string cls, string field) =>
        _fields.TryGetValue(new(cls, field), out var s) ? s.Type : null;

    /// <summary>
    /// Returns true if the named field exists on the given class.
    /// </summary>
    public bool IsField(string cls, string field) => _fields.ContainsKey(new(cls, field));

    #endregion

    #region Visibility

    // Class/method members declared private — accessible only from the declaring type.
    public HashSet<MemberKey> PrivateMembers { get; } = new();

    /// <summary>
    /// Returns true if the named member on the given owner was declared private.
    /// </summary>
    public bool IsPrivateMember(string owner, string member) =>
        PrivateMembers.Contains(new(owner, member));

    // File-local free functions — registered per declaring file so unrelated files may
    // reuse a name, and mangled uniquely so they never clash in the C output.
    readonly Dictionary<(string File, string Name), List<Symbol>> _privateFuncs = new();

    /// <summary>
    /// Registers a file-local (private) free function from the given source file.
    /// </summary>
    public void RegisterPrivateFunc(string file, string name, MethodSig sig) =>
        Bucket(_privateFuncs, (file, name)).Add(new Symbol(name, SymKind.FreeFunc, sig.ReturnType, null, sig) { Module = file });

    /// <summary>
    /// Returns the last registered overload of the named file-local function, or null if not found.
    /// </summary>
    public Symbol? LookupPrivateFunc(string file, string name) =>
        _privateFuncs.TryGetValue((file, name), out var l) ? l[^1] : null;

    /// <summary>
    /// Returns all overloads of the named file-local function.
    /// </summary>
    public IReadOnlyList<Symbol> PrivateFuncOverloads(string file, string name) =>
        _privateFuncs.TryGetValue((file, name), out var l) ? l : [];

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
        if (KernelPassthrough.Contains(t)) return "void*";
        return $"{Mangler.Class(t)}*";
    }

    #endregion
}
