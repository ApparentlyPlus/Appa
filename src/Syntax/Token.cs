namespace Appa;

/// <summary>
/// Represents the type of a token in the Appa programming language.
/// </summary>
enum TK
{
    // Literals
    Ident, IntLit, FloatLit, StrLit, BoolLit, InterpStrStart, InterpStrEnd, CharLit,

    // Native block / native type
    NativeContent, NativeTypeDecl,

    // Keywords (Structure)
    Import, Kernel, User,
    Process, Thread, Foreground, Background,
    Class, Module, Func, Static, Public, Private,
    Entry, Throws, Operator, As, Fields, Ref,

    // Annotations (@ prefix, parsed as keywords)
    AtIntrinsic, AtPreamble, AtExtern, AtEnvironment, AtKeep,

    // Keywords (Flow control)
    Return, If, Else, While, For, In, Break, Continue, Switch, Case,
    Try, Catch, New, Let, Null, Unsafe, Throw, Sizeof, Default, Enum,
    Debug, Panic, Defer, Match, Union,

    // Primitive types
    TBool, TInt, TChar, TFloat, TDouble, TShort, TVoid, TPrim,

    /// <Note>
    /// TPrim is the width explicit family (int64/uint/uint64/ushort/byte/sbyte/usize/uintptr)
    /// Its spelling is carried in the token value.
    /// </Note>

    // Compound assignment
    PlusEq, MinusEq, StarEq, SlashEq, PercentEq,
    AmpEq, PipeEq, CaretEq, ShlEq, ShrEq,

    // Operators
    EqEq, NotEq, LtEq, GtEq, And, Or, Inc, Dec, Arrow,

    // Punctuation
    Punct,

    // End of file
    EOF
}

/// <summary>
/// A single token produced by the lexer. Carries its kind, raw text value, and source location.
/// </summary>
readonly struct Token(TK kind, string value, TextSpan span)
{
    public TK Kind { get; } = kind;
    public string Value { get; } = value;
    public TextSpan Span { get; } = span;
}
