namespace Appa;

/// <summary>
/// The single authority for C identifiers. Every emitted name is produced here from
/// a symbol's identity; no other pass spells a gata_ name. Definition and call
/// sites read the same Symbol.CName assigned via this class once declarations are
/// collected, so a definition and its callers can never disagree on a name.
/// </summary>
internal static class Mangler
{
    public const string KernelEntry = "gata_kernelspace_main";

    // C keywords and the handful of standard macros that behave like them. None of these
    // is a Gata keyword, so all of them are ordinary identifiers a Gata program may use -
    // and locals and parameters are the only names emitted verbatim, so they are the only
    // names that can collide. Everything else already carries a gata_ prefix.
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
    /// Returns the C spelling of a local variable or parameter name.
    ///
    /// Locals and parameters are the one category of name the emitter prints as written -
    /// keeping them readable is the point, since they are what a person reads the generated
    /// C for. That makes them the one category that can collide with C's own vocabulary:
    /// 'struct', 'register' and 'signed' are all perfectly good Gata identifiers, and each
    /// one used to produce C that would not parse. A trailing underscore is appended in
    /// exactly those cases, which C guarantees is never itself reserved.
    ///
    /// Must be applied at every site that prints such a name - the declaration, the
    /// parameter list, and each reference - so the three can never disagree.
    /// </summary>
    public static string Local(string name)
    {
        return CReserved.Contains(name) ? name + "_" : name;
    }

    // Prefixes the compiler itself generates local names from: Ownership's hoisting temps,
    // Desugar's switch and match scrutinee temps, the throws lowering's Result temps and
    // labels, the constructor's self parameter, and the Densifier's dense function tokens.
    // Each is followed by a sequence number, except the fixed names checked separately.
    private static readonly string[] GeneratedPrefixes =
    [
        "_g", "_a", "_arr", "_asg", "_ci", "_col", "_e", "_fc", "_fi", "_first", "_if",
        "_ixi", "_ixo", "_mt", "_res_", "_ret", "_sw", "_tern", "_wh", "_catch_", "_end_",
    ];

    /// <summary>
    /// Returns true if a user-written local or parameter name would collide with a name the
    /// compiler generates for its own temporaries.
    ///
    /// Renaming the user's version is not an option here: the generated names are emitted
    /// through the same path, so any rule that moves one moves the other and the collision
    /// survives. Rejecting the name instead is unambiguous and costs the user a rename of an
    /// identifier they had no reason to pick. Ordinary leading-underscore names such as
    /// '_unused' are unaffected - only an exact generated shape matches.
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
    }

    /// <summary>
    /// Records the base name and type arguments for a generic instantiation so
    /// diagnostics can display it in user-readable form.
    /// </summary>
    public static void RegisterGenericInstance(string mangled, string baseName, List<string> args)
    {
        _genericInfo[mangled] = (baseName, args);
    }

    /// <summary>
    /// Returns the registered base name and type arguments for a mangled generic instance
    /// name, such as Map_int_String, which yields ("Map", ["int", "String"]). Structural consumers
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

    /// <summary>
    /// Returns the user-readable display name for a type, expanding generic
    /// instantiations recursively, e.g. List_int becomes List[int].
    /// </summary>
    public static string DisplayName(string name)
    {
        if (!_genericInfo.TryGetValue(name, out var info))
        {
            return name;
        }
        var sb = new System.Text.StringBuilder();
        AppendDisplayName(sb, name);
        return sb.ToString();
    }

    /// <summary>
    /// Recursively appends the user-readable display name for a type to the given StringBuilder.
    /// </summary>
    private static void AppendDisplayName(System.Text.StringBuilder sb, string name)
    {
        if (_genericInfo.TryGetValue(name, out var info))
        {
            sb.Append(info.Base).Append('[');
            for (int i = 0; i < info.Args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                AppendDisplayName(sb, info.Args[i]);
            }
            sb.Append(']');
        }
        else
        {
            sb.Append(name);
        }
    }

    /// <summary>
    /// Returns the C struct typedef name for a Gata class, using the dense token if available.
    /// </summary>
    public static string Class(string name)
    {
        return _dense.GetValueOrDefault(name, $"gata_{name}");
    }

    /// <summary>
    /// Returns the C allocator function name for a class, using the dense token if available.
    /// </summary>
    public static string Allocator(string cls)
    {
        return _dense.TryGetValue(cls, out var d) ? d + "_n" : $"new_{cls}";
    }

    /// <summary>
    /// Returns the C destructor function name for a class, using the dense token if available.
    /// </summary>
    public static string Dtor(string cls)
    {
        return _dense.TryGetValue(cls, out var d) ? d + "_d" : $"gata_{cls}__dtor";
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
        return $"gata_{name}";
    }

    /// <summary>
    /// Returns the C enumerator name for a member of a Gata enum type.
    /// </summary>
    public static string EnumMember(string enumName, string member)
    {
        return $"gata_{enumName}_{member}";
    }

    /// <summary>
    /// Returns the C typedef name for a Gata union type.
    /// </summary>
    public static string Union(string name)
    {
        return $"gata_{name}";
    }

    /// <summary>
    /// Returns the C tag enumerator name for a variant of a Gata union type.
    /// </summary>
    public static string UnionTag(string unionName, string variant)
    {
        return $"gata_{unionName}_{variant}";
    }

    /// <summary>
    /// Returns the C function name for a method, appending the overload suffix when overloaded.
    /// </summary>
    public static string Method(string owner, string name, IReadOnlyList<Param> ps, bool overloaded)
    {
        return $"gata_{owner}_{name}" + (overloaded ? "_" + OverloadSuffix(ps) : "");
    }

    /// <summary>
    /// Returns the C function name for a free function. Entry functions use the kernel entry
    /// constant; extern functions use their bare C name; all others get the gata_ prefix.
    /// </summary>
    public static string FreeFunc(string name, IReadOnlyList<Param> ps, bool overloaded, bool isEntry, bool isExtern)
    {
        if (isEntry)  return KernelEntry;
        if (isExtern) return name;
        string b = name.StartsWith("gata_") ? name : $"gata_{name}";
        return b + (overloaded ? "_" + OverloadSuffix(ps) : "");
    }

    /// <summary>
    /// Returns the C function name for a file-local private free function, prefixed by a
    /// stable per-file token so two files may reuse the same name without clashing.
    /// </summary>
    public static string PrivateFreeFunc(string fileToken, string name, IReadOnlyList<Param> ps, bool overloaded)
    {
        return $"gata_f{fileToken}_{name}" + (overloaded ? "_" + OverloadSuffix(ps) : "");
    }

    /// <summary>
    /// Returns a stable 8-hex C-identifier fragment derived from the declaring file path
    /// via a 32-bit FNV-1a hash, used to namespace file-local function names.
    /// </summary>
    public static string FileToken(string file)
    {
        uint h = 2166136261;
        foreach (char c in file) { h ^= c; h *= 16777619; }
        return h.ToString("x8");
    }

    /// <summary>
    /// Returns the C operator function name for an operator overload on the given class.
    /// 'overloaded' appends a disambiguating suffix - only 'as' can have more than one overload
    /// per class today, distinguished by its parameter type the same way every other
    /// parameterized operator or method overload already is.
    /// </summary>
    public static string Operator(string owner, string op, IReadOnlyList<Param> ps, bool overloaded)
    {
        string bare = $"gata_{owner}_{OpSuffix(op)}";
        if (!overloaded) return bare;
        
        // Note here: A zero param overload is the unary form of a symbol that also has a binary form. 
        // "unary" keeps it distinct from the binary overload's param type suffix.
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
    /// Returns the overload suffix that distinguishes parameter-type combinations,
    /// encoding each parameter's mangled type name joined by underscores.
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
    /// Converts a Gata type name to a C-identifier fragment. Every non-identifier
    /// character becomes a separating underscore (collapsed to prevent runs); pointer
    /// stars become _p markers so distinct pointer types never collapse to the same suffix.
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
