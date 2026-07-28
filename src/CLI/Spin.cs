namespace Appa;

using System.Diagnostics;

static class Spin
{
    static readonly char[] Frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
    static bool Tty => !Console.IsOutputRedirected;

    /// <summary>
    /// Animates a label while a process runs, then clears the line. The caller is responsible for
    /// reporting success or failure once it has the exit code.
    /// </summary>
    public static void WhileRunning(Process proc, string label)
    {
        if (!Tty) { Out.Note($"{label}..."); return; }
        int i = 0;
        while (!proc.WaitForExit(80))
            Out.Redraw($"  {C.DIM}{Frames[i++ % Frames.Length]}{C.NC} {label}{C.DIM}...{C.NC}");
        Out.ClearRedraw();
    }

    /// <summary>
    /// Runs work synchronously, prints a checkmark and elapsed time line, and returns the result.
    /// </summary>
    public static T Step<T>(string label, Func<T> work)
    {
        var sw = Stopwatch.StartNew();
        T result = work();
        Done(label, sw.Elapsed);
        return result;
    }

    /// <summary>
    /// Runs work synchronously and prints a checkmark and elapsed time line.
    /// </summary>
    public static void Step(string label, Action work) => Step(label, () => { work(); return 0; });

    /// <summary>
    /// Prints a checkmark and elapsed time line for a step that has already completed.
    /// </summary>
    public static void Done(string label, TimeSpan elapsed) => Out.Step(label, elapsed);

    /// <summary>
    /// Formats a TimeSpan as a human-readable elapsed string (e.g. "42ms" or "1.23s").
    /// </summary>
    public static string Fmt(TimeSpan t) =>
        t.TotalSeconds >= 1 ? $"{t.TotalSeconds:F2}s" : $"{Math.Max(1, t.TotalMilliseconds):F0}ms";
}
