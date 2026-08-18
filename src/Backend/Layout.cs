namespace Appa;

internal sealed record EmitOutput(string SharedHeader, string KernelPreamble, string KernelTypes,
    string KernelFwd, string KernelFuncs, string KernelBoot, string UserPreamble,
    string UserTypes, string UserFwd, string UserFuncs, IReadOnlyList<IrProcess> Processes,
    bool HasKernelRealm, bool HasUserRealm, string? UserEntryCName);

/// <summary>
/// A named output file produced by the compiler for a single translation unit.
/// </summary>
internal record OutputFile(string Name, string Content);

internal static class Layout
{
    /// <summary>
    /// The C function generated to create every process and spawn its threads. Named here so the
    /// collision check can reserve it against a declaration that would take it over.
    /// </summary>
    public const string LauncherName = "uapps";

    /// <summary>
    /// Composes the emitter output into the set of translation-unit files for the build.
    /// Kernel-only builds produce kmain.c; user-only produce program.c; both produce kmain.c,
    /// uproc.c, uproc.h, and umain.c.
    public static IReadOnlyList<OutputFile> Compose(EmitOutput o, SymbolTable sym)
    {
        // Seed the header generator with a static hash of the content
        Finesse.Seed(ContentSeed(o));
        var files = new List<OutputFile> { new("shared.h", SharedHeader(o)) };
        bool launch = o.Processes.Count > 0;

        if (o.HasKernelRealm && o.HasUserRealm)
        {
            files.Add(new("kmain.c", Concat("kmain.c", o.KernelPreamble, o.KernelTypes, o.KernelFwd, o.KernelFuncs, o.KernelBoot)));
            files.Add(new("uproc.c", Concat("uproc.c", o.UserPreamble, o.UserTypes, o.UserFwd, o.UserFuncs)));
            files.Add(new("uproc.h", UprocHeader(o.Processes)));
            files.Add(new("umain.c", Launcher(o.Processes, sym, ownUnit: true)));
        }
        else if (o.HasUserRealm)
        {
            files.Add(new("program.c", Concat("program.c", o.UserPreamble, o.UserTypes, o.UserFwd, o.UserFuncs,
                launch ? Launcher(o.Processes, sym, ownUnit: false) : "",
                HostedMain(o.UserEntryCName, launch))));
        }
        else if (o.HasKernelRealm)
        {
            files.Add(new("kmain.c", Concat("kmain.c", o.KernelPreamble, o.KernelTypes, o.KernelFwd, o.KernelFuncs,
                launch ? Launcher(o.Processes, sym, ownUnit: false) : "", o.KernelBoot)));
        }
        return files;
    }

    /// <summary>
    /// The generated main() for a hosted build: the launcher first, so a thread is running by the
    /// time the entry func does anything, then the entry func itself. Empty when the build has
    /// neither, which only a module that failed validation can reach.
    /// </summary>
    private static string HostedMain(string? entryCName, bool launch)
    {
        if (entryCName == null && !launch) return "";
        var w = new CodeWriter();
        using (w.Block("int main(void) {"))
        {
            if (launch) w.Line($"{LauncherName}();");
            if (entryCName != null) w.Line($"{entryCName}();");
            w.Line("return 0;");
        }
        return w.ToString();
    }

    /// <summary>
    /// A stable SHA hash of the emitted content, used to seed the decorative header generator. Fed
    /// section by section, so the program text is not materialised a third time to be hashed.
    /// </summary>
    private static int ContentSeed(EmitOutput o)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);

        ReadOnlySpan<string> sections =
        [
            o.SharedHeader, o.KernelPreamble, o.KernelTypes, o.KernelFwd, o.KernelFuncs,
            o.KernelBoot, o.UserPreamble, o.UserTypes, o.UserFwd, o.UserFuncs,
        ];

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var section in sections) Feed(hash, section, buffer);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return BitConverter.ToInt32(digest[..4]);
    }

    /// <summary>
    /// Feeds one section's UTF-8 bytes to the hash in buffer-sized chunks, splitting on whole
    /// characters so a surrogate pair is never encoded across two chunks.
    /// </summary>
    private static void Feed(System.Security.Cryptography.IncrementalHash hash, string section, byte[] buffer)
    {
        var utf8 = System.Text.Encoding.UTF8;
        ReadOnlySpan<char> rest = section.AsSpan();
        int chunk = buffer.Length / 3;
        while (rest.Length > chunk)
        {
            int take = char.IsHighSurrogate(rest[chunk - 1]) ? chunk - 1 : chunk;
            hash.AppendData(buffer.AsSpan(0, utf8.GetBytes(rest[..take], buffer)));
            rest = rest[take..];
        }
        if (!rest.IsEmpty) hash.AppendData(buffer.AsSpan(0, utf8.GetBytes(rest, buffer)));
    }

    /// <summary>
    /// Builds the shared header file content with the pragma-once guard and emitted shared types.
    /// </summary>
    private static string SharedHeader(EmitOutput o)
    {
        var w = new CodeWriter();
        w.Lines(Finesse.GenerateKewlHeader("shared.h"), "#pragma once", "");
        w.Line(o.SharedHeader);
        return w.ToString();
    }

    /// <summary>
    /// Concatenates sections into a single translation unit string with a file header comment. The
    /// first four are the unit's skeleton and are written whether or not they carry text; anything
    /// after them is optional and an empty one contributes nothing, not even a blank line.
    /// </summary>
    private static string Concat(string name, string s1, string s2, string s3, string s4,
                                 params ReadOnlySpan<string> rest)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Finesse.GenerateKewlHeader(name)).Append('\n')
          .Append(s1).Append('\n')
          .Append(s2).Append('\n')
          .Append(s3).Append('\n')
          .Append(s4);
        foreach (var section in rest)
        {
            if (section.Length > 0) sb.Append('\n').Append(section);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the uproc.h header that forward-declares every thread entry function.
    /// </summary>
    private static string UprocHeader(IReadOnlyList<IrProcess> procs)
    {
        var w = new CodeWriter();
        w.Lines(Finesse.GenerateKewlHeader("uproc.h"), "#pragma once", "");
        for (int i = 0; i < procs.Count; i++)
        {
            var p = procs[i];
            for (int j = 0; j < p.Threads.Count; j++)
            {
                var t = p.Threads[j];
                if (t.EntryFunc is { } e)
                {
                    w.Line($"void {e.CName}(void* arg);");
                }
            }
        }
        return w.ToString();
    }

    /// <summary>
    /// Builds the userspace launcher that creates processes and spawns their threads through
    /// environment bindings, so porting the OS is an edit to env.*.g and never to this file. No C
    /// name is hardcoded here; they come from whatever @intrinsic binds.
    /// </summary>
    private static string Launcher(IReadOnlyList<IrProcess> procs, SymbolTable sym, bool ownUnit)
    {
        string procCreate = sym.FloorName(Roles.EnvProcCreate);
        string procHide = sym.FloorName(Roles.EnvProcHide);
        string threadSpawn = sym.FloorName(Roles.EnvThreadSpawn);

        var w = new CodeWriter();
        if (ownUnit)
        {
            w.Lines(
                Finesse.GenerateKewlHeader("umain.c"),
                "#include \"uproc.h\"",
                "",
                "// Topology floor provided by the environment (env.*.g).",
                $"extern void* {procCreate}(const char* name);",
                $"extern void  {procHide}(void* proc);",
                $"extern void  {threadSpawn}(void* proc, const char* name, void (*entry)(void*), int is_user);",
                "");
        }
        using (w.Block($"void {LauncherName}(void) {{"))
        {
            for (int i = 0; i < procs.Count; i++)
            {
                var proc = procs[i];
                string handle = $"__p{i}";
                w.Line($"void* {handle} = {procCreate}(\"{proc.Name}\");");
                if (proc.Mode == "background")
                    w.Line($"{procHide}({handle});");
                for (int j = 0; j < proc.Threads.Count; j++)
                {
                    var t = proc.Threads[j];
                    if (t.EntryFunc is { } e)
                    {
                        string isUser = e.Vis == Visibility.Kernel ? "0" : "1";
                        w.Line($"{threadSpawn}({handle}, \"{t.Name}\", {e.CName}, {isUser});");
                    }
                }
            }
        }
        return w.ToString();
    }
}
