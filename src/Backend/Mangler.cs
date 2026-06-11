namespace Appa;

/// <summary>
/// The single authority for C identifiers. Every emitted name is produced here from
/// a symbol's identity; no other pass spells a gata_ name. Definition and call
/// sites read the same Symbol.CName assigned via this class once declarations are
/// collected, so a definition and its callers can never disagree on a name.
/// </summary>
static class Mangler
{
    public const string KernelEntry = "gata_kernelspace_main";

    // Step 7 dense naming. When populated by the Densifier after reachability, a
    // class's readable C name collapses to a short machine token. Empty during
    // resolution. Reset per build.
    static Dictionary<string, string> _dense = [];

    // Every generic instantiation the Monomorphizer stamps is recorded here so
    // diagnostics can show the user-written form instead of the mangled name.
    // Reset per build; populated as each instantiation is processed.
    static readonly Dictionary<string, (string Base, List<string> Args)> _genericInfo = [];

    /// <summary>
    /// Replaces the dense name map with the given mapping produced by the Densifier.
    /// </summary>
    public static void SetDense(Dictionary<string, string> map) => _dense = map;

    /// <summary>
    /// Clears the dense name map, restoring readable names for the next build.
    /// </summary>
    public static void ResetDense() => _dense = [];

    /// <summary>
    /// Clears the generic instance display registry for the next build.
    /// </summary>
    public static void ResetGenericDisplay() => _genericInfo.Clear();

    /// <summary>
    /// Records the base name and type arguments for a generic instantiation so
    /// diagnostics can display it in user-readable form.
    /// </summary>
    public static void RegisterGenericInstance(string mangled, string baseName, List<string> args) =>
        _genericInfo[mangled] = (baseName, args);

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
    public static string Class(string name) => _dense.GetValueOrDefault(name, $"gata_{name}");

    /// <summary>
    /// Returns the C allocator function name for a class, using the dense token if available.
    /// </summary>
    public static string Allocator(string cls) => _dense.TryGetValue(cls, out var d) ? d + "_n" : $"new_{cls}";

    /// <summary>
    /// Returns the C destructor function name for a class, using the dense token if available.
    /// </summary>
    public static string Dtor(string cls) => _dense.TryGetValue(cls, out var d) ? d + "_d" : $"gata_{cls}__dtor";

    /// <summary>
    /// Returns the C thread entry function name for a fully-qualified thread path.
    /// </summary>
    public static string ThreadEntry(string full) => $"gata_{full}_main";

    /// <summary>
    /// Returns the C typedef name for a Gata enum type.
    /// </summary>
    public static string Enum(string name) => $"gata_{name}";

    /// <summary>
    /// Returns the C enumerator name for a member of a Gata enum type.
    /// </summary>
    public static string EnumMember(string enumName, string member) => $"gata_{enumName}_{member}";

    /// <summary>
    /// Returns the C typedef name for a Gata union type.
    /// </summary>
    public static string Union(string name) => $"gata_{name}";

    /// <summary>
    /// Returns the C tag enumerator name for a variant of a Gata union type.
    /// </summary>
    public static string UnionTag(string unionName, string variant) => $"gata_{unionName}_{variant}";

    /// <summary>
    /// Returns the C function name for a method, appending the overload suffix when overloaded.
    /// </summary>
    public static string Method(string owner, string name, IReadOnlyList<Param> ps, bool overloaded) =>
        $"gata_{owner}_{name}" + (overloaded ? "_" + OverloadSuffix(ps) : "");

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
    public static string PrivateFreeFunc(string fileToken, string name, IReadOnlyList<Param> ps, bool overloaded) =>
        $"gata_f{fileToken}_{name}" + (overloaded ? "_" + OverloadSuffix(ps) : "");

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
    /// </summary>
    public static string Operator(string owner, string op) => $"gata_{owner}_{OpSuffix(op)}";

    /// <summary>
    /// Returns the stable C identifier suffix for a Gata operator token.
    /// </summary>
    public static string OpSuffix(string op) => op switch
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
        _ => "op"
    };

    /// <summary>
    /// Returns the overload suffix that distinguishes parameter-type combinations,
    /// encoding each parameter's mangled type name joined by underscores.
    /// </summary>
    public static string OverloadSuffix(IReadOnlyList<Param> ps)
    {
        if (ps.Count == 0) return "void";
        if (ps.Count == 1) return MangleTypeName(ps[0].Type);

        var sb = new System.Text.StringBuilder();
        sb.Append(MangleTypeName(ps[0].Type));
        for (int i = 1; i < ps.Count; i++)
        {
            sb.Append('_');
            sb.Append(MangleTypeName(ps[i].Type));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Converts a Gata type name to a C-identifier fragment. Every non-identifier
    /// character becomes a separating underscore (collapsed to prevent runs); pointer
    /// stars become _p markers so distinct pointer types never collapse to the same suffix.
    /// </summary>
    static string MangleTypeName(string t)
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
