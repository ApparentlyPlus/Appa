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
    /// Kernel-only builds produce kmain.c. User-only produce program.c. Both produce kmain.c,
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
    /// The generated main() for a hosted build: stashes argc/argv into the gata_argc/gata_argv
    /// globals an environment's _env_argc/_env_argv can read, then calls the user entry function and/or the launcher.
    /// </summary>
    private static string HostedMain(string? entryCName, bool launch)
    {
        if (entryCName == null && !launch) return "";
        var w = new CodeWriter();
        using (w.Block("int main(int argc, char** argv) {"))
        {
            w.Line("gata_argc = argc;");
            w.Line("gata_argv = argv;");
            if (launch) w.Line($"{LauncherName}();");
            if (entryCName != null) w.Line($"{entryCName}();");
            w.Line("return 0;");
        }
        return w.ToString();
    }

    /// <summary>
    /// A stable SHA-256 hash of the emitted content, used to seed the decorative header generator.
    /// </summary>
    private static int ContentSeed(EmitOutput o)
    {
        string all = string.Concat(
            o.SharedHeader, o.KernelPreamble, o.KernelTypes, o.KernelFwd, o.KernelFuncs,
            o.KernelBoot, o.UserPreamble, o.UserTypes, o.UserFwd, o.UserFuncs);

        byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all));
        return BitConverter.ToInt32(digest, 0);
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
    /// first four are the unit's skeleton and are written whether or not they carry text. Anything
    /// after them is optional and an empty one contributes nothing, not even a blank line.
    /// </summary>
    private static string Concat(string name, string s1, string s2, string s3, string s4, params ReadOnlySpan<string> rest)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Finesse.GenerateKewlHeader(name)).Append('\n').Append(s1).Append('\n').Append(s2).Append('\n')
            .Append(s3).Append('\n').Append(s4);
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
        foreach (var p in procs)
        {
            foreach (var t in p.Threads)
            {
                if (t.EntryFunc != null) w.Line($"void {t.EntryFunc.CName}(void* arg);");
            }
        }
        return w.ToString();
    }

    /// <summary>
    /// Builds the userspace launcher that creates processes and spawns their threads through
    /// environment bindings, so porting the OS is an edit to env.*.g and never to this file. No C
    /// name is hardcoded here. They come from whatever @intrinsic binds.
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

                foreach (var t in proc.Threads)
                {
                    if (t.EntryFunc == null) continue;

                    string isUser = t.EntryFunc.Vis == Visibility.Kernel ? "0" : "1";
                    w.Line($"{threadSpawn}({handle}, \"{t.Name}\", {t.EntryFunc.CName}, {isUser});");
                }
            }
        }
        return w.ToString();
    }
}
