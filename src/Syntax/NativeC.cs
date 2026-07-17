namespace Appa;

using System.Text.RegularExpressions;

internal static partial class NativeC
{
    /// <summary>
    /// Same-length copy with C comments and string/char literals blanked to spaces
    /// (newlines preserved). Real code is left untouched.
    /// </summary>
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

    /// <summary>
    /// Struct/typedef names declared in a native body (for the opaque-struct registry),
    /// scanned over masked text so names in comments/strings are ignored.
    /// </summary>
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
