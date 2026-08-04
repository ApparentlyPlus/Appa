namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// Builds a Gata program against the real standard library, compiles the emitted C and runs it -
/// answering "does it compute the right answer and give the memory back", which nothing stopping at
/// gcc can. Builds with ASan/UBSan where the toolchain has them.
/// </summary>
internal static class HostedRun
{
    /// <summary>
    /// Outcome of a build-compile-run cycle: the program's combined stdout and stderr with newlines
    /// normalised, its exit status, and whether the binary was actually built with the sanitizers.
    /// </summary>
    internal readonly record struct Result(string Output, int ExitCode, bool Sanitized);

    /// <summary>
    /// Finds the sibling Gata checkout by walking up from the test binary, looking for a directory
    /// holding both libgata/ and envs/. Returns null if this is not a source checkout.
    /// </summary>
    public static string? FindGataCheckout()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Gata");
            if (Directory.Exists(Path.Combine(candidate, "libgata")) &&
                File.Exists(Path.Combine(candidate, "envs", "env.hosted.g")))
                return candidate;
        }
        return null;
    }
    
    // The compiler for sanitizer builds, probed once
    private static readonly Lazy<string?> _compiler = new(ProbeCompiler, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Locates a usable host C compiler, or null.
    /// </summary>
    public static string? FindCompiler() => _compiler.Value;

    private static string? ProbeCompiler()
    {
        var usable = ((string[])["cc", "gcc", "clang"]).Where(CanRun).ToList();
        return usable.FirstOrDefault(SupportsSanitizers) ?? usable.FirstOrDefault();
    }

    // The compiler for release-flag builds, probed once
    private static readonly Lazy<string?> _releaseCompiler =
        new(ProbeReleaseCompiler, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Locates a compiler that accepts the flag set a Release build really uses, or null.
    /// </summary>
    public static string? FindCompilerAcceptingReleaseFlags() => _releaseCompiler.Value;

    private static string? ProbeReleaseCompiler()
    {
        string flags = string.Join(" ", GatosFlags.For(Mode.Release));
        foreach (var exe in (string[])["cc", "gcc", "clang"])
        {
            if (!CanRun(exe)) continue;
            try
            {
                using var probe = TempDir.Create("appa-relflags-probe-");
                File.WriteAllText(probe.Combine("p.c"), "int main(void){return 0;}");
                var (code, _) = Run(exe, $"{flags} -o probe p.c", probe.Path);
                if (code == 0) return exe;
            }
            catch { /* try the next one */ }
        }
        return null;
    }

    /// <summary>
    /// Returns true if the named compiler is on PATH and answers --version.
    /// </summary>
    public static bool CanRun(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; /* not on PATH */ }
    }

    // Whether a given compiler can actually link a sanitized binary
    private static readonly Dictionary<string, bool> _sanitizerProbe = [];

    private const string SanitizerFlags = "-fsanitize=address,undefined -fno-omit-frame-pointer";

    /// <summary>
    /// Returns true if the host toolchain can compile and link with ASan/UBSan. A compiler that
    /// accepts the flag but has no runtime library to link against fails here rather than turning
    /// every later test red.
    /// </summary>
    public static bool SupportsSanitizers(string cc)
    {
        lock (_sanitizerProbe)
        {
            if (_sanitizerProbe.TryGetValue(cc, out bool known)) return known;
            bool answer;
            try
            {
                using var probe = TempDir.Create("appa-asan-probe-");
                File.WriteAllText(probe.Combine("p.c"), "int main(void){return 0;}");
                var (code, _) = Run(cc, $"{SanitizerFlags} -o probe p.c", probe.Path);
                answer = code == 0;
            }
            catch { answer = false; }
            _sanitizerProbe[cc] = answer;
            return answer;
        }
    }

    /// <summary>
    /// Transpiles <paramref name="files"/> (keyed by path under src/) as a Hosted project against
    /// the real libgata, then compiles and runs it. Goes through the appa CLI so manifests and
    /// imports are covered, and compiles with -Werror.
    /// </summary>
    public static Result BuildAndRun(IReadOnlyDictionary<string, string> files, string gata, string cc) =>
        BuildAndRun(files, gata, cc, release: false);

    /// <summary>
    /// The same cycle in the build mode the caller asks for.
    /// </summary>
    public static Result BuildAndRun(IReadOnlyDictionary<string, string> files, string gata, string cc, bool release)
    {
        using var work = TempDir.Create("appa-managed-union-");
        Directory.CreateDirectory(work.Combine("src"));
        foreach (var (name, text) in files)
        {
            string full = work.Combine("src", name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        File.WriteAllText(work.Combine("host.gconf"), """
            <appa>
                <ProjectName>host</ProjectName>
                <TargetBackend>Hosted</TargetBackend>
                <BuildMode>{{MODE}}</BuildMode>
            </appa>
            """.Replace("{{MODE}}", release ? "Release" : "Debug"));

        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (buildCode, buildOut) = Run("dotnet",
            $"\"{appaDll}\" build \"{work.Path}\" --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);
        Assert.True(buildCode == 0, $"appa build failed:\n{buildOut}");

        var outDir = work.Combine("transpilation");
        Assert.True(File.Exists(Path.Combine(outDir, "program.c")),
            $"expected transpilation/program.c, got: " +
            $"{string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName))}");

        bool sanitized = !release && SupportsSanitizers(cc);
        string exe = work.Combine("prog");
        string warnings = "-Wall -Wextra -Werror -Wno-unused-parameter -Wno-unused-function " +
                          "-Wno-unused-variable -Wno-missing-field-initializers";

        string opt = release ? string.Join(" ", GatosFlags.For(Mode.Release)) : "";
        var (ccCode, ccOut) = Run(cc,
            $"-std=c11 -I. {warnings} {opt} {(sanitized ? SanitizerFlags + " -g" : "")} -o \"{exe}\" program.c -lm",
            outDir);
        Assert.True(ccCode == 0, $"{cc} rejected the emitted C:\n{ccOut}");
        var env = new Dictionary<string, string> { ["ASAN_OPTIONS"] = "detect_leaks=0" };
        var (runCode, runOut) = Run(exe, "", work.Path, env);
        return new Result(runOut.Replace("\r\n", "\n"), runCode, sanitized);
    }

    /// <summary>
    /// Convenience overload for a single-file program.
    /// </summary>
    public static Result BuildAndRun(string source, string gata, string cc) =>
        BuildAndRun(new Dictionary<string, string> { ["main.g"] = source }, gata, cc);

    /// <summary>
    /// Asserts the program ran to completion with no sanitizer report: no use-after-free, double
    /// free or undefined behaviour. Leaks are not this check's job - they are caught
    /// deterministically instead.
    /// </summary>
    public static void AssertClean(Result r)
    {
        Assert.True(r.ExitCode == 0,
            $"the program exited {r.ExitCode}" +
            (r.Sanitized ? " (a sanitizer or leak report is below)" : "") + $":\n{r.Output}");
        Assert.DoesNotContain("AddressSanitizer", r.Output);
        Assert.DoesNotContain("LeakSanitizer", r.Output);
        Assert.DoesNotContain("runtime error:", r.Output);
    }

    /// <summary>
    /// Runs a process to completion, returning its exit code and combined output.
    /// </summary>
    public static (int Code, string Output) Run(
        string exe, string args, string cwd, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, $"'{exe} {args}' did not finish within 180s and was killed\n" +
                        stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
