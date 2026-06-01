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
/// A generic instantiation site found during parsing, eg. List[int]. The Monomorphizer
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
/// The environment file provides the intrinsic bindings (I/O, ARC, panic) for the target.
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
/// An operator overload inside a class. Op is the operator symbol string ("+", "==").
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
/// Base class for all expression nodes.
/// </summary>
abstract record Expr(TextSpan Span);

/// <summary>
/// Base class for all statement nodes.
/// </summary>
abstract record Stmt(TextSpan Span);

/// <summary>
/// A brace-delimited sequence of statements forming a lexical scope.
/// </summary>
record Block(IReadOnlyList<Stmt> Stmts, TextSpan Span) : Stmt(Span);

#endregion

#region Expressions

/// <summary>
/// An integer literal, eg. 42 or 0xFF. Value holds the raw source spelling including
/// any suffix (u, L) so the backend can emit the right C constant.
/// </summary>
record IntLitExpr(string Value, TextSpan Span) : Expr(Span);

/// <summary>
/// A character literal, eg. 'a' or '\n'. Value is the decoded Unicode codepoint.
/// </summary>
record CharLitExpr(int Value, TextSpan Span) : Expr(Span);

/// <summary>
/// A floating-point literal, eg. 3.14 or 1e9f. Value holds the raw source spelling
/// including any suffix so the backend can choose float vs double.
/// </summary>
record FloatLitExpr(string Value, TextSpan Span) : Expr(Span);

/// <summary>
/// A boolean literal. Value is "true" or "false".
/// </summary>
record BoolLitExpr(string Value, TextSpan Span) : Expr(Span);

/// <summary>
/// A plain string literal. Value holds the decoded string content without surrounding quotes.
/// </summary>
record StrLitExpr(string Value, TextSpan Span) : Expr(Span);

/// <summary>
/// The null literal. Represents a null pointer or absent reference.
/// </summary>
record NullExpr(TextSpan Span) : Expr(Span);

/// <summary>
/// An interpolated string. Parts alternates between StrLitExpr (literal segments) and
/// arbitrary Expr (embedded expressions). Built by the parser from the InterpStrStart,
/// StrLit, brace-delimited expr, and InterpStrEnd token stream the lexer emits.
/// </summary>
record InterpStrExpr(IReadOnlyList<Expr> Parts, TextSpan Span) : Expr(Span);

/// <summary>
/// A bare identifier used as an expression, eg. a variable or function name.
/// </summary>
record IdentExpr(string Name, TextSpan Span) : Expr(Span);

/// <summary>
/// An explicit type cast, eg. (int) x. TargetType is the destination type name.
/// </summary>
record CastExpr(string TargetType, Expr Value, TextSpan Span) : Expr(Span);

/// <summary>
/// A function or method call. Callee may be any expression that resolves to a callable.
/// </summary>
record CallExpr(Expr Callee, IReadOnlyList<Expr> Args, TextSpan Span) : Expr(Span);

/// <summary>
/// Member access via dot, eg. obj.field or obj.method. Member is the field or method name.
/// </summary>
record MemberAccessExpr(Expr Object, string Member, TextSpan Span) : Expr(Span);

/// <summary>
/// Index access, eg. arr[i]. Object is the collection expression, Index is the subscript.
/// </summary>
record IndexExpr(Expr Object, Expr Index, TextSpan Span) : Expr(Span);

/// <summary>
/// A binary expression. Op is the operator string.
/// </summary>
record BinExpr(string Op, Expr Left, Expr Right, TextSpan Span) : Expr(Span);

/// <summary>
/// A ternary conditional: cond ? then : else.
/// </summary>
record TernaryExpr(Expr Cond, Expr Then, Expr Else, TextSpan Span) : Expr(Span);

/// <summary>
/// A prefix unary expression. Op is the operator string.
/// </summary>
record UnaryExpr(string Op, Expr Operand, TextSpan Span) : Expr(Span);

/// <summary>
/// A postfix unary expression, eg. x++ or x--. Op comes after the operand, unlike UnaryExpr.
/// </summary>
record PostfixExpr(string Op, Expr Operand, TextSpan Span) : Expr(Span);

/// <summary>
/// Object construction. Args holds constructor arguments for class instantiation;
/// CollectionInit holds the bracketed element list for collection construction.
/// </summary>
record NewExpr(string Type, IReadOnlyList<Expr> Args, IReadOnlyList<Expr> CollectionInit, TextSpan Span) : Expr(Span);

/// <summary>
/// A fixed-size array literal, eg. [e1, e2, e3].
/// </summary>
record ArrayLitExpr(IReadOnlyList<Expr> Elems, TextSpan Span) : Expr(Span);

/// <summary>
/// Address-of expression. Takes the address of an lvalue. Only legal inside unsafe blocks.
/// </summary>
record AddrOfExpr(Expr Target, TextSpan Span) : Expr(Span);

/// <summary>
/// A ref argument at a call site, eg. ref x. Passes an lvalue by reference.
/// Only legal as a direct call argument, not in any other expression position.
/// </summary>
record RefArgExpr(Expr Target, TextSpan Span) : Expr(Span);

/// <summary>
/// Pointer dereference, eg. *ptr. Only legal inside unsafe blocks.
/// </summary>
record DerefExpr(Expr Ptr, TextSpan Span) : Expr(Span);

/// <summary>
/// sizeof(T) expression. Evaluates to the size_t byte count of the named type.
/// </summary>
record SizeofExpr(string TypeName, TextSpan Span) : Expr(Span);

/// <summary>
/// default(T) expression. Evaluates to the zero value of the named type.
/// </summary>
record DefaultExpr(string TypeName, TextSpan Span) : Expr(Span);

#endregion

#region Statements

/// <summary>
/// A verbatim native C statement embedded inside a Gata method body.
/// </summary>
record NativeStmt(NativeBody Body, TextSpan Span) : Stmt(Span);

/// <summary>
/// A local variable declaration. Type is null when the type is inferred from the initializer.
/// Init is null for declarations without an initializer.
/// </summary>
record LetStmt(string? Type, string Name, Expr? Init, TextSpan Span) : Stmt(Span);

/// <summary>
/// An assignment statement. Op is the assignment operator ("=", "+=", "-=", etc.).
/// Target must be an lvalue expression.
/// </summary>
record AssignStmt(Expr Target, string Op, Expr Value, TextSpan Span) : Stmt(Span);

/// <summary>
/// An expression used as a statement, typically a call expression whose return value is discarded.
/// </summary>
record ExprStmt(Expr E, TextSpan Span) : Stmt(Span);

/// <summary>
/// An if/else statement. Else is null when there is no else branch.
/// </summary>
record IfStmt(Expr Cond, Stmt Then, Stmt? Else, TextSpan Span) : Stmt(Span);

/// <summary>
/// A while loop.
/// </summary>
record WhileStmt(Expr Cond, Stmt Body, TextSpan Span) : Stmt(Span);

/// <summary>
/// A C-style for loop. Init, Cond, and Step are all optional.
/// </summary>
record ForStmt(Stmt? Init, Expr? Cond, Expr? Step, Block Body, TextSpan Span) : Stmt(Span);

/// <summary>
/// A for-in loop that iterates over a collection. Var is the loop variable name.
/// </summary>
record ForInStmt(string Var, Expr Collection, Block Body, TextSpan Span) : Stmt(Span);

/// <summary>
/// A return statement. Value is null for void returns.
/// </summary>
record ReturnStmt(Expr? Value, TextSpan Span) : Stmt(Span);

/// <summary>
/// A break statement that exits the nearest enclosing loop or switch.
/// </summary>
record BreakStmt(TextSpan Span) : Stmt(Span);

/// <summary>
/// A continue statement that skips to the next iteration of the nearest enclosing loop.
/// </summary>
record ContinueStmt(TextSpan Span) : Stmt(Span);

/// <summary>
/// A try/catch statement for Result-based error propagation. The catch block receives
/// control when the try block throws.
/// </summary>
record TryCatchStmt(Block Try, Block Catch, TextSpan Span) : Stmt(Span);

/// <summary>
/// A switch statement. Cases is the list of arms; Default is the optional fallback block.
/// There is no fallthrough: break and continue inside a case target the enclosing loop.
/// </summary>
record SwitchStmt(Expr Scrutinee, IReadOnlyList<SwitchCase> Cases, Block? Default, TextSpan Span) : Stmt(Span);

/// <summary>
/// One arm of a switch statement. Labels is the list of values that route to this arm.
/// </summary>
record SwitchCase(IReadOnlyList<Expr> Labels, Block Body, TextSpan Span);

/// <summary>
/// A match statement that scrutinizes a union value by variant. Each case binds the
/// variant's fields as locals in its body. Default is the optional fallback block.
/// </summary>
record MatchStmt(Expr Scrutinee, IReadOnlyList<MatchCase> Cases, Block? Default, TextSpan Span) : Stmt(Span);

/// <summary>
/// One arm of a match statement. Variant is the union variant name; Bindings are the
/// local names bound to the variant's fields in source order.
/// </summary>
record MatchCase(string Variant, IReadOnlyList<string> Bindings, Block Body, TextSpan Span);

/// <summary>
/// An unsafe block. Pointer operations (address-of, dereference) are only legal inside one.
/// </summary>
record UnsafeBlock(IReadOnlyList<Stmt> Stmts, TextSpan Span) : Stmt(Span);

/// <summary>
/// A defer statement. Action runs on every exit from the enclosing block, in LIFO order
/// with other defers. Action may not itself transfer control.
/// </summary>
record DeferStmt(Stmt Action, TextSpan Span) : Stmt(Span);

/// <summary>
/// A throw statement that aborts the enclosing throws function or try block with an error Result.
/// </summary>
record ThrowStmt(TextSpan Span) : Stmt(Span);

/// <summary>
/// A debug statement. Raw is the raw string literal including quotes. Lowered to the
/// environment's debug binding. Hard error in a release build.
/// </summary>
record DebugStmt(string Raw, TextSpan Span) : Stmt(Span);

/// <summary>
/// A panic statement. Raw is the raw string literal including quotes. Lowered to the
/// environment's panic binding. Only legal in kernel context. Hard error in a release build.
/// </summary>
record PanicStmt(string Raw, TextSpan Span) : Stmt(Span);

#endregion
