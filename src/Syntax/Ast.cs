namespace Appa;

// Every AST node carries a TextSpan by construction. No node exists without a location.

using System.Collections.Generic;

/// <summary>
/// Root of the AST. Holds all top-level declarations in source order, plus generic instantiation
/// requests collected during parsing and consumed by the Monomorphizer.
/// </summary>
record Program(IReadOnlyList<TopLevel> Items)
{
    public IReadOnlyList<GenericUse> GenericUses { get; init; } = [];
}

/// <summary>
/// A generic instantiation site found during parsing, e.g. List[int]. The Monomorphizer
/// reads these to know which concrete copies to generate before the type resolver runs.
/// </summary>
record GenericUse(string Base, IReadOnlyList<string> Args, TextSpan Span);

#region Top-level declarations

/// <summary>
/// Base class for all top-level declarations. Every subclass carries the source span
/// of its full declaration via the primary constructor.
/// </summary>
abstract record TopLevel(TextSpan Span);

/// <summary>
/// import "path" or import name. Pulls another Gata source file into the build.
/// IsPath distinguishes a filesystem path (true) from a bare module name (false).
/// </summary>
record ImportDecl(string Name, bool IsPath, TextSpan Span) : TopLevel(Span);

/// <summary>
/// Marks exactly one file in the build as the environment definition.
/// The environment file provides the intrinsic bindings (I/O, ARC, panic, etc.) for the target.
/// </summary>
record EnvironmentDecl(TextSpan Span) : TopLevel(Span);

/// <summary>
/// A native { … } block containing raw C source captured verbatim. Routed to the
/// kernel or user translation unit by NativeC based on the block's section marker.
/// </summary>
record NativeBlock(NativeBody Body, TextSpan Span, IReadOnlyList<Annotation>? Annotations = null) : TopLevel(Span);

/// <summary>
/// class or module declaration. IsModule = true means all members are implicitly static, meaning
/// no self parameter, no instances. GenericParams non-empty makes it a generic class
/// monomorphized per concrete type argument set.
/// </summary>
record ClassDecl(string Name, IReadOnlyList<string> GenericParams, IReadOnlyList<Annotation> Annotations,
                 IReadOnlyList<ClassMember> Members, TextSpan Span, bool IsModule = false) : TopLevel(Span);

/// <summary>
/// kernel { … } or user { … } block. Groups top-level declarations that belong to one
/// execution environment. Kind is the keyword ("kernel" or "user").
/// </summary>
record ContextDecl(string Kind, IReadOnlyList<TopLevel> Items, TextSpan Span) : TopLevel(Span);

/// <summary>
/// A free function declaration. GenericParams empty = ordinary function; non-empty = generic
/// template monomorphized per call site with type arguments inferred from the argument types.
/// IsEntry marks it as a thread entry point; Throws means it may propagate a Result error.
/// </summary>
record FuncDecl(IReadOnlyList<string> Modifiers, IReadOnlyList<Annotation> Annotations, string? ReturnType,
                string Name, IReadOnlyList<string> GenericParams, IReadOnlyList<Param> Params,
                bool IsEntry, bool Throws, MethodBody Body, TextSpan Span) : TopLevel(Span);

/// <summary>
/// A process declaration is pure deployment topology. A process is a named bag of threads;
/// it holds no logic of its own. Mode is the deployment mode ("foreground" or "background").
/// </summary>
record ProcessDecl(string Name, string Mode, IReadOnlyList<ThreadDecl> Threads, TextSpan Span) : TopLevel(Span);

/// <summary>
/// An extern function pre-declaration that tells the compiler a C function exists so it can
/// be called from Gata without a Gata body. Translated to a forward prototype in the backend.
/// </summary>
record ExternFuncDecl(string? ReturnType, string Name, IReadOnlyList<Param> Params,
                      TextSpan Span, IReadOnlyList<Annotation>? Annotations = null) : TopLevel(Span);

/// <summary>
/// native type Name { C body }. It registers a C struct as a named Gata type. The CBody is
/// emitted verbatim as a typedef; the name becomes resolvable in type positions.
/// </summary>
record NativeTypeDecl(string Name, string CBody, TextSpan Span, IReadOnlyList<Annotation>? Annotations = null) : TopLevel(Span);

/// <summary>
/// enum Name { A, B = 2, C } is a distinct integer-backed type with named members.
/// Members may carry explicit integer values; unspecified members follow C's increment rule.
/// </summary>
record EnumDecl(string Name, IReadOnlyList<EnumMember> Members, TextSpan Span, IReadOnlyList<Annotation>? Annotations = null) : TopLevel(Span);

/// <summary>
/// One member of an enum. Value is null when the member takes the implicit next integer.
/// </summary>
record EnumMember(string Name, Expr? Value, TextSpan Span);

/// <summary>
/// union Name { Circle(float radius), Square(float side), Point } is a tagged union.
/// Each variant either carries named fields or no payload. Lowered to a tag enum + C union.
/// </summary>
record UnionDecl(string Name, IReadOnlyList<UnionVariant> Variants, TextSpan Span, IReadOnlyList<Annotation>? Annotations = null) : TopLevel(Span);

/// <summary>
/// One variant of a union. Fields is empty for a payload-free variant like Point.
/// </summary>
record UnionVariant(string Name, IReadOnlyList<Param> Fields, TextSpan Span);

#endregion

#region Native body and annotations

/// <summary>
/// The captured C source of a native block, split into the kernel-side and user-side portions
/// by NativeC. Either string may be empty if the block had no content for that side.
/// </summary>
record NativeBody(string KernelC, string UserC);

/// <summary>
/// Base class for all annotations (@intrinsic, @preamble, @keep). Concrete subtypes added
/// in a follow-up commit.
/// </summary>
abstract record Annotation;

#endregion

#region Class members

/// <summary>
/// Base class for all members that can appear inside a class or module body.
/// </summary>
abstract record ClassMember(TextSpan Span);

/// <summary>
/// The fields { … } block is raw C struct fields injected into the emitted struct typedef.
/// </summary>
record FieldsBlock(NativeBody Body, TextSpan Span) : ClassMember(Span);

/// <summary>
/// A Gata field declaration. Init is the optional initializer expression; Type is null
/// when inferred.
/// </summary>
record FieldDecl(IReadOnlyList<string> Modifiers, string? Type, string Name, TextSpan Span, Expr? Init = null) : ClassMember(Span);

/// <summary>
/// A method declaration inside a class or module. IsEntry marks it as a thread entry point;
/// Throws means it participates in the Result error-propagation protocol.
/// </summary>
record MethodDecl(IReadOnlyList<string> Modifiers, IReadOnlyList<Annotation> Annotations, string? ReturnType,
                  string Name, IReadOnlyList<Param> Params, bool IsEntry, bool Throws,
                  MethodBody Body, TextSpan Span) : ClassMember(Span);

/// <summary>
/// An operator overload inside a class. Op is the operator symbol string ("+", "==", etc.).
/// </summary>
record OperatorDecl(string Op, IReadOnlyList<Param> Params, string? ReturnType, MethodBody Body, TextSpan Span) : ClassMember(Span);

#endregion

#region Method body

/// <summary>
/// Discriminates between a Gata block body and a verbatim native C body for methods and functions.
/// </summary>
abstract record MethodBody;

/// <summary>
/// A method whose implementation is a Gata statement block.
/// </summary>
record BlockBody(Block Block) : MethodBody;

/// <summary>
/// A method whose implementation is raw C source captured verbatim.
/// </summary>
record NativeMethodBody(NativeBody Native) : MethodBody;

#endregion

#region Process / thread

/// <summary>
/// A thread inside a process. Points at exactly one entry function.
/// Threads do not carry their own deployment mode, that belongs to the process.
/// </summary>
record ThreadDecl(string Name, EntryFuncDecl Entry, TextSpan Span);

/// <summary>
/// The entry function of a thread. It consists of parameters and a single block body. Not a FuncDecl
/// because it cannot be called from Gata code, only dispatched by the runtime.
/// </summary>
record EntryFuncDecl(IReadOnlyList<string> Modifiers, string? ReturnType, IReadOnlyList<Param> Params, Block Body, TextSpan Span);

#endregion

#region Parameters

/// <summary>
/// A function or method parameter. IsRef = true means the argument is passed by reference;
/// the call site must supply an lvalue prefixed with ref.
/// </summary>
record Param(string Type, string Name, TextSpan Span, bool IsRef = false);

#endregion

#region Expression and statement roots

/// <summary>
/// Base class for all expression nodes. Subclasses added in a follow-up commit.
/// </summary>
abstract record Expr(TextSpan Span);

/// <summary>
/// Base class for all statement nodes. Subclasses added in a follow-up commit.
/// </summary>
abstract record Stmt(TextSpan Span);

/// <summary>
/// A brace-delimited sequence of statements forming a lexical scope.
/// </summary>
record Block(IReadOnlyList<Stmt> Stmts, TextSpan Span) : Stmt(Span);

#endregion
