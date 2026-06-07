namespace Appa;

using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, IrClassRef> Cache = new();
    public static IrClassRef Get(string className) => Cache.GetOrAdd(className, name => new IrClassRef(name));
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
    private static readonly ConcurrentDictionary<string, IrEnumType> Cache = new();
    public static IrEnumType Get(string name) => Cache.GetOrAdd(name, n => new IrEnumType(n));

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
    private static readonly ConcurrentDictionary<IrType, IrPtrType> Cache = new();
    public static IrPtrType Get(IrType inner) => Cache.GetOrAdd(inner, i => new IrPtrType(i));

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
    private static readonly ConcurrentDictionary<IrType, IrResultType> Cache = new();
    public static IrResultType Get(IrType inner) => Cache.GetOrAdd(inner, i => new IrResultType(i));

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

#region Expressions

/// <summary>
/// Base type for all IR expression nodes.
/// Every expression carries its result type and an optional source span.
/// </summary>
abstract record IrExpr(IrType Type) { public TextSpan Span { get; init; } = TextSpan.None; }

// Note for literals:
// IrLitInt - Value is the literal's 64-bit bit pattern. T is the type selected by
// suffix or magnitude (int by default). CText, when set, is the exact C
// text to emit (hex forms, suffixed forms); otherwise Value is printed.
// IrLitFloat - Raw is emitted verbatim (valid C including exponent and trailing `f`).
// T is double by default, float for an `f` suffix.

/// <summary>
/// An integer literal. Value is the 64-bit bit pattern; CText overrides the emitted text when set.
/// </summary>
record IrLitInt(long Value, IrType? T = null, string? CText = null) : IrExpr(T ?? IrType.Int);

/// <summary>
/// A character literal. Codepoint is the Unicode code point of the character.
/// </summary>
record IrLitChar(int Codepoint) : IrExpr(IrType.Char);

/// <summary>
/// A floating-point literal. Raw is emitted verbatim as valid C text.
/// </summary>
record IrLitFloat(string Raw, IrType? T = null) : IrExpr(T ?? IrType.Double);

/// <summary>
/// A boolean literal.
/// </summary>
record IrLitBool(bool Value) : IrExpr(IrType.Bool);

/// <summary>
/// A string literal. Raw includes the surrounding quotes.
/// </summary>
record IrLitString(string Raw) : IrExpr(IrType.String);

/// <summary>
/// A null literal of a specific type.
/// </summary>
record IrLitNull(IrType T) : IrExpr(T);

/// <summary>
/// A reference to a named enum member.
/// </summary>
record IrEnumConst(string EnumName, string Member) : IrExpr(IrEnumType.Get(EnumName));

// Identifiers
// IrVar.IsRef: the variable resolves to a `ref` parameter - emitted as a dereferenced
// pointer (*name) rather than a bare name, for both reads and writes. Set by the resolver.
/// <summary>
/// A local variable or parameter reference.
/// </summary>
record IrVar(string Name, IrType T, bool IsRef = false) : IrExpr(T);

/// <summary>
/// A reference to the implicit self object inside a method body.
/// </summary>
record IrSelfExpr(string ClassName) : IrExpr(IrClassRef.Get(ClassName));

/// <summary>
/// A field load from an object expression.
/// </summary>
record IrFieldLoad(IrExpr Obj, string Field, IrType FieldType) : IrExpr(FieldType);

/// <summary>
/// An index expression into a collection or fixed array.
/// </summary>
record IrIndex(IrExpr Obj, IrExpr Idx, IrType ElemType) : IrExpr(ElemType);

// Calls - CName is the fully-qualified C function name
/// <summary>
/// A call to a static (free) C function.
/// </summary>
record IrStaticCall(string CName, IrType RetType, List<IrExpr> Args) : IrExpr(RetType);

/// <summary>
/// A call to an instance method, passing the receiver as the first argument.
/// </summary>
record IrInstanceCall(IrExpr Recv, string CName, IrType RetType, List<IrExpr> Args) : IrExpr(RetType);

/// <summary>
/// A call to a throws-annotated static function. The result type wraps the inner type in Result.
/// </summary>
record IrThrowsCall(string CName, IrType InnerType, List<IrExpr> Args) : IrExpr(IrResultType.Get(InnerType));

/// <summary>
/// A call to a throws-annotated instance method. The result type wraps the inner type in Result.
/// </summary>
record IrThrowsInstanceCall(IrExpr Recv, string CName, IrType InnerType, List<IrExpr> Args) : IrExpr(IrResultType.Get(InnerType));

/// <summary>
/// A bare reference to a free function by name, decaying to a function-pointer value.
/// CName is a valid C function-pointer value with no cast needed.
/// </summary>
record IrFuncRef(string CName, IrFuncPtrType T) : IrExpr(T);

/// <summary>
/// A call through a function-pointer-typed expression.
/// </summary>
record IrIndirectCall(IrExpr Target, IrType Ret, List<IrExpr> Args) : IrExpr(Ret);

/// <summary>
/// Constructs a union variant value. VariantIndex selects the tag.
/// </summary>
record IrUnionConstruct(IrUnionType T, int VariantIndex, List<IrExpr> Args) : IrExpr(T);

/// <summary>
/// Reads one payload field of a union's active variant.
/// Only emitted after the tag has already been tested.
/// </summary>
record IrUnionField(IrExpr Union, int VariantIndex, string Field, IrType FieldType) : IrExpr(FieldType);

// Operators
/// <summary>
/// A binary operator expression.
/// </summary>
record IrBinOp(string Op, IrExpr Left, IrExpr Right, IrType T) : IrExpr(T);

/// <summary>
/// A ternary conditional expression.
/// </summary>
record IrTernary(IrExpr Cond, IrExpr Then, IrExpr Else, IrType T) : IrExpr(T);

/// <summary>
/// A prefix unary operator expression.
/// </summary>
record IrUnaryOp(string Op, IrExpr Operand, IrType T) : IrExpr(T);

/// <summary>
/// A postfix operator expression such as i++ or i--.
/// </summary>
record IrPostfix(string Op, IrExpr Operand) : IrExpr(Operand.Type);

/// <summary>
/// An explicit cast to a target type.
/// </summary>
record IrCast(IrType To, IrExpr Value) : IrExpr(To);

// Allocation
/// <summary>
/// A heap allocation of a named class with constructor arguments.
/// </summary>
record IrNew(string ClassName, List<IrExpr> Args) : IrExpr(IrClassRef.Get(ClassName));

/// <summary>
/// A heap allocation followed by repeated Add calls to populate a collection.
/// Lowered to a GNU statement expression by the emitter.
/// </summary>
record IrNewInit(string ClassName, List<IrExpr> Args, string AddCName, List<IrExpr> Inits)
    : IrExpr(IrClassRef.Get(ClassName));

/// <summary>
/// A fixed-array literal [e1, e2, ...] lowered to a C compound literal.
/// </summary>
record IrArrayLit(IrArrayType ArrType, List<IrExpr> Elems) : IrExpr(ArrType);

/// <summary>
/// An interpolated string whose parts are all typed String.
/// </summary>
record IrInterp(List<IrExpr> Parts) : IrExpr(IrType.String);

// Unsafe
/// <summary>
/// Takes the address of a target expression, producing a pointer.
/// </summary>
record IrAddrOf(IrExpr Target) : IrExpr(IrPtrType.Get(Target.Type));

/// <summary>
/// Dereferences a pointer expression to yield the pointed-to value.
/// </summary>
record IrDeref(IrExpr Ptr, IrType T) : IrExpr(T);

/// <summary>
/// A sizeof expression. Emits as C sizeof(ctype).
/// </summary>
record IrSizeof(IrType Of) : IrExpr(IrType.SizeT);

/// <summary>
/// A default value expression. Emits as a C zero-cast: (ctype)0.
/// </summary>
record IrDefault(IrType Of) : IrExpr(Of);

#endregion

#region Statements

/// <summary>
/// Base type for all IR statement nodes.
/// Every statement carries an optional source span.
/// </summary>
abstract record IrStmt { public TextSpan Span { get; init; } = TextSpan.None; }

/// <summary>
/// A sequential list of statements forming a scope.
/// </summary>
record IrBlock(List<IrStmt> Stmts) : IrStmt;

/// <summary>
/// A native C statement with separate kernel and user variants.
/// </summary>
record IrNativeStmt(string KernelC, string UserC) : IrStmt;

// Verbatim C produced by a lowering pass (Result branches, gotos/labels). Printed as-is.
/// <summary>
/// Verbatim C code produced by a lowering pass such as Result branches or goto/label pairs.
/// </summary>
record IrRaw(string Code) : IrStmt;

/// <summary>
/// A local variable declaration with an optional initializer.
/// </summary>
record IrDeclVar(string Name, IrType Type, IrExpr? Init) : IrStmt;

/// <summary>
/// An assignment expression statement. Op is the assignment operator, e.g. = += -=.
/// </summary>
record IrAssign(IrExpr Target, string Op, IrExpr Value) : IrStmt;

/// <summary>
/// An expression evaluated for its side effects, result discarded.
/// </summary>
record IrExprStmt(IrExpr Expr) : IrStmt;

/// <summary>
/// A return statement with an optional value.
/// </summary>
record IrReturn(IrExpr? Value) : IrStmt;

/// <summary>
/// A break statement exiting the nearest enclosing loop or switch.
/// </summary>
record IrBreak() : IrStmt;

/// <summary>
/// A continue statement jumping to the next iteration of the nearest enclosing loop.
/// </summary>
record IrContinue() : IrStmt;

/// <summary>
/// An if/else statement. Else is null when there is no else branch.
/// </summary>
record IrIf(IrExpr Cond, IrBlock Then, IrBlock? Else) : IrStmt;

/// <summary>
/// A while loop.
/// </summary>
record IrWhile(IrExpr Cond, IrBlock Body) : IrStmt;

/// <summary>
/// A C-style for loop with optional init, condition, and step.
/// </summary>
record IrFor(IrStmt? Init, IrExpr? Cond, IrExpr? Step, IrBlock Body) : IrStmt;

// ArraySize >= 0: iterate a fixed array by value (coll._[i], length known from type).
// ArraySize < 0:  iterate a collection via its LenCName/GetCName functions.
/// <summary>
/// A for-in loop over a collection or fixed array.
/// </summary>
record IrForIn(string Var, IrType ElemType, string LenCName, string GetCName,
               IrExpr Collection, IrBlock Body, int ArraySize = -1) : IrStmt;

/// <summary>
/// A try/catch block. Seq is a unique sequence number used to name generated labels.
/// </summary>
record IrTryCatch(IrBlock Try, IrBlock Catch, int Seq) : IrStmt;

// Lowered to an if/else-if chain in Desugar; never reaches the backend as a switch.
/// <summary>
/// A switch statement. Lowered to an if/else-if chain by Desugar; never reaches the backend.
/// </summary>
record IrSwitch(IrExpr Scrutinee, List<IrSwitchCase> Cases, IrBlock? Default) : IrStmt;

/// <summary>
/// One case arm of an IrSwitch, with one or more labels and a body block.
/// </summary>
record IrSwitchCase(List<IrExpr> Labels, IrBlock Body);

// Lowered to an if/else-if chain on the union tag in Desugar; never reaches the backend.
/// <summary>
/// A match statement over a union type. Lowered to an if/else-if chain by Desugar; never reaches the backend.
/// </summary>
record IrMatch(IrExpr Scrutinee, IrUnionType UnionT, List<IrMatchCase> Cases, IrBlock? Default) : IrStmt;

// FieldName is the variant's own field; BindName is the local the pattern introduced.
// They can differ: `case Circle(r)` binds field `radius` to local `r`.
/// <summary>
/// A single binding introduced by a match pattern - maps a variant field to a local name.
/// </summary>
record IrMatchBind(string FieldName, string BindName, IrType Type);

/// <summary>
/// One case arm of an IrMatch, identified by variant index with its pattern bindings.
/// </summary>
record IrMatchCase(int VariantIndex, List<IrMatchBind> Binds, IrBlock Body);

/// <summary>
/// An unsafe block containing statements that may use pointer operations.
/// </summary>
record IrUnsafeBlock(IrBlock Body) : IrStmt;

// The Ownership pass lowers defer: Action is spliced into every exit path of the
// enclosing block in LIFO order, then this node is dropped. Never reaches the backend.
/// <summary>
/// A defer statement. Lowered by the Ownership pass; never reaches the backend.
/// </summary>
record IrDefer(IrStmt Action) : IrStmt;

// The Ownership pass lowers throw: releases owned values, then emits an error Result or goto catch.
/// <summary>
/// A throw statement. Lowered by the Ownership pass; never reaches the backend.
/// </summary>
record IrThrow() : IrStmt;

// Raw includes the quotes. No String allocation - calls the env's _env_dbg / _env_panic directly.
/// <summary>
/// A debug assertion. Emitted as a direct call to the env debug binding with a raw C string literal.
/// </summary>
record IrDebug(string Raw) : IrStmt;

/// <summary>
/// A panic statement. Emitted as a direct call to the env panic binding with a raw C string literal.
/// </summary>
record IrPanic(string Raw) : IrStmt;

#endregion
