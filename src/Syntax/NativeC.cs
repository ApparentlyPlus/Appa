namespace Appa;

using System.Text.RegularExpressions;

// Helpers for the raw C inside `native { }` blocks. The `#kernel:` / `#user:`
// section markers and the struct scan must see only real code — a marker or a
// `struct gata_X {` sitting inside a C comment or string literal must NOT be
// matched. We do this by computing a same-length "masked" copy of the text with
// every comment and string/char-literal body blanked to spaces, locating things
// in the masked copy, then slicing the ORIGINAL text at the same offsets.
static partial class NativeC
{
    // Same-length copy with C comments and string/char literals blanked to spaces
    // (newlines preserved). Real code is left untouched.
    public static string Mask(string s)
    {
        var a = s.ToCharArray();
        int n = a.Length;
        for (int i = 0; i < n; i++)
        {
            char c = a[i];
            char d = i + 1 < n ? a[i + 1] : '\0';
            if (c == '/' && d == '/')                       // line comment
            {
                while (i < n && a[i] != '\n') { a[i] = ' '; i++; }
                i--;
            }
            else if (c == '/' && d == '*')                  // block comment
            {
                a[i] = ' '; a[i + 1] = ' '; i += 2;
                while (i < n && !(a[i] == '*' && i + 1 < n && a[i + 1] == '/'))
                { if (a[i] != '\n') a[i] = ' '; i++; }
                if (i < n) a[i] = ' ';
                if (i + 1 < n) a[i + 1] = ' ';
                i++;
            }
            else if (c == '"' || c == '\'')                 // string / char literal
            {
                char q = c; a[i] = ' '; i++;
                while (i < n && a[i] != q)
                {
                    if (a[i] == '\\') { a[i] = ' '; if (i + 1 < n) a[i + 1] = ' '; i += 2; continue; }
                    if (a[i] != '\n') a[i] = ' ';
                    i++;
                }
                if (i < n) a[i] = ' ';
            }
        }
        return new string(a);
    }

    // Split a native body into (kernelC, userC) on #kernel:/#user: markers found in
    // real code only. With no markers, both variants are the whole body.
    public static (string KernelC, string UserC) Split(string raw)
    {
        string masked = Mask(raw);
        int ki = masked.IndexOf("#kernel:", StringComparison.Ordinal);
        int ui = masked.IndexOf("#user:",   StringComparison.Ordinal);
        if (ki < 0 && ui < 0) return (raw, raw);
        string kc = "", uc = "";
        if (ki >= 0) { int s = ki + 8; int e = ui >= 0 && ui > ki ? ui : raw.Length; kc = raw[s..e].Trim(); }
        if (ui >= 0) { int s = ui + 6; int e = ki >= 0 && ki > ui ? ki : raw.Length; uc = raw[s..e].Trim(); }
        if (kc == "") kc = uc;
        if (uc == "") uc = kc;
        return (kc, uc);
    }

    // Struct/typedef names declared in a native body (for the opaque-struct registry),
    // scanned over masked text so names in comments/strings are ignored.
    public static IEnumerable<string> ScanStructs(string raw)
    {
        foreach (Match m in TDefRegex().Matches(Mask(raw)))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!string.IsNullOrEmpty(name)) yield return name;
        }
    }

    [GeneratedRegex(@"GATA_(\w+)_DEFINED|struct gata_(\w+)\s*\{")]
    private static partial Regex TDefRegex();

}
