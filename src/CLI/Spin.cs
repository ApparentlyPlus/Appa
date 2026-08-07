namespace Appa;

using System.Diagnostics;

static class Spin
{
    static readonly char[] Frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
    static bool Tty => !Console.IsOutputRedirected;

    // One animation frame
    const int _frameMs = 80;

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static long _drawnAt = -_frameMs;
    static string _drawn = "";
    static bool _cursorHidden;

    /// <summary>
    /// True when the spinner has somewhere to animate - false under a pipe or a test harness, where
    /// in-place redraws would be recorded as line noise.
    /// </summary>
    public static bool IsTty => Tty;

    /// <summary>
    /// Renders the spinner line in place: the frame, the label, and whatever detail the caller wants
    /// after it (a byte count, a percentage).
    /// </summary>
    public static void Tick(string label, string? detail = null)
    {
        if (!Tty) return;

        long now = _clock.ElapsedMilliseconds;
        if (now - _drawnAt < _frameMs) return;

        string line = $"  {C.EMBER}{Frames[(int)(now / _frameMs) % Frames.Length]}{C.NC} " +
                      $"{label}{C.DIM}{(detail is null ? "..." : " " + detail)}{C.NC}";
        _drawnAt = now;
        if (line == _drawn) return;
        _drawn = line;

        HideCursor();
        Out.Redraw(line);
    }

    /// <summary>
    /// Ends an animation: restores the cursor and clears the line the spinner was drawn on, leaving
    /// the caller free to write the finished line over it.
    /// </summary>
    public static void Stop()
    {
        if (!Tty) return;
        _drawn = "";
        _drawnAt = -_frameMs;
        ShowCursor();
        Out.ClearRedraw();
    }

    /// <summary>
    /// A blinking block parked at the end of a line that repaints ten times a second is most of what
    /// reads as flicker. It is restored by Done, and again on process exit so an interrupted run
    /// cannot leave a terminal with no cursor.
    /// </summary>
    static void HideCursor()
    {
        if (_cursorHidden) return;
        _cursorHidden = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShowCursor();
        Console.CancelKeyPress += (_, _) => ShowCursor();
        Console.Write("\x1b[?25l");
    }

    static void ShowCursor()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        Console.Write("\x1b[?25h");
    }

    /// <summary>
    /// Animates a label while a process runs, then clears the line. The caller is responsible for
    /// reporting success or failure once it has the exit code.
    /// </summary>
    public static void WhileRunning(Process proc, string label)
    {
        if (!Tty) { Out.Note($"{label}..."); return; }
        while (!proc.WaitForExit(_frameMs)) Tick(label);
        Stop();
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
    /// Runs work on a worker thread and spins on the caller's, so a long blocking step (an extract,
    /// a chmod over a whole toolchain) shows it is alive. The work's exception propagates unwrapped.
    /// </summary>
    public static T While<T>(string label, Func<T> work)
    {
        var sw = Stopwatch.StartNew();
        var task = Task.Run(work);
        if (Tty)
        {
            while (!task.IsCompleted) { Tick(label); Thread.Sleep(_frameMs / 2); }
            Stop();
        }
        T result = task.GetAwaiter().GetResult();
        Done(label, sw.Elapsed);
        return result;
    }

    /// <summary>
    /// Spins while an already-started task runs. Used for work that is asynchronous in its own right
    /// rather than blocking work pushed onto a thread.
    /// </summary>
    public static async Task<T> While<T>(string label, Task<T> task)
    {
        var sw = Stopwatch.StartNew();
        if (Tty)
        {
            while (!task.IsCompleted) { Tick(label); await Task.Delay(_frameMs / 2); }
            Stop();
        }
        T result = await task;
        Done(label, sw.Elapsed);
        return result;
    }

    /// <summary>
    /// Runs blocking work on a worker thread, spinning meanwhile, with no result to return.
    /// </summary>
    public static void While(string label, Action work) => While(label, () => { work(); return 0; });

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
