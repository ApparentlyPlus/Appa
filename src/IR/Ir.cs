namespace Appa;

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
    public static bool IsPrim(string s) => s is 
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
    public static readonly HashSet<string> Spellings =
        ["int", "int64", "uint", "uint64", "short", "ushort", "sbyte", "byte", "usize", "uintptr", "char", "bool", "float", "double", "void"];
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
    public override bool IsString => _isString;
}

#endregion
