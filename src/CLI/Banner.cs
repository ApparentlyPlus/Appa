namespace Appa;

using System.Text;

#region Banner

static class Banner
{
    private static readonly (int R, int G, int B) Start = (0xff, 0xd3, 0x5c), End = (0xfe, 0x7a, 0x4d);
    private static readonly int[] Ramp = [221, 215, 209];
    private const int Steps = 32;

    private static readonly string[] Logo =
    [
        "      🬭🬵🬹🬻████████🬹🬹🬭🬏                ",
        "   🬞🬹█████████████████🬺🬱              ",
        "  🬵█████████████████████🬺🬏            ",
        " 🬻██████🬎🬂🬂🬂🬂🬊🬬██████████🬺            ",
        "🬷█████🬝🬀       🬊██████████▌           ",
        "██████🬀         ███████████           ",
        "██████🬏         ▐██████████🬓          ",
        "🬨█████🬺🬏        🬉███████████🬏         ",
        " 🬬██████🬹🬭🬭🬭     🬨███████████🬺🬱🬭🬏     ",
        "  🬊█████████████🬱 🬊█████████████████🬺🬱",
        "   🬁🬎████████████  🬁🬊█████████████████",
        "      🬂🬊🬎🬎█████🬎🬀     🬁🬊🬎🬎██████████🬎🬀"
    ];

    private static readonly string[] AppaText =
    [
        "   ░███                                     ",
        "  ░██░██                                    ",
        " ░██  ░██  ░████████  ░████████   ░██████   ",
        "░█████████ ░██    ░██ ░██    ░██       ░██  ",
        "░██    ░██ ░██    ░██ ░██    ░██  ░███████  ",
        "░██    ░██ ░███   ░██ ░███   ░██ ░██   ░██  ",
        "░██    ░██ ░██░█████  ░██░█████   ░█████░██ ",
        "           ░██        ░██                   ",
        "           ░██        ░██                   ",
    ];

    private const int Gap = 4;
    private static readonly string[] Full = Lockup();
    private static readonly int FullWidth = Widest(Full), TextWidth = Widest(AppaText);

    /// <summary>
    /// Sets the wordmark beside the cat, vertically centred against it - with an odd number of rows
    /// left over the extra one goes above, which is what puts "Appa" on the cat's third row.
    /// </summary>
    private static string[] Lockup()
    {
        int logoWidth = Widest(Logo);
        int drop = (Logo.Length - AppaText.Length + 1) / 2;
        int rows = Math.Max(Logo.Length, drop + AppaText.Length);

        var lockup = new string[rows];
        for (int y = 0; y < rows; y++)
        {
            string left = y < Logo.Length ? PadTo(Logo[y].TrimEnd(), logoWidth) : new string(' ', logoWidth);
            string right = y >= drop && y - drop < AppaText.Length ? AppaText[y - drop] : "";
            lockup[y] = (left + new string(' ', Gap) + right).TrimEnd();
        }
        return lockup;
    }

    /// <summary>
    /// The widest row of an art block, ignoring the trailing padding that squares the literals off.
    /// </summary>
    private static int Widest(string[] block) => block.Max(row => Visible(row.TrimEnd()));

    /// <summary>
    /// Pads a row out to a column count - by what the terminal draws, not by String.Length.
    /// </summary>
    private static string PadTo(string row, int width) =>
        row + new string(' ', Math.Max(0, width - Visible(row)));

    /// <summary>
    /// Prints the banner.
    /// </summary>
    public static void Print(string indent = "")
    {
        var (w, h) = Viewport();
        w -= indent.Length;

        // cat + wordmark if it fits, just the wordmark if that fits, otherwise text only
        string[]? art = null;
        int width = 0;
        if (w >= FullWidth && h >= Full.Length + 6)
        {
            art = Full;
            width = FullWidth;
        }
        else if (w >= TextWidth)
        {
            art = AppaText;
            width = TextWidth;
        }

        Console.WriteLine();
        if (art != null)
        {
            for (int y = 0; y < art.Length; y++)
                Console.WriteLine(indent + Paint(art[y].TrimEnd(), (double)y / (art.Length - 1), width));
            Console.WriteLine();
        }

        string top = $"Welcome to Appa v{AppaVersion.Current}";
        string bottom = "The Gata Compiler";
        if (Visible(Spaced(top)) <= w)
        {
            top = Spaced(top);
            bottom = Spaced(bottom);
        }

        int over = Math.Max(width, Visible(top));
        Console.WriteLine(indent + Centred(top, over, 0.55, C.BOLD));
        Console.WriteLine(indent + Centred(bottom, over, 0.80, C.DIM));
        Console.WriteLine();
    }

    /// <summary>
    /// Letterspaces a line - one space between letters, three between words - so a short string reads
    /// as a masthead rather than as a sentence.
    /// </summary>
    private static string Spaced(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = string.Join(' ', words[i].ToCharArray());
        return string.Join("   ", words);
    }

    /// <summary>
    /// Centres a line in the terminal and paints it at the given point down the gradient.
    /// </summary>
    private static string Centred(string text, int w, double rowT, string style)
    {
        int width = Visible(text);
        return new string(' ', Math.Max(0, (w - width) / 2)) + style + Paint(text, rowT, width);
    }

    /// <summary>
    /// Colours one row of a block, walking the gradient left to right while <paramref name="rowT"/>
    /// carries how far down it already is.
    /// </summary>
    private static string Paint(string row, double rowT, int width)
    {
        var sb = new StringBuilder();
        int col = 0, last = -1;
        foreach (var rune in row.EnumerateRunes())
        {
            double across = width > 1 ? (double)col / (width - 1) : 0;
            int step = (int)Math.Round(Steps * Math.Clamp(0.5 * across + 0.5 * rowT, 0, 1));
            if (step != last)
            {
                sb.Append(Code((double)step / Steps));
                last = step;
            }
            sb.Append(rune.ToString());
            col++;
        }
        return sb.Append(C.NC).ToString();
    }

    /// <summary>
    /// The SGR escape for a point on the gradient, in 24-bit colour where the terminal advertises it
    /// and in the nearest of three xterm-256 golds where it does not.
    /// </summary>
    private static string Code(double t)
    {
        if (!TrueColor)
            return $"\x1b[38;5;{Ramp[Math.Clamp((int)(t * Ramp.Length), 0, Ramp.Length - 1)]}m";

        int Mix(int a, int b) => (int)Math.Round(a + (b - a) * t);
        return $"\x1b[38;2;{Mix(Start.R, End.R)};{Mix(Start.G, End.G)};{Mix(Start.B, End.B)}m";
    }

    private static bool TrueColor
    {
        get
        {
            string? ct = Environment.GetEnvironmentVariable("COLORTERM");
            return ct != null && (ct.Contains("truecolor") || ct.Contains("24bit"));
        }
    }

    /// <summary>
    /// The number of columns a string occupies.
    /// </summary>
    private static int Visible(string s)
    {
        int n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    /// <summary>
    /// The real terminal size, unclamped.
    /// </summary>
    private static (int Width, int Height) Viewport()
    {
        try
        {
            return Console.IsOutputRedirected ? (80, 24) : (Console.WindowWidth, Console.WindowHeight);
        }
        catch (IOException)
        {
            return (80, 24);
        }
    }
}

#endregion
