namespace Appa;

internal static class Mangler
{
    public const string KernelEntry = "gata_kernelspace_main";

    // C keywords and the standard macros that behave like them. None is a Gata keyword, so a
    // program may use them all - and locals and parameters, the only names emitted verbatim, are
    // therefore the only ones that can collide.
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
        ], StringComparer.Ordinal);

    /// <summary>
    /// Returns the C spelling of a local or parameter name. These are the only names printed as
    /// written and so the only ones that can collide with C's vocabulary; those get a trailing
    /// underscore. Apply at every site that prints the name.
    /// </summary>
    public static string Local(string name)
    {
        return CReserved.Contains(name) ? name + "_" : name;
    }

    // Prefixes the compiler generates local names from: hoisting temps, scrutinee temps, Result
    // temps and labels, the constructor's self parameter, dense function tokens. Each is followed
    // by a sequence number, except the fixed names checked separately.
    private static readonly string[] GeneratedPrefixes =
    [
        "_g", "_a", "_arr", "_asg", "_ci", "_col", "_e", "_fc", "_fi", "_first", "_if",
        "_ixi", "_ixo", "_mt", "_res_", "_ret", "_sw", "_tern", "_wh", "_catch_", "_end_",
    ];

    /// <summary>
    /// True if a user-written local would collide with a compiler temporary. Renaming the user's is
    /// not an option - both go through this path, so any rule that moves one moves the other. Only
    /// an exact generated shape matches, so '_unused' is fine.
    /// </summary>
    public static bool IsReservedLocal(string name)
    {
        if (name is "_has_error" or "_o") return true;
        foreach (var prefix in GeneratedPrefixes)
        {
            if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            bool allDigits = true;
            for (int i = prefix.Length; i < name.Length; i++)
                if (!char.IsAsciiLetterOrDigit(name[i]) || char.IsAsciiLetterUpper(name[i])) { allDigits = false; break; }
            if (allDigits) return true;
        }
        return false;
    }

    // Step 7 dense naming. When populated by the Densifier after reachability, a
    // class's readable C name collapses to a short machine token. Empty during resolution.
    [ThreadStatic] private static Dictionary<string, string>? _denseTls;
    private static Dictionary<string, string> _dense => _denseTls ??= [];

    // Every generic instantiation the Monomorphizer stamps is recorded here so
    // diagnostics can show the user-written form instead of the mangled name.
    [ThreadStatic] private static Dictionary<string, (string Base, List<string> Args)>? _genericInfoTls;
    private static Dictionary<string, (string Base, List<string> Args)> _genericInfo => _genericInfoTls ??= [];

    /// <summary>
    /// Replaces the dense name map with the given mapping produced by the Densifier.
    /// </summary>
    public static void SetDense(Dictionary<string, string> map)
    {
        _denseTls = map;
    }

    /// <summary>
    /// Clears the dense name map, restoring readable names for the next build.
    /// </summary>
    public static void ResetDense()
    {
        _denseTls = [];
    }

    /// <summary>
    /// Clears the generic instance display registry for the next build.
    /// </summary>
    public static void ResetGenericDisplay()
    {
        _genericInfo.Clear();
        _genericTemplates.Clear();
    }

    /// <summary>
    /// Composes the internal name of a generic instantiation: ("List", ["int"]) is "List_int". The
    /// single place this rule is spelled, so no caller's own concatenation can drift from it.
    /// </summary>
    public static string GenericInstance(string baseName, IEnumerable<string> args)
    {
        var sb = new System.Text.StringBuilder(baseName);
        foreach (var a in args) sb.Append('_').Append(a);
        return sb.ToString();
    }

    /// <summary>
    /// Records the base name and type arguments for a generic instantiation so diagnostics can
    /// display it in user-readable form.
    /// </summary>
    public static void RegisterGenericInstance(string mangled, string baseName, List<string> args)
    {
        _genericInfo[mangled] = (baseName, args);
    }

    /// <summary>
    /// Returns the registered base name and type arguments for a mangled generic instance name,
    /// such as Map_int_String, which yields ("Map", ["int", "String"]). Structural consumers
    /// (generic-function type inference) use this instead of re-splitting the mangled string.
    /// </summary>
    public static bool TryGetGenericInstance(string mangled, out string baseName, out List<string> args)
    {
        if (_genericInfo.TryGetValue(mangled, out var info))
        {
            (baseName, args) = info;
            return true;
        }
        baseName = "";
        args = [];
        return false;
    }

    // Base names of every generic template seen this build, whether or not anything
    // instantiated them. Kept for diagnostics: a template nothing names as a type is replaced
    // by nothing, so without this the base name looks like an undefined identifier.
    [ThreadStatic] private static HashSet<string>? _genericTemplatesTls;
    private static HashSet<string> _genericTemplates => _genericTemplatesTls ??= [];

    /// <summary>
    /// Records that a generic template with this base name was declared.
    /// </summary>
    public static void RegisterGenericTemplate(string baseName)
    {
        _genericTemplates.Add(baseName);
    }

    /// <summary>
    /// Returns true if a generic template with this base name was declared.
    /// </summary>
    public static bool IsGenericTemplate(string baseName) => _genericTemplates.Contains(baseName);

    /// <summary>
    /// Every registered instantiation of a generic base name, mangled and ordinally sorted - which
    /// stamped instance 'Maybe.Found(7)' means once the template is gone. Sorted because these
    /// reach the user, and dictionary order is not stable.
    /// </summary>
    public static List<string> InstancesOf(string baseName)
    {
        var found = new List<string>();
        foreach (var (mangled, info) in _genericInfo)
            if (info.Base == baseName) found.Add(mangled);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    // Readable form of every scope-qualified name this build produced: 'Config@kernel$P1' maps to
    // 'kernel.P1.Config'. Without this every diagnostic naming a scoped type would print the raw
    // internal spelling, which is both unreadable and a lie about what the user wrote.
    [ThreadStatic] private static Dictionary<string, string>? _scopeDisplayTls;
    private static Dictionary<string, string> _scopeDisplay => _scopeDisplayTls ??= [];

    // Bare name to the readable paths of every scope declaring it. Lets a diagnostic say where a
    // name it could not resolve does exist, instead of insisting it does not exist at all.
    [ThreadStatic] private static Dictionary<string, List<string>>? _scopedByBareTls;
    private static Dictionary<string, List<string>> _scopedByBare => _scopedByBareTls ??= [];

    // What each scope qualified name was declared as: "a type", "a function", "a generic type". A
    // scope holds one meaning per name, so a use in the wrong position can be told what it found
    // instead of being answered with "unknown type".
    [ThreadStatic] private static Dictionary<string, string>? _scopeKindTls;
    private static Dictionary<string, string> _scopeKind => _scopeKindTls ??= [];

    /// <summary>
    /// Records what kind of declaration a scope-qualified name refers to.
    /// </summary>
    public static void RegisterScopedKind(string qualified, string kind) => _scopeKind[qualified] = kind;

    /// <summary>
    /// What a scope-qualified name was declared as, or null when nothing scoped declares it.
    /// </summary>
    public static string? ScopedKind(string qualified) => _scopeKind.GetValueOrDefault(qualified);

    /// <summary>
    /// Records the readable, fully-qualified form of a scope-qualified name.
    /// </summary>
    public static void RegisterScopedName(string qualified, string display)
    {
        _scopeDisplay[qualified] = display;

        int dot = display.LastIndexOf('.');
        if (dot < 0) return;
        string bare = display[(dot + 1)..];
        if (!_scopedByBare.TryGetValue(bare, out var paths)) _scopedByBare[bare] = paths = [];
        if (!paths.Contains(display)) paths.Add(display);
    }

    /// <summary>
    /// The readable paths of every scope declaring this bare name, ordinally sorted. Empty when
    /// nothing scoped declares it, which is the ordinary case.
    /// </summary>
    public static List<string> ScopedCandidates(string bare)
    {
        if (!_scopedByBare.TryGetValue(bare, out var paths)) return [];
        var copy = new List<string>(paths);
        copy.Sort(StringComparer.Ordinal);
        return copy;
    }

    /// <summary>
    /// Clears the scoped-name display registry for the next build. Called beside the other resets;
    /// leaving it populated leaks names between builds, which in the in-process test harness means
    /// leaking them between tests.
    /// </summary>
    public static void ResetScopeDisplay()
    {
        _scopeDisplayTls = [];
        _scopedByBareTls = [];
        _scopeKindTls = [];
    }

    /// <summary>
    /// Returns the user readable display name for a type, expanding generic instantiations
    /// recursively, eg. List_int becomes List[int], and unqualifying scoped names.
    /// </summary>
    public static string DisplayName(string name)
    {
        if (!_genericInfo.ContainsKey(name))
        {
            if (_scopeDisplay.TryGetValue(name, out var plain)) return plain;
            return name.Contains('@') ? UnstampedScoped(name) : name;
        }
        var sb = new System.Text.StringBuilder();
        AppendDisplayName(sb, name);
        return sb.ToString();
    }

    /// <summary>
    /// The readable form of a scope-qualified instantiation.
    /// 'Box[int]' over a scoped 'Box' that is not generic. The longest registered prefix is
    /// the base, and what follows it the arguments, as GenericInstance wrote them.
    /// </summary>
    private static string UnstampedScoped(string name)
    {
        for (int cut = name.LastIndexOf('_'); cut > 0; cut = name.LastIndexOf('_', cut - 1))
        {
            if (!_scopeDisplay.TryGetValue(name[..cut], out var head)) continue;
            var args = name[(cut + 1)..].Split('_');
            for (int i = 0; i < args.Length; i++) args[i] = DisplayName(args[i]);
            return $"{head}[{string.Join(", ", args)}]";
        }
        return name;
    }

    /// <summary>
    /// Recursively appends the user-readable display name for a type to the given StringBuilder.
    /// </summary>
    private static void AppendDisplayName(System.Text.StringBuilder sb, string name)
    {
        if (_genericInfo.TryGetValue(name, out var info))
        {
            sb.Append(_scopeDisplay.GetValueOrDefault(info.Base, info.Base)).Append('[');
            for (int i = 0; i < info.Args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendDisplayName(sb, info.Args[i]);
            }
            sb.Append(']');
        }
        else
        {
            // A scoped type reached as a generic argument lands here, so the unqualification has to
            // happen on this branch too: List_Config@kernel$P1 must read List[kernel.P1.Config].
            sb.Append(_scopeDisplay.GetValueOrDefault(name, name));
        }
    }

    /// <summary>
    /// Turns a scope-qualified Gata name into a C-safe fragment: 'Config@kernel$P' becomes
    /// 'Config_s3f2a71c9'. The token hashes the suffix, so depth costs no length; an unqualified
    /// name passes through, so a program declaring nothing in a scope emits identical C.
    /// </summary>
    public static string Sanitize(string name)
    {
        int at = name.IndexOf('@');
        return at < 0 ? name : string.Concat(name.AsSpan(0, at), "_s", Hash(name.AsSpan(at)));
    }

    /// <summary>
    /// Returns the C struct typedef name for a Gata class, using the dense token if available.
    /// </summary>
    public static string Class(string name)
    {
        return _dense.GetValueOrDefault(name, $"gata_{Sanitize(name)}");
    }

    /// <summary>
    /// Returns the C allocator function name for a class, using the dense token if available.
    /// </summary>
    public static string Allocator(string cls)
    {
        return _dense.TryGetValue(cls, out var d) ? d + "_n" : $"new_{Sanitize(cls)}";
    }

    /// <summary>
    /// Returns the C destructor function name for a class, using the dense token if available.
    /// </summary>
    public static string Dtor(string cls)
    {
        return _dense.TryGetValue(cls, out var d) ? d + "_d" : $"gata_{Sanitize(cls)}__dtor";
    }

    /// <summary>
    /// Returns the C thread entry function name for a fully-qualified thread path.
    /// </summary>
    public static string ThreadEntry(string full)
    {
        return $"gata_{full}_main";
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
    private static string Hash(ReadOnlySpan<char> s)
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

            foreach (char ch in span)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_')
                {
                    dest[destIdx++] = ch;
                    lastWasSep = false;
                }
                else if (ch == '*')
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
