namespace Appa;

using System.Collections.Immutable;

/// <summary>
/// A generic instantiation's structure. The template it stamps and the arguments it stamps it over,
/// each already flat because a nested instantiation is registered under its own key.
/// </summary>
internal readonly record struct GenericKey(string Base, ImmutableArray<string> Args);

internal static class Mangler
{
    public const string KernelEntry = "gata_kernelspace_main";

    // C keywords and the standard macros that behave like them. None is a Gata keyword, so a
    // program may use them all - and the names emitted verbatim (locals, parameters, and the
    // members of a generated struct) are therefore the ones that can collide.
    private static readonly System.Collections.Frozen.FrozenSet<string> CReserved =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "auto", "break", "case", "char", "const", "continue", "default", "do", "double",
            "else", "enum", "extern", "float", "for", "goto", "if", "inline", "int", "long",
            "register", "restrict", "return", "short", "signed", "sizeof", "static", "struct",
            "switch", "typedef", "union", "unsigned", "void", "volatile", "while",
            "_Alignas", "_Alignof", "_Atomic", "_Bool", "_Complex", "_Generic", "_Imaginary",
            "_Noreturn", "_Static_assert", "_Thread_local",
            "bool", "true", "false", "NULL", "alignas", "alignof", "static_assert",
            "thread_local", "complex", "imaginary", "noreturn",

            // Object-like macros from the standard headers a hosted preamble includes
            "stdin", "stdout", "stderr", "EOF", "BUFSIZ", "FILENAME_MAX", "FOPEN_MAX", "TMP_MAX",
            "SEEK_SET", "SEEK_CUR", "SEEK_END", "L_tmpnam", "_IOFBF", "_IOLBF", "_IONBF",
            "EXIT_SUCCESS", "EXIT_FAILURE", "RAND_MAX", "MB_CUR_MAX", "errno", "CLOCKS_PER_SEC",
            "CHAR_BIT", "CHAR_MAX", "CHAR_MIN", "SCHAR_MAX", "SCHAR_MIN", "UCHAR_MAX",
            "SHRT_MAX", "SHRT_MIN", "USHRT_MAX", "INT_MAX", "INT_MIN", "UINT_MAX",
            "LONG_MAX", "LONG_MIN", "ULONG_MAX", "LLONG_MAX", "LLONG_MIN", "ULLONG_MAX",
            "HUGE_VAL", "HUGE_VALF", "INFINITY", "NAN", "M_PI", "M_E",
            "DBL_MAX", "DBL_MIN", "DBL_EPSILON", "FLT_MAX", "FLT_MIN", "FLT_EPSILON",
            "SIZE_MAX", "PTRDIFF_MAX", "INTPTR_MAX", "UINTPTR_MAX",
            "cdecl", "near", "far", "pascal", "winapi", "WINAPI", "CALLBACK", "APIENTRY",
            "IN", "OUT", "OPTIONAL", "CONST", "VOID", "min", "max",
            "TRUE", "FALSE", "INVALID_HANDLE_VALUE", "ERROR", "interface",
        ], StringComparer.Ordinal);

    /// <summary>
    /// Returns the C spelling of a local or parameter name. Names printed as written can collide
    /// with C's vocabulary; those get a trailing underscore. Apply at every site that prints the
    /// name.
    /// </summary>
    public static string Local(string name)
    {
        return CReserved.Contains(name) ? name + "_" : name;
    }

    /// <summary>
    /// Returns the C spelling of a struct member: a class field, a union variant, or a variant's
    /// payload field.
    /// </summary>
    public static string Member(string name)
    {
        return CReserved.Contains(name) ? name + "_" : name;
    }

    /// <summary>
    /// True if the name is a C keyword or a standard macro behaving like one, and so cannot stand
    /// as an identifier in emitted C. For the names this compiler cannot rename because the author
    /// pinned them to C text of their own.
    /// </summary>
    public static bool IsCReserved(string name)
    {
        return CReserved.Contains(name);
    }

    /// <summary>
    /// True if a user-written local would collide with a compiler temporary.
    /// </summary>
    public static bool IsReservedLocal(string name)
    {
        return name.StartsWith("__", StringComparison.Ordinal);
    }

    [field: ThreadStatic]
    private static NameTable _names
    {
        get => field ??= new NameTable();
        set;
    }

    /// <summary>
    /// Starts a compilation, discarding whatever the last one invented.
    /// </summary>
    public static void Begin() => _names = new NameTable();

    /// <summary>
    /// Starts a front-end round within the current compilation.
    /// </summary>
    public static void BeginRound() => _names.BeginRound();

    /// <summary>
    /// Replaces the dense name map with the given mapping produced by the Densifier.
    /// </summary>
    public static void SetDense(Dictionary<string, string> map) => _names.SetDense(map);

    /// <summary>
    /// The C spelling of an IR type under the current naming, composed on first ask.
    /// </summary>
    public static string CType(IrType t)
    {
        var cache = _names.CTypes;
        if (cache.TryGetValue(t, out var c)) return c;
        return cache[t] = t.ComposeCType();
    }

    /// <summary>
    /// Adopts the scope tree of the round about to run.
    /// </summary>
    public static void SetScopes(ScopeTree tree) => _names.Scopes = tree;

    /// <summary>
    /// Records that this instantiation was rejected with a diagnostic of its own.
    /// </summary>
    public static void MarkGenericFailed(string mangled) => _names.Failed.Add(mangled);

    /// <summary>
    /// True if this instantiation was already rejected, so a missing-type report would cascade.
    /// </summary>
    public static bool GenericFailed(string mangled) => _names.Failed.Contains(mangled);

    /// <summary>
    /// Composes the internal name of a generic instantiation: ("List", ["int"]) is "List_int". The
    /// single place this rule is spelled, so no caller's own concatenation can drift from it.
    /// </summary>
    public static string GenericInstance(string baseName, IReadOnlyList<string> args)
    {
        var sb = new System.Text.StringBuilder(baseName);
        foreach (var a in args) sb.Append('_').Append(a);
        string mangled = sb.ToString();
        _names.Composed[mangled] = new GenericKey(baseName, [.. args]);
        return mangled;
    }

    /// <summary>
    /// Records that this instantiation was stamped, so diagnostics can tell an instance the build
    /// produced from a spelling that merely names one.
    /// </summary>
    public static void RegisterGenericInstance(string mangled)
    {
        if (_names.Composed.TryGetValue(mangled, out var key)) _names.AddStamped(mangled, key);
    }

    /// <summary>
    /// Returns the base name and type arguments of a stamped generic instance, such as
    /// Map_int_String, which yields ("Map", ["int", "String"]). Structural consumers
    /// (generic-function type inference) use this instead of re-splitting the mangled string.
    /// </summary>
    public static bool TryGetGenericInstance(string mangled, out string baseName, out ImmutableArray<string> args)
    {
        if (_names.Stamped.TryGetValue(mangled, out var info))
        {
            (baseName, args) = info;
            return true;
        }
        baseName = "";
        args = [];
        return false;
    }

    /// <summary>
    /// Records that a generic template with this base name was declared.
    /// </summary>
    public static void RegisterGenericTemplate(string baseName) => _names.Templates.Add(baseName);

    /// <summary>
    /// Splits a mangled instance name back into the template it instantiates and its arguments, for
    /// a name that reached a pass already flattened. The split is the key filed when the name was
    /// composed, so a base or an argument containing an underscore costs nothing.
    /// </summary>
    public static bool TrySplitInstance(string mangled, out string baseName, out ImmutableArray<string> args)
    {
        if (_names.Composed.TryGetValue(mangled, out var key) && _names.Templates.Contains(key.Base))
        {
            (baseName, args) = key;
            return true;
        }
        baseName = ""; args = [];
        return false;
    }

    /// <summary>
    /// Returns true if a generic template with this base name was declared.
    /// </summary>
    public static bool IsGenericTemplate(string baseName) => _names.Templates.Contains(baseName);

    /// <summary>
    /// Every stamped instantiation of a generic base name, ordinally sorted - which instance
    /// 'Maybe.Found(7)' means once the template is gone.
    /// </summary>
    public static IReadOnlyList<string> InstancesOf(string baseName) =>
        _names.StampedByBase.GetValueOrDefault(baseName) ?? [];

    /// <summary>
    /// What a scope-qualified name was declared as, or null when nothing scoped declares it.
    /// </summary>
    public static string? ScopedKind(string qualified) => _names.Scopes?.KindOf(qualified);

    /// <summary>
    /// The readable paths of every scope declaring this bare name, ordinally sorted. Empty when
    /// nothing scoped declares it, which is the ordinary case.
    /// </summary>
    public static List<string> ScopedCandidates(string bare) => _names.Scopes?.Candidates(bare) ?? [];

    /// <summary>
    /// The readable, fully-qualified form of a scoped declaration name.
    /// </summary>
    private static string Unqualified(string name) =>
        _names.Scopes is { } t && t.TryUnqualify(name, out var qn) ? t.Display(qn.Scope, qn.Name) : name;

    /// <summary>
    /// True when a scope declares this exact qualified name, as opposed to it merely containing one.
    /// </summary>
    private static bool IsScoped(string name) => _names.Scopes?.TryUnqualify(name, out _) == true;

    /// <summary>
    /// Returns the user readable display name for a type, expanding generic instantiations
    /// recursively, eg. List_int becomes List[int], and unqualifying scoped names.
    /// </summary>
    public static string DisplayName(string name)
    {
        if (!TryStructure(name, out _)) return Unqualified(name);
        var sb = new System.Text.StringBuilder();
        AppendDisplayName(sb, name);
        return sb.ToString();
    }

    /// <summary>
    /// The instantiation a flat name denotes: what the build stamped, or failing that whatever
    /// composed the spelling, which is how an instantiation that was never stamped still reads as
    /// 'Box[int]' rather than as its internal name.
    /// </summary>
    private static bool TryStructure(string name, out GenericKey key)
    {
        if (_names.Stamped.TryGetValue(name, out key)) return true;
        key = default;
        return !IsScoped(name) && _names.Composed.TryGetValue(name, out key);
    }

    /// <summary>
    /// Recursively appends the user-readable display name for a type to the given StringBuilder.
    /// </summary>
    private static void AppendDisplayName(System.Text.StringBuilder sb, string name)
    {
        if (TryStructure(name, out var key))
        {
            sb.Append(Unqualified(key.Base)).Append('[');
            for (int i = 0; i < key.Args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendDisplayName(sb, key.Args[i]);
            }
            sb.Append(']');
        }
        else
        {
            sb.Append(Unqualified(name));
        }
    }

    /// <summary>
    /// Turns a scope-qualified Gata name into a C-safe fragment.
    /// </summary>
    public static string Sanitize(string name)
    {
        if (_names.Scopes is { } tree && tree.TryUnqualify(name, out var qn)) return qn.Name + tree.Token(qn.Scope);
        int at = name.IndexOf('@');
        return at < 0 ? name : string.Concat(name.AsSpan(0, at), "_s", Hash(name.AsSpan(at)));
    }

    /// <summary>
    /// Returns the C struct typedef name for a Gata class, using the dense token if available.
    /// </summary>
    public static string Class(string name)
    {
        return _names.Dense.GetValueOrDefault(name, $"gata_{Sanitize(name)}");
    }

    /// <summary>
    /// Returns the C allocator function name for a class, using the dense token if available.
    /// </summary>
    public static string Allocator(string cls)
    {
        return _names.Dense.TryGetValue(cls, out var d) ? d + "_n" : $"new_{Sanitize(cls)}";
    }

    /// <summary>
    /// Returns the C destructor function name for a class, using the dense token if available.
    /// </summary>
    public static string Dtor(string cls)
    {
        return _names.Dense.TryGetValue(cls, out var d) ? d + "_d" : $"gata_{Sanitize(cls)}__dtor";
    }

    /// <summary>
    /// Returns the C thread entry function name for a fully-qualified thread path.
    /// </summary>
    public static string ThreadEntry(string full)
    {
        return $"gata_{full}_main";
    }

    /// <summary>
    /// Returns the C name of the static holding a process variable, given the process's fully
    /// qualified name and the variable's written name.
    /// </summary>
    public static string ProcessVar(string procFull, string name)
    {
        return $"gata_{Sanitize(procFull)}_state_{Sanitize(name)}";
    }

    /// <summary>
    /// Returns the C name of the generated function that assigns a process's variables their
    /// initial values. External linkage: the launcher lives in its own translation unit.
    /// </summary>
    public static string ProcessStateInit(string procFull)
    {
        return $"gata_{Sanitize(procFull)}_state_init";
    }

    /// <summary>
    /// Returns the C typedef name for a Gata enum type.
    /// </summary>
    public static string Enum(string name)
    {
        return $"gata_{Sanitize(name)}";
    }

    /// <summary>
    /// Returns the C enumerator name for a member of a Gata enum type.
    /// </summary>
    public static string EnumMember(string enumName, string member)
    {
        return $"gata_{Sanitize(enumName)}_{member}";
    }

    /// <summary>
    /// Returns the C typedef name for a Gata union type.
    /// </summary>
    public static string Union(string name)
    {
        return $"gata_{Sanitize(name)}";
    }

    /// <summary>
    /// Returns the C tag enumerator name for a variant of a Gata union type.
    /// </summary>
    public static string UnionTag(string unionName, string variant)
    {
        return $"gata_{Sanitize(unionName)}_{variant}";
    }

    /// <summary>
    /// The C name of a managed union's generated retain, which switches on the tag and returns the
    /// union unchanged so it composes like the runtime intrinsic. Not densified, since unions keep
    /// their readable typedef name and one type must be spelled one way.
    /// </summary>
    public static string UnionRetain(string name)
    {
        return $"gata_{Sanitize(name)}__retain";
    }

    /// <summary>
    /// Returns the C function name for a managed union's generated release, which switches on the
    /// tag and releases whatever the live variant holds.
    /// </summary>
    public static string UnionRelease(string name)
    {
        return $"gata_{Sanitize(name)}__release";
    }

    /// <summary>
    /// Returns the C function name for a union's generated structural equality, which compares tags
    /// first and then the live variant's fields.
    /// </summary>
    public static string UnionEq(string name)
    {
        return $"gata_{Sanitize(name)}__eq";
    }

    /// <summary>
    /// Returns the C function name for a method, appending the overload suffix when overloaded.
    /// </summary>
    public static string Method(string owner, string name, IReadOnlyList<Param> ps, bool overloaded)
    {
        return $"gata_{Sanitize(owner)}_{name}" + (overloaded ? "_" + OverloadSuffix(ps) : "");
    }

    /// <summary>
    /// Returns the C function name for a free function. Entry functions use the kernel entry
    /// constant; extern functions use their bare C name; all others get the gata_ prefix.
    /// </summary>
    public static string FreeFunc(string name, IReadOnlyList<Param> ps, bool overloaded, bool isEntry, bool isExtern)
    {
        if (isEntry)  return KernelEntry;
        if (isExtern) return name;
        string b = name.StartsWith("gata_") ? name : $"gata_{Sanitize(name)}";
        return b + (overloaded ? "_" + OverloadSuffix(ps) : "");
    }

    /// <summary>
    /// Returns the C function name for a file-local private free function, prefixed by a stable
    /// per-file token so two files may reuse the same name without clashing.
    /// </summary>
    public static string PrivateFreeFunc(string fileToken, string name, IReadOnlyList<Param> ps, bool overloaded)
    {
        return $"gata_f{fileToken}_{Sanitize(name)}" + (overloaded ? "_" + OverloadSuffix(ps) : "");
    }

    /// <summary>
    /// Returns a stable 8-hex C-identifier fragment derived from the declaring file path via a
    /// 32-bit FNV-1a hash, used to namespace file-local function names.
    /// </summary>
    public static string FileToken(string file)
    {
        return Hash(file);
    }

    /// <summary>
    /// A stable 8-hex C-identifier fragment derived from a string via 32-bit FNV-1a. Stable across
    /// builds and machines, which matters because it ends up in emitted C.
    /// </summary>
    internal static string Hash(ReadOnlySpan<char> s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h.ToString("x8");
    }

    /// <summary>
    /// The C name for an operator overload. 'overloaded' appends a disambiguating suffix - only
    /// 'as' can have more than one per class today, distinguished by parameter type as every other
    /// parameterized overload already is.
    /// </summary>
    public static string Operator(string owner, string op, IReadOnlyList<Param> ps, bool overloaded)
    {
        string bare = $"gata_{Sanitize(owner)}_{OpSuffix(op)}";
        if (!overloaded) return bare;
        string suffix = ps.Count > 0 ? OverloadSuffix(ps) : "unary";
        return $"{bare}_{suffix}";
    }

    /// <summary>
    /// Returns the stable C identifier suffix for a Gata operator token.
    /// </summary>
    public static string OpSuffix(string op)
    {
        return op switch
        {
            "+" => "add",
            "-" => "sub",
            "*" => "mul",
            "/" => "div",
            "%" => "mod",
            "==" => "eq",
            "!=" => "neq",
            "<" => "lt",
            ">" => "gt",
            "<=" => "lte",
            ">=" => "gte",
            "&" => "band",
            "|" => "bor",
            "^" => "bxor",
            "<<" => "shl",
            ">>" => "shr",
            "[]" => "index_get",
            "[]=" => "index_set",
            "!" => "not",
            "~" => "bnot",
            "++" => "inc",
            "--" => "dec",
            _ => "op"
        };
    }

    /// <summary>
    /// Returns the overload suffix that distinguishes parameter-type combinations, encoding each
    /// parameter's mangled type name joined by underscores.
    /// </summary>
    public static string OverloadSuffix(IReadOnlyList<Param> ps)
    {
        if (ps.Count == 0) return "void";
        if (ps.Count == 1) return MangleTypeName(ps[0].Type.ToSpecString());

        var sb = new System.Text.StringBuilder();
        sb.Append(MangleTypeName(ps[0].Type.ToSpecString()));
        for (int i = 1; i < ps.Count; i++)
        {
            sb.Append('_');
            sb.Append(MangleTypeName(ps[i].Type.ToSpecString()));
        }
        return sb.ToString();
    }

    private static readonly System.Buffers.SearchValues<char> IdentChars =
        System.Buffers.SearchValues.Create(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    /// <summary>
    /// Converts a Gata type name to a C-identifier fragment. Every non-identifier character becomes
    /// a separating underscore (collapsed to prevent runs); pointer stars become _p markers so
    /// distinct pointer types never collapse to the same suffix.
    /// </summary>
    internal static string MangleTypeName(string t)
    {
        ReadOnlySpan<char> span = t.AsSpan().Trim();
        if (span.IsEmpty) return "x";

        int maxLen = span.Length * 2;
        char[]? rented = null;
        Span<char> dest = maxLen <= 256
            ? stackalloc char[256]
            : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(maxLen));

        try
        {
            int destIdx = 0;
            bool lastWasSep = false;

            while (!span.IsEmpty)
            {
                int at = span.IndexOfAnyExcept(IdentChars);
                int run = at < 0 ? span.Length : at;
                if (run > 0)
                {
                    span[..run].CopyTo(dest[destIdx..]);
                    destIdx += run;
                    lastWasSep = false;
                    span = span[run..];
                    if (span.IsEmpty) break;
                }

                if (span[0] == '*')
                {
                    dest[destIdx++] = '_';
                    dest[destIdx++] = 'p';
                    lastWasSep = false;
                }
                else if (!lastWasSep)
                {
                    dest[destIdx++] = '_';
                    lastWasSep = true;
                }
                span = span[1..];
            }

            while (destIdx > 0 && dest[destIdx - 1] == '_')
            {
                destIdx--;
            }

            int startIdx = 0;
            while (startIdx < destIdx && dest[startIdx] == '_')
            {
                startIdx++;
            }

            int finalLen = destIdx - startIdx;
            return finalLen <= 0 ? "x" : new string(dest.Slice(startIdx, finalLen));
        }
        finally
        {
            if (rented != null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
