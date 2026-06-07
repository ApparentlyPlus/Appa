namespace Appa;

using System.Collections.Frozen;

#region Primitive types

// One source of truth for scalar types. Each distinct type has a single canonical
// token stored in IrPrimType.CName; width-implicit and stdint spellings fold onto
// it. ToC maps the canonical token to a fixed-width C type for a deterministic ABI.
static class PrimTypes
{
    // Accepted Gata spellings -> canonical token. Kept as a map so future surface
    // aliases fold here in one place.

    /// <summary>
    /// Returns true if the string is a recognised Gata primitive spelling.
    /// </summary>
    public static bool IsPrim(string s) => IsPrim(s.AsSpan());

    /// <summary>
    /// Returns true if the span is a recognised Gata primitive spelling.
    /// </summary>
    public static bool IsPrim(ReadOnlySpan<char> s) => s is 
        "int" or "int64" or "uint" or "uint64" or "short" or "ushort" or 
        "sbyte" or "byte" or "usize" or "uintptr" or "char" or "bool" or 
        "float" or "double" or "void";

    /// <summary>
    /// Returns the canonical token for a primitive spelling,
    /// or the input unchanged if not a known alias.
    /// </summary>
    public static string Canon(string s) => s;

    /// <summary>
    /// Returns the fixed-width C type for a primitive spelling.
    /// </summary>
    public static string ToC(string s) => s switch
    {
        "int"     => "int32_t",
        "int64"   => "int64_t",
        "uint"    => "uint32_t",
        "uint64"  => "uint64_t",
        "short"   => "int16_t",
        "ushort"  => "uint16_t",
        "sbyte"   => "int8_t",
        "byte"    => "uint8_t",
        "usize"   => "size_t",
        "uintptr" => "uintptr_t",
        "char"    => "char",
        "bool"    => "bool",
        "float"   => "float",
        "double"  => "double",
        "void"    => "void",
        _         => s
    };

    /// <summary>
    /// Returns true if the canonical token belongs to the integer family.
    /// </summary>
    public static bool IsIntCanon(string canon) => canon is
        "int" or "int64" or "uint" or "uint64" or "short" or "ushort" or 
        "sbyte" or "byte" or "usize" or "uintptr" or "char" or "bool";

    /// <summary>
    /// Returns true if the canonical token is float or double.
    /// </summary>
    public static bool IsFloat(string canon) => canon is "float" or "double";

    // Every accepted spelling - the front-end's set of primitive type names.
    public static readonly FrozenSet<string> Spellings = FrozenSet.ToFrozenSet(
        ["int", "int64", "uint", "uint64", "short", "ushort", "sbyte", "byte", "usize", "uintptr", "char", "bool", "float", "double", "void"]
    );
}

/// <summary>
/// Base type for all IR type nodes.
/// </summary>
abstract record IrType
{
    /// <summary>
    /// Returns the C type spelling used in emitted output.
    /// </summary>
    public abstract string ToCType();

    /// <summary>
    /// Returns the stable C-identifier mangling of a type.
    /// </summary>
    public abstract string MangledName { get; }

    public virtual bool IsNumeric => false;
    public virtual bool IsFloat   => false;
    public virtual bool IsString  => false;
    public virtual bool IsVoid    => false;

    // Singletons for primitives (CName is the canonical token, see PrimTypes)
    public static readonly IrVoidType Void   = new();
    public static readonly IrPrimType Bool   = new("bool");
    public static readonly IrPrimType Int    = new("int");
    public static readonly IrPrimType Char   = new("char");
    public static readonly IrPrimType Short  = new("short");
    public static readonly IrPrimType Long   = new("int64");
    public static readonly IrPrimType Float  = new("float");
    public static readonly IrPrimType Double = new("double");
    public static readonly IrPrimType SizeT  = new("usize");
    public static readonly IrClassRef String = new("String");
}

/// <summary>
/// The void type - used as a return type for functions that produce no value.
/// </summary>
record IrVoidType : IrType
{
    public override string ToCType() => "void";
    public override string MangledName => "void";
    public override bool IsVoid => true;
}

/// <summary>
/// A primitive scalar type. CName is the canonical token;
/// ToCType lowers it to the corresponding fixed-width C type.
/// </summary>
record IrPrimType(string CName) : IrType
{
    private readonly string _cType = PrimTypes.ToC(CName);
    private readonly bool _isNumeric = PrimTypes.IsIntCanon(CName);
    private readonly bool _isFloat = PrimTypes.IsFloat(CName);

    public override string ToCType() => _cType;
    public override string MangledName => CName;
    public override bool IsNumeric => _isNumeric;
    public override bool IsFloat   => _isFloat;
}

/// <summary>
/// A reference to a named class type.
/// Lowers to a mangled pointer in C output.
/// </summary>
record IrClassRef(string ClassName) : IrType
{
    private readonly bool _isString = ClassName is "String" or "gata_String";
    public override string ToCType() => throw new NotImplementedException();
    public override string MangledName => ClassName;
    public override bool IsString => _isString;
}

#endregion

#region Composite types

/// <summary>
/// A named integer-backed enum type. Distinct from int with no implicit conversion,
/// but comparable, assignable, and usable as a switch scrutinee. Lowers to a C enum.
/// </summary>
record IrEnumType(string Name) : IrType
{
    private readonly string _cType = Mangler.Enum(Name);
    public override string ToCType() => _cType;
    public override string MangledName => Name;
}

/// <summary>
/// A pointer type for unsafe Gata code.
/// Lowers to a C pointer to the inner type.
/// </summary>
record IrPtrType(IrType Inner) : IrType
{
    private readonly string _cType = $"{Inner.ToCType()}*";
    public override string ToCType() => _cType;
    public override string MangledName => Inner.MangledName + "_p";
}

/// <summary>
/// A fixed-size array type [N]T - a value aggregate, not a heap reference.
/// Monomorphized per (element, size) pair into a named C struct.
/// Copies, returns, and iterates by value with the length carried in the type.
/// </summary>
record IrArrayType(IrType Elem, int Size) : IrType
{
    private readonly string _cType = Mangler.Class($"Arr_{Elem.MangledName}_{Size}");
    public override string ToCType() => _cType;
    public override string MangledName => $"Arr_{Elem.MangledName}_{Size}";

    /// <summary>
    /// Produces a stable C-identifier mangling of a type, used to name array structs
    /// and function-pointer typedefs.
    /// </summary>
    public static string Mangle(IrType t) => t.MangledName;
}

/// <summary>
/// A Result-of-T wrapper produced by throws functions.
/// Lowers to a C struct with a bool tag and a value or error payload.
/// </summary>
record IrResultType(IrType Inner) : IrType
{
    /// <summary>
    /// The C typedef name for this result type, e.g. Result_int or Result_MyClass.
    /// </summary>
    public string ResultName { get; } = $"Result_{(
        Inner switch
        {
            IrVoidType    => "int",
            IrClassRef cr => cr.ClassName,
            IrPrimType p  => p.CName,
            _             => Inner.ToCType().TrimEnd('*').Replace("gata_", "")
        }
    )}";

    public override string ToCType() => ResultName;
    public override string MangledName => "Result_" + Inner.MangledName;
}

/// <summary>
/// A function-pointer type func(T1, T2) -> R.
/// ToCType returns a stable typedef name rather than an inline C declarator,
/// because inline declarators cannot be used with the type-then-name emission pattern.
/// The typedef is emitted once per distinct signature from IrModule.FuncPtrTypes.
/// </summary>
record IrFuncPtrType(IrType Ret, List<IrType> Params) : IrType
{
    public override string MangledName { get; } = $"Fn_{Ret.MangledName}__{(
        Params.Count switch
        {
            0 => "",
            1 => Params[0].MangledName,
            _ => string.Join("_", Params.Select(p => p.MangledName))
        }
    )}";

    private readonly string _cType = Mangler.Class($"Fn_{Ret.MangledName}__{(
        Params.Count switch
        {
            0 => "",
            1 => Params[0].MangledName,
            _ => string.Join("_", Params.Select(p => p.MangledName))
        }
    )}");

    public override string ToCType() => _cType;
}

/// <summary>
/// A named tagged-union type. Not generic, not ARC-managed.
/// Lowers to a tag enum and a C struct containing the tag and a union of per-variant payload structs.
/// </summary>
record IrUnionType(string Name) : IrType
{
    private readonly string _cType = Mangler.Union(Name);
    public override string ToCType() => _cType;
    public override string MangledName => Name;
}

#endregion
