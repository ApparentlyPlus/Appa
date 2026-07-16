namespace Appa;

internal sealed record EmitOutput(string SharedHeader, string KernelPreamble, string KernelTypes,
    string KernelFwd, string KernelFuncs, string KernelBoot, string UserPreamble,
    string UserTypes, string UserFwd, string UserFuncs, IReadOnlyList<IrProcess> Processes,
    bool HasKernelRealm, bool HasUserRealm, string? UserEntryCName);

/// <summary>
/// A named output file produced by the compiler for a single translation unit.
/// </summary>
internal record OutputFile(string Name, string Content);

/// <summary>
/// Composes emitted sections into translation units. The realms a build emits
/// come from the environment, never a command-line switch.
/// </summary>
internal static class Layout
{
    /// <summary>
    /// Composes the emitter output into the set of translation-unit files for the build.
    /// Kernel-only builds produce kmain.c; user-only produce program.c; both produce
    /// kmain.c, uproc.c, uproc.h, and umain.c with a generated process launcher.
    /// </summary>
    public static IReadOnlyList<OutputFile> Compose(EmitOutput o, SymbolTable sym)
    {
        // Seed the header generator with a static hash of the content
        Finesse.Seed(ContentSeed(o));
        var files = new List<OutputFile> { new("shared.h", SharedHeader(o)) };

        if (o.HasKernelRealm && o.HasUserRealm)
        {
            files.Add(new("kmain.c", Concat("kmain.c", o.KernelPreamble, o.KernelTypes, o.KernelFwd, o.KernelFuncs, o.KernelBoot)));
            files.Add(new("uproc.c", Concat("uproc.c", o.UserPreamble, o.UserTypes, o.UserFwd, o.UserFuncs)));
            files.Add(new("uproc.h", UprocHeader(o.Processes)));
            files.Add(new("umain.c", Launcher(o.Processes, sym)));
        }
        else if (o.HasUserRealm)
        {
            // Hosted (user-only) builds get a generated main() calling the single validated
            // user-realm entry func, so program.c is actually invocable.
            string main = o.UserEntryCName is { } cname
                ? $"int main(void) {{\n    {cname}();\n    return 0;\n}}\n"
                : "";
            files.Add(new("program.c", Concat("program.c", o.UserPreamble, o.UserTypes, o.UserFwd, o.UserFuncs, main)));
        }
        else if (o.HasKernelRealm)
        {
            files.Add(new("kmain.c", Concat("kmain.c", o.KernelPreamble, o.KernelTypes, o.KernelFwd, o.KernelFuncs, o.KernelBoot)));
        }
        return files;
    }

    /// <summary>
    /// A stable SHA hash of the emitted content, used to seed the decorative header generator.
    /// </summary>
    private static int ContentSeed(EmitOutput o)
    {
        string st = o.SharedHeader + o.KernelPreamble + o.KernelTypes + o.KernelFwd + o.KernelFuncs +
                    o.KernelBoot + o.UserPreamble + o.UserTypes + o.UserFwd + o.UserFuncs;
        
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(st);
        
        // we can rent a temporary buffer or use stackalloc if the file is small
        byte[]? rentedArray = null;
        Span<byte> byteBuffer = byteCount <= 128 * 1024 ? stackalloc byte[byteCount] 
            : (rentedArray = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount));

        // We also need a buffer for the hash itself
        Span<byte> hashBuffer = stackalloc byte[32];

        System.Text.Encoding.UTF8.GetBytes(st, byteBuffer);
        System.Security.Cryptography.SHA256.HashData(byteBuffer[..byteCount], hashBuffer);
        return BitConverter.ToInt32(hashBuffer[..4]);
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
    /// Concatenates non-empty sections into a single translation unit string with a file header comment.
    /// </summary>
    private static string Concat(string name, string s1, string s2, string s3, string s4, string s5 = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Finesse.GenerateKewlHeader(name))
          .Append(s1).Append('\n')
          .Append(s2).Append('\n')
          .Append(s3).Append('\n')
          .Append(s4);
        if (s5.Length > 0)
        {
            sb.Append('\n').Append(s5);
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
    /// Builds the userspace launcher that creates processes and spawns their threads.
    /// Process and thread spawning use environment bindings, so porting the OS is an
    /// edit to env.*.g, never to this file. The C names themselves are never hardcoded
    /// here - they come from whatever libgata's @intrinsic(env_proc_create) etc. bind to.
    /// </summary>
    private static string Launcher(IReadOnlyList<IrProcess> procs, SymbolTable sym)
    {
        string procCreate = sym.IntrinsicOrNull(Roles.EnvProcCreate) ?? "_env_proc_create";
        string procHide = sym.IntrinsicOrNull(Roles.EnvProcHide) ?? "_env_proc_hide";
        string threadSpawn = sym.IntrinsicOrNull(Roles.EnvThreadSpawn) ?? "_env_thread_spawn";

        var w = new CodeWriter();
        w.Lines(
            Finesse.GenerateKewlHeader("umain.c"),
            "#include \"uproc.h\"",
            "",
            "// Topology floor provided by the environment (env.*.g).",
            $"extern void* {procCreate}(const char* name);",
            $"extern void  {procHide}(void* proc);",
            $"extern void  {threadSpawn}(void* proc, const char* name, void (*entry)(void*), int is_user);",
            "");
        using (w.Block("void uapps(void) {"))
        {
            for (int i = 0; i < procs.Count; i++)
            {
                var proc = procs[i];
                w.Line($"void* {proc.Name} = {procCreate}(\"{proc.Name}\");");
                if (proc.Mode == "background")
                    w.Line($"{procHide}({proc.Name});");
                for (int j = 0; j < proc.Threads.Count; j++)
                {
                    var t = proc.Threads[j];
                    if (t.EntryFunc is { } e)
                    {
                        string isUser = e.Vis == Visibility.Kernel ? "0" : "1";
                        w.Line($"{threadSpawn}({proc.Name}, \"{t.Name}\", {e.CName}, {isUser});");
                    }
                }
            }
        }
        return w.ToString();
    }
}
