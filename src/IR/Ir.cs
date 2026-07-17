namespace Appa;

using System.Collections.Frozen;

#region Primitive types

// One source of truth for scalar types: a single frozen table carrying each primitive's
// fixed-width C spelling, family, and numeric promotion rank. Every other predicate and
// list (spelling set, integer family, promotion ranks) is derived from this table, so
// adding a primitive is a one-line change that cannot drift across passes.
internal static class PrimTypes
{
    internal readonly record struct PrimInfo(string CType, bool IsInt, bool IsFloatTy, int Rank);

    private static readonly FrozenDictionary<string, PrimInfo> Table = FrozenDictionary.ToFrozenDictionary(
        new Dictionary<string, PrimInfo>
        {
            ["bool"]    = new("bool",      IsInt: true,  IsFloatTy: false, Rank: 1),
            ["char"]    = new("char",      IsInt: true,  IsFloatTy: false, Rank: 2),
            ["sbyte"]   = new("int8_t",    IsInt: true,  IsFloatTy: false, Rank: 2),
            ["byte"]    = new("uint8_t",   IsInt: true,  IsFloatTy: false, Rank: 2),
            ["short"]   = new("int16_t",   IsInt: true,  IsFloatTy: false, Rank: 3),
            ["ushort"]  = new("uint16_t",  IsInt: true,  IsFloatTy: false, Rank: 3),
            ["int"]     = new("int32_t",   IsInt: true,  IsFloatTy: false, Rank: 4),
            ["uint"]    = new("uint32_t",  IsInt: true,  IsFloatTy: false, Rank: 4),
            ["int64"]   = new("int64_t",   IsInt: true,  IsFloatTy: false, Rank: 5),
            ["uint64"]  = new("uint64_t",  IsInt: true,  IsFloatTy: false, Rank: 5),
            ["usize"]   = new("size_t",    IsInt: true,  IsFloatTy: false, Rank: 5),
            ["uintptr"] = new("uintptr_t", IsInt: true,  IsFloatTy: false, Rank: 5),
            ["float"]   = new("float",     IsInt: false, IsFloatTy: true,  Rank: 6),
            ["double"]  = new("double",    IsInt: false, IsFloatTy: true,  Rank: 7),
            ["void"]    = new("void",      IsInt: false, IsFloatTy: false, Rank: 0),
        });

    private static readonly FrozenDictionary<string, PrimInfo>.AlternateLookup<ReadOnlySpan<char>> TableLookup =
        Table.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Returns true if the string is a recognised Gata primitive spelling.
    /// </summary>
    public static bool IsPrim(string s)
    {
        return Table.ContainsKey(s);
    }

    /// <summary>
    /// Returns true if the span is a recognised Gata primitive spelling.
    /// </summary>
    public static bool IsPrim(ReadOnlySpan<char> s)
    {
        return TableLookup.ContainsKey(s);
    }

    /// <summary>
    /// Returns the fixed-width C type for a primitive spelling.
    /// </summary>
    public static string ToC(string s)
    {
        return Table.TryGetValue(s, out var i) ? i.CType : s;
    }

    /// <summary>
    /// Returns true if the canonical token belongs to the integer family (bool included,
    /// matching C's integral treatment).
    /// </summary>
    public static bool IsIntCanon(string canon)
    {
        return Table.TryGetValue(canon, out var i) && i.IsInt;
    }

    /// <summary>
    /// Returns true if the canonical token is float or double.
    /// </summary>
    public static bool IsFloat(string canon)
    {
        return Table.TryGetValue(canon, out var i) && i.IsFloatTy;
    }

    /// <summary>
    /// Returns the numeric promotion rank used to pick the wider operand type.
    /// Unknown names take int's rank, mirroring the resolver's historical default.
    /// </summary>
    public static int Rank(string name)
    {
        return Table.TryGetValue(name, out var i) ? i.Rank : 4;
    }

    // Every accepted spelling - the front-end's set of primitive type names.
    public static readonly FrozenSet<string> Spellings = FrozenSet.ToFrozenSet(Table.Keys);
}

/// <summary>
/// Base type for all IR type nodes.
/// </summary>
internal abstract record IrType
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
    public virtual bool IsChar    => false;
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
    public static readonly IrClassRef String = new(BuiltinTypes.String);
}

/// <summary>
/// The void type - used as a return type for functions that produce no value.
/// </summary>
internal record IrVoidType : IrType
{
    public override string ToCType()
    {
        return "void";
    }

    public override string MangledName => "void";
    public override bool IsVoid => true;
}

/// <summary>
/// A primitive scalar type. CName is the canonical token;
/// ToCType lowers it to the corresponding fixed-width C type.
/// </summary>
internal record IrPrimType(string CName) : IrType
{
    private readonly string _cType = PrimTypes.ToC(CName);
    private readonly bool _isNumeric = PrimTypes.IsIntCanon(CName);
    private readonly bool _isFloat = PrimTypes.IsFloat(CName);
    private readonly bool _isChar = CName == "char";

    public override string ToCType()
    {
        return _cType;
    }

    public override string MangledName => CName;
    public override bool IsNumeric => _isNumeric;
    public override bool IsFloat   => _isFloat;
    public override bool IsChar    => _isChar;
}

/// <summary>
/// A reference to a named class type.
/// Lowers to a mangled pointer in C output.
/// </summary>
internal record IrClassRef(string ClassName) : IrType
{
    // No SymbolTable is threaded through IrType's static singletons, so this
    // still names BuiltinTypes.String directly rather than resolving a binding. The
    // constant itself is shared with everywhere else that references the same slot.
    private readonly bool _isString = ClassName == BuiltinTypes.String || ClassName == $"gata_{BuiltinTypes.String}";
    public override string ToCType()
    {
        return $"{Mangler.Class(ClassName)}*";
    }

    public override string MangledName => ClassName;
    public override bool IsString => _isString;
}

#endregion

#region Composite types

/// <summary>
/// A named integer-backed enum type. Distinct from int with no implicit conversion,
/// but comparable, assignable, and usable as a switch scrutinee. Lowers to a C enum.
/// </summary>
internal record IrEnumType(string Name) : IrType
{
    // An instance may be
    // constructed before Densifier.SetDense runs, at which point Mangler.Enum
    // would freeze the pre-dense name into a cached field.
    public override string ToCType()
    {
        return Mangler.Enum(Name);
    }

    public override string MangledName => Name;
}

/// <summary>
/// A pointer type for unsafe Gata code.
/// Lowers to a C pointer to the inner type.
/// </summary>
internal record IrPtrType(IrType Inner) : IrType
{
    // Computed on every call - see IrEnumType.ToCType for why this can't be cached.
    public override string ToCType()
    {
        return $"{Inner.ToCType()}*";
    }

    public override string MangledName => Inner.MangledName + "_p";
}

/// <summary>
/// A fixed-size array type [N]T - a value aggregate, not a heap reference.
/// Monomorphized per (element, size) pair into a named C struct.
/// Copies, returns, and iterates by value with the length carried in the type.
/// </summary>
internal record IrArrayType(IrType Elem, int Size) : IrType
{
    // Computed on every call - see IrEnumType.ToCType for why this can't be cached.
    public override string ToCType()
    {
        return Mangler.Class($"Arr_{Elem.MangledName}_{Size}");
    }

    public override string MangledName => $"Arr_{Elem.MangledName}_{Size}";

    /// <summary>
    /// Produces a stable C-identifier mangling of a type, used to name array structs
    /// and function-pointer typedefs.
    /// </summary>
    public static string Mangle(IrType t)
    {
        return t.MangledName;
    }
}

/// <summary>
/// A Result-of-T wrapper produced by throws functions.
/// Lowers to a C struct with a bool tag and a value or error payload.
/// </summary>
internal record IrResultType(IrType Inner) : IrType
{
    /// <summary>
    /// The C typedef name for this result type, e.g. Result_int or Result_MyClass.
    /// Derived from MangledName so it always agrees with the typedef registered by
    /// SymbolTable.RegisterThrows (see SymbolTable.ResultInnerName). void folds to int.
    /// </summary>
    public string ResultName => $"Result_{(Inner is IrVoidType ? "int" : Inner.MangledName)}";

    public override string ToCType()
    {
        return ResultName;
    }

    public override string MangledName => "Result_" + Inner.MangledName;
}

/// <summary>
/// A function-pointer type func(T1, T2) -> R.
/// ToCType returns a stable typedef name rather than an inline C declarator,
/// because inline declarators cannot be used with the type-then-name emission pattern.
/// The typedef is emitted once per distinct signature from IrModule.FuncPtrTypes.
/// </summary>
internal record IrFuncPtrType(IrType Ret, List<IrType> Params) : IrType
{
    public override string MangledName { get; } = $"Fn_{Ret.MangledName}__{(
        Params.Count switch
        {
            0 => "",
            1 => Params[0].MangledName,
            _ => string.Join("_", Params.Select(p => p.MangledName))
        }
    )}";

    // Computed on every call - see IrEnumType.ToCType for why this can't be cached.
    public override string ToCType()
    {
        return Mangler.Class($"Fn_{Ret.MangledName}__{(
            Params.Count switch
            {
                0 => "",
                1 => Params[0].MangledName,
                _ => string.Join("_", Params.Select(p => p.MangledName))
            }
        )}");
    }
}

/// <summary>
/// A named tagged-union type. Not generic, not ARC-managed.
/// Lowers to a tag enum and a C struct containing the tag and a union of per-variant payload structs.
/// </summary>
internal record IrUnionType(string Name) : IrType
{
    // Computed on every call - see IrEnumType.ToCType for why this can't be cached.
    public override string ToCType()
    {
        return Mangler.Union(Name);
    }

    public override string MangledName => Name;
}

#endregion

#region Expressions

/// <summary>
/// Base type for all IR expression nodes.
/// Every expression carries its result type and an optional source span.
/// </summary>
internal abstract record IrExpr(IrType Type) { public TextSpan Span { get; init; } = TextSpan.None; }

// Note for literals:

// IrLitInt - Value is the literal's 64-bit bit pattern. T is the type selected by
// suffix or magnitude (int by default). CText, when set, is the exact C
// text to emit (hex forms, suffixed forms); otherwise Value is printed.
// IrLitFloat - Raw is emitted verbatim (valid C including exponent and trailing `f`).
// T is double by default, float for an `f` suffix.

/// <summary>
/// An integer literal. Value is the 64-bit bit pattern; CText overrides the emitted text when set.
/// </summary>
internal record IrLitInt(long Value, IrType? T = null, string? CText = null) : IrExpr(T ?? IrType.Int);

/// <summary>
/// A character literal. Codepoint is the Unicode code point of the character.
/// </summary>
internal record IrLitChar(int Codepoint) : IrExpr(IrType.Char);

/// <summary>
/// A floating-point literal. Raw is emitted verbatim as valid C text.
/// </summary>
internal record IrLitFloat(string Raw, IrType? T = null) : IrExpr(T ?? IrType.Double);

/// <summary>
/// A boolean literal.
/// </summary>
internal record IrLitBool(bool Value) : IrExpr(IrType.Bool);

/// <summary>
/// A string literal. Raw includes the surrounding quotes.
/// </summary>
internal record IrLitString(string Raw) : IrExpr(IrType.String);

/// <summary>
/// A null literal of a specific type.
/// </summary>
internal record IrLitNull(IrType T) : IrExpr(T);

/// <summary>
/// A reference to a named enum member.
/// </summary>
internal record IrEnumConst(string EnumName, string Member) : IrExpr(new IrEnumType(EnumName));

/// <summary>
/// A local variable or parameter reference.
/// </summary>
internal record IrVar(string Name, IrType T, bool IsRef = false) : IrExpr(T);

/// <summary>
/// A reference to the implicit self object inside a method body.
/// </summary>
internal record IrSelfExpr(string ClassName) : IrExpr(new IrClassRef(ClassName));

/// <summary>
/// A field load from an object expression.
/// </summary>
internal record IrFieldLoad(IrExpr Obj, string Field, IrType FieldType) : IrExpr(FieldType);

/// <summary>
/// An index expression into a collection or fixed array.
/// </summary>
internal record IrIndex(IrExpr Obj, IrExpr Idx, IrType ElemType) : IrExpr(ElemType);

// Calls - CName is the fully-qualified C function name
/// <summary>
/// A call to a static (free) C function.
/// </summary>
internal record IrStaticCall(string CName, IrType RetType, List<IrExpr> Args) : IrExpr(RetType);

/// <summary>
/// A call to an instance method, passing the receiver as the first argument.
/// </summary>
internal record IrInstanceCall(IrExpr Recv, string CName, IrType RetType, List<IrExpr> Args) : IrExpr(RetType);

/// <summary>
/// A call to a throws-annotated static function. The result type wraps the inner type in Result.
/// </summary>
internal record IrThrowsCall(string CName, IrType InnerType, List<IrExpr> Args) : IrExpr(new IrResultType(InnerType));

/// <summary>
/// A call to a throws-annotated instance method. The result type wraps the inner type in Result.
/// </summary>
internal record IrThrowsInstanceCall(IrExpr Recv, string CName, IrType InnerType, List<IrExpr> Args) : IrExpr(new IrResultType(InnerType));

/// <summary>
/// A bare reference to a free function by name, decaying to a function-pointer value.
/// CName is a valid C function-pointer value with no cast needed.
/// </summary>
internal record IrFuncRef(string CName, IrFuncPtrType T) : IrExpr(T);

/// <summary>
/// A call through a function-pointer-typed expression.
/// </summary>
internal record IrIndirectCall(IrExpr Target, IrType Ret, List<IrExpr> Args) : IrExpr(Ret);

/// <summary>
/// Constructs a union variant value. VariantIndex selects the tag.
/// </summary>
internal record IrUnionConstruct(IrUnionType T, int VariantIndex, List<IrExpr> Args) : IrExpr(T);

/// <summary>
/// Reads one payload field of a union's active variant.
/// Only emitted after the tag has already been tested.
/// </summary>
internal record IrUnionField(IrExpr Union, int VariantIndex, string Field, IrType FieldType) : IrExpr(FieldType);

/// <summary>
/// A binary operator expression.
/// </summary>
internal record IrBinOp(BinOp Op, IrExpr Left, IrExpr Right, IrType T) : IrExpr(T);

/// <summary>
/// A ternary conditional expression.
/// </summary>
internal record IrTernary(IrExpr Cond, IrExpr Then, IrExpr Else, IrType T) : IrExpr(T);

/// <summary>
/// A prefix unary operator expression.
/// </summary>
internal record IrUnaryOp(UnOp Op, IrExpr Operand, IrType T) : IrExpr(T);

/// <summary>
/// A postfix operator expression such as i++ or i--.
/// </summary>
internal record IrPostfix(PostfixOp Op, IrExpr Operand) : IrExpr(Operand.Type);

/// <summary>
/// An explicit cast to a target type.
/// </summary>
internal record IrCast(IrType To, IrExpr Value) : IrExpr(To);

// Allocation
/// <summary>
/// A heap allocation of a named class with constructor arguments.
/// </summary>
internal record IrNew(string ClassName, List<IrExpr> Args) : IrExpr(new IrClassRef(ClassName));

/// <summary>
/// A heap allocation followed by repeated Add calls to populate a collection.
/// Lowered to a GNU statement expression by the emitter.
/// </summary>
internal record IrNewInit(string ClassName, List<IrExpr> Args, string AddCName, List<IrExpr> Inits)
    : IrExpr(new IrClassRef(ClassName));

/// <summary>
/// A fixed-array literal [e1, e2, ...] lowered to a C compound literal.
/// </summary>
internal record IrArrayLit(IrArrayType ArrType, List<IrExpr> Elems) : IrExpr(ArrType);

/// <summary>
/// An interpolated string whose parts are all typed String.
/// </summary>
internal record IrInterp(List<IrExpr> Parts) : IrExpr(IrType.String);

/// <summary>
/// Takes the address of a target expression, producing a pointer.
/// </summary>
internal record IrAddrOf(IrExpr Target) : IrExpr(new IrPtrType(Target.Type));

/// <summary>
/// Dereferences a pointer expression to yield the pointed-to value.
/// </summary>
internal record IrDeref(IrExpr Ptr, IrType T) : IrExpr(T);

/// <summary>
/// A sizeof expression. Emits as C sizeof(ctype).
/// </summary>
internal record IrSizeof(IrType Of) : IrExpr(IrType.SizeT);

/// <summary>
/// A default value expression. Emits as a C zero-cast: (ctype)0.
/// </summary>
internal record IrDefault(IrType Of) : IrExpr(Of);

#endregion

#region Statements

/// <summary>
/// Base type for all IR statement nodes.
/// Every statement carries an optional source span.
/// </summary>
internal abstract record IrStmt { public TextSpan Span { get; init; } = TextSpan.None; }

/// <summary>
/// A sequential list of statements forming a scope.
/// </summary>
internal record IrBlock(List<IrStmt> Stmts) : IrStmt;

/// <summary>
/// A native C statement, spliced verbatim.
/// </summary>
internal record IrNativeStmt(string C) : IrStmt;

// Verbatim C produced by a lowering pass (Result branches, gotos/labels). Printed as-is.
/// <summary>
/// Verbatim C code produced by a lowering pass such as Result branches or goto/label pairs.
/// </summary>
internal record IrRaw(string Code) : IrStmt;

/// <summary>
/// A local variable declaration with an optional initializer.
/// </summary>
internal record IrDeclVar(string Name, IrType Type, IrExpr? Init) : IrStmt;

/// <summary>
/// An assignment expression statement. Op is the assignment operator kind, e.g. Assign, AddAssign.
/// </summary>
internal record IrAssign(IrExpr Target, AssignOp Op, IrExpr Value) : IrStmt;

/// <summary>
/// An expression evaluated for its side effects, result discarded.
/// </summary>
internal record IrExprStmt(IrExpr Expr) : IrStmt;

/// <summary>
/// A return statement with an optional value.
/// </summary>
internal record IrReturn(IrExpr? Value) : IrStmt;

/// <summary>
/// A break statement exiting the nearest enclosing loop or switch.
/// </summary>
internal record IrBreak() : IrStmt;

/// <summary>
/// A continue statement jumping to the next iteration of the nearest enclosing loop.
/// </summary>
internal record IrContinue() : IrStmt;

/// <summary>
/// An if/else statement. Else is null when there is no else branch.
/// </summary>
internal record IrIf(IrExpr Cond, IrBlock Then, IrBlock? Else) : IrStmt;

/// <summary>
/// A while loop.
/// </summary>
internal record IrWhile(IrExpr Cond, IrBlock Body) : IrStmt;

/// <summary>
/// A C-style for loop with optional init, condition, and step.
/// </summary>
internal record IrFor(IrStmt? Init, IrExpr? Cond, IrStmt? Step, IrBlock Body) : IrStmt;

/// <summary>
/// A for-in loop over a collection or fixed array.
/// </summary>
internal record IrForIn(string Var, IrType ElemType, string LenCName, string GetCName,
               IrExpr Collection, IrBlock Body, int ArraySize = -1) : IrStmt;

/// <summary>
/// A try/catch block. Seq is a unique sequence number used to name generated labels.
/// </summary>
internal record IrTryCatch(IrBlock Try, IrBlock Catch, int Seq) : IrStmt;

/// <summary>
/// A switch statement. Lowered to an if/else-if chain by Desugar; never reaches the backend.
/// </summary>
internal record IrSwitch(IrExpr Scrutinee, List<IrSwitchCase> Cases, IrBlock? Default) : IrStmt;

/// <summary>
/// One case arm of an IrSwitch, with one or more labels and a body block.
/// </summary>
internal record IrSwitchCase(List<IrExpr> Labels, IrBlock Body);

/// <summary>
/// A match statement over a union type. Lowered to an if/else-if chain by Desugar; never reaches the backend.
/// </summary>
internal record IrMatch(IrExpr Scrutinee, IrUnionType UnionT, List<IrMatchCase> Cases, IrBlock? Default) : IrStmt;

/// <summary>
/// A single binding introduced by a match pattern - maps a variant field to a local name.
/// </summary>
internal record IrMatchBind(string FieldName, string BindName, IrType Type);

/// <summary>
/// One case arm of an IrMatch, identified by variant index with its pattern bindings.
/// </summary>
internal record IrMatchCase(int VariantIndex, List<IrMatchBind> Binds, IrBlock Body);

/// <summary>
/// An unsafe block containing statements that may use pointer operations.
/// </summary>
internal record IrUnsafeBlock(IrBlock Body) : IrStmt;

/// <summary>
/// A defer statement. Lowered by the Ownership pass; never reaches the backend.
/// </summary>
internal record IrDefer(IrStmt Action) : IrStmt;

/// <summary>
/// A throw statement. Lowered by the Ownership pass; never reaches the backend.
/// </summary>
internal record IrThrow() : IrStmt;

/// <summary>
/// A debug assertion. Emitted as a direct call to the env debug binding with a raw C string literal.
/// </summary>
internal record IrDebug(string Raw) : IrStmt;

/// <summary>
/// A panic statement. Emitted as a direct call to the env panic binding with a raw C string literal.
/// </summary>
internal record IrPanic(string Raw) : IrStmt;

#endregion

#region Functions and classes

internal enum Visibility { Shared, Kernel, User }

/// <summary>
/// A single parameter in an IR function signature.
/// </summary>
internal record IrParam(string Name, IrType Type, bool IsRef = false);

/// <summary>
/// An IR function - either a free function or a class method.
/// Body is null for native functions; Native carries the C text instead.
/// </summary>
internal record IrFunction(
    string Name,
    string CName,
    IrType ReturnType,
    List<IrParam> Params,
    bool IsStatic,
    bool IsEntry,
    bool IsThrows,
    bool IsLib,
    Visibility Vis,
    string? OwnerClass,
    IrBlock? Body,
    string? Native,
    List<Annotation> Annotations
);

/// <summary>
/// A field declaration on a class, with an optional default initializer.
/// </summary>
internal record IrField(string Name, IrType Type, IrExpr? Init);

/// <summary>
/// A raw native struct-field block.
/// </summary>
internal record RawFieldBlock(string C);

/// <summary>
/// An operator overload defined on a class.
/// Body is null for native operators; Native carries the C text instead.
/// IsStatic is true only for the one parameter form of 'as' (a static factory converting its
/// parameter to self, eg. String's 'operator String func as(char c)') - every other
/// operator, including zero-parameter 'as', is an instance operator over self.
/// </summary>
internal record IrOperator(
    string Op,
    string CName,
    IrType ReturnType,
    List<IrParam> Params,
    string OwnerClass,
    bool IsLib,
    Visibility Vis,
    IrBlock? Body,
    string? Native,
    bool IsStatic = false
);

/// <summary>
/// An IR class declaration with its fields, methods, and operator overloads.
/// Keep marks a class as exempt from Dce reachability and Densifier renaming.
/// </summary>
internal record IrClass(
    string Name,
    string CName,
    bool IsLib,
    Visibility Vis,
    List<RawFieldBlock> RawFields,
    List<IrField> Fields,
    List<IrFunction> Methods,
    List<IrOperator> Operators,
    bool HasInit,
    Dictionary<string, IrExpr> FieldInits,
    bool IsModule = false,
    // @keep - exempt from Dce reachability sweep and Densifier dense renaming.
    bool Keep = false
);

/// <summary>
/// A process declaration grouping one or more threads.
/// </summary>
internal record IrProcess(string Name, string Mode, List<IrThread> Threads);

/// <summary>
/// A single thread within a process, with a fully-qualified name and optional entry function.
/// Deployment mode lives on the owning process; threads have none of their own.
/// </summary>
internal record IrThread(string Name, string FullName, IrFunction? EntryFunc);

#endregion

#region Module

// Where a native block lands in the output. Types (default) -> the type section,
// alongside structs. Preamble -> before #include "shared.h". Boot -> after all functions.
internal enum NativeSection { Types, Preamble, Boot }

/// <summary>
/// A native C block with a target output section.
/// </summary>
internal record IrNativeBlock(string C, Visibility Vis,
                     NativeSection Section = NativeSection.Types);

/// <summary>
/// A native type declaration - a C struct declared inside Gata source.
/// </summary>
internal record IrNativeType(
    string Name,
    string CName,
    string C,
    Visibility Vis
);

/// <summary>
/// An enum declaration. Members carry optional explicit C values.
/// </summary>
internal record IrEnum(string Name, string CName, List<(string Name, string? CValue)> Members);

/// <summary>
/// One variant of a union type, with a tag name and its payload fields.
/// </summary>
internal record IrUnionVariant(string Name, string TagCName, List<IrParam> Fields);

/// <summary>
/// A union type declaration with all its variants.
/// </summary>
internal record IrUnion(string Name, string CName, List<IrUnionVariant> Variants);

/// <summary>
/// The top-level IR module produced by the type resolver.
/// Carries all classes, functions, native blocks, and supporting type lists.
/// </summary>
internal record IrModule(
    List<IrNativeBlock> NativeBlocks,
    List<IrNativeType> NativeTypes,
    List<IrClass> Classes,
    List<IrFunction> FreeFunctions,
    List<IrProcess> Processes,
    List<IrArrayType> ArrayTypes,
    List<IrEnum> Enums,
    SymbolTable Symbols,
    List<IrFuncPtrType> FuncPtrTypes,
    List<IrUnion> Unions
)
{
    // The realms a build emits are those the environment declared via @preamble:
    // (kernel)/(boot) -> kernel realm; (user) -> user realm. Computed on every access,
    // not cached at construction - NativeBlocks is populated by ResolveTop's Add calls
    // on this same instance after the constructor already ran, so a get-only property
    // initializer here would always see an empty list.
    /// <summary>
    /// Returns true if the module emits a kernel realm, determined by the presence of kernel preamble or boot blocks.
    /// </summary>
    public bool HasKernelRealm => NativeBlocks.Any(nb =>
        nb.Vis == Visibility.Kernel && nb.Section is NativeSection.Preamble or NativeSection.Boot);

    /// <summary>
    /// Returns true if the module emits a user realm, determined by the presence of user preamble blocks.
    /// </summary>
    public bool HasUserRealm => NativeBlocks.Any(nb =>
        nb.Vis == Visibility.User && nb.Section == NativeSection.Preamble);
}

#endregion
