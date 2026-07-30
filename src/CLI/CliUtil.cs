namespace Appa;

using System.Diagnostics.CodeAnalysis;

internal static class Cli
{
    #region Utilities

    /// <summary>
    /// Resolves the environment file, entry file, project root, and libgata directory for a build
    /// or check invocation.
    /// </summary>
    internal static (Manifest? manifest, string envPath, string entryPath, string projectRoot, string stdlibDir) ResolveInputs(
        string? manifestArg, string? envOverride, string? entryOverride, string? stdlibOverride,
        bool loose, string manifestHint, string looseHint)
    {
        Manifest? manifest = null;
        if (!loose)
        {
            try
            {
                if (manifestArg != null && !Directory.Exists(manifestArg) && !File.Exists(manifestArg))
                    Cli.Fail($"'{manifestArg}' does not exist",
                        "the argument is a project directory, or the path to its .gconf");

                string? manifestPath =
                    manifestArg == null ? ManifestReader.Discover(Directory.GetCurrentDirectory())
                    : Directory.Exists(manifestArg) ? ManifestReader.Discover(manifestArg)
                    : manifestArg;
                if (manifestPath != null) manifest = ManifestReader.Load(manifestPath);
            }
            catch (ManifestError e) { Cli.Fail(e.Message); }
            if (manifest == null)
                FailNoManifest(envOverride, entryOverride, manifestHint);
        }
        else if (manifestArg != null)
            Log.Warn($"project argument '{manifestArg}' is ignored with {looseHint} (loose-file mode discovers nothing from a project)");

        var unreadableEnvs = new List<string>();
        string? envPath = envOverride
            ?? (manifest != null ? Pipeline.DiscoverEnv(manifest.Dir, unreadableEnvs) : null);
        string? entryPath = entryOverride ?? (manifest != null ? Pipeline.DiscoverEntry(manifest.Dir) : null);
        if (envPath == null)
            Fail("no environment found - mark one project file @environment, or pass --env",
                 unreadableEnvs.Count > 0
                     ? $"could not parse {string.Join(", ", unreadableEnvs)}; if the environment is declared there, fix the syntax error first"
                     : null);
        if (entryPath == null) Fail("no entry point - expected src/main.g, or pass --entry");

        string projectRoot = manifest?.Dir ?? Path.GetDirectoryName(Path.GetFullPath(entryPath))!;
        string? stdlibDir = stdlibOverride ?? Pipeline.FindLibgata();
        if (stdlibDir == null) Fail("cannot find libgata - run 'appa setup' or pass --stdlib <dir>");
        foreach (var p in new[] { envPath, entryPath })
            if (!File.Exists(p)) Fail($"file not found: {p}");

        return (manifest, envPath, entryPath, projectRoot, stdlibDir);
    }

    /// <summary>
    /// Reports that no project was found, naming what is actually missing.
    /// </summary>
    [DoesNotReturn]
    private static void FailNoManifest(string? envOverride, string? entryOverride, string manifestHint)
    {
        if ((envOverride == null) != (entryOverride == null))
        {
            string had = envOverride != null ? "--env" : "--entry";
            string missing = envOverride != null ? "--entry" : "--env";
            Fail($"{had} was given without {missing}",
                $"building without a .gconf needs both, so the environment and the entry point are " +
                $"each chosen explicitly - add {missing} <file>, or drop {had} and build a project");
        }
        if (envOverride != null)
            Fail("--env and --entry need '--pure-transpile' to build without a .gconf",
                $"use '{manifestHint}' to emit C from loose files, or run this in a project directory");
        Fail("no <project>.gconf found - run 'appa init', or use " + manifestHint);
    }

    /// <summary>
    /// Writes all output files to a directory, creating it if necessary.
    /// </summary>
    internal static void WriteOutputs(IReadOnlyList<OutputFile> files, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f.Name), f.Content);
    }

    /// <summary>
    /// Writes the dense-to-readable name sourcemap as sourcemap.json in the given directory.
    /// </summary>
    internal static void WriteSourcemap(IReadOnlyDictionary<string, string> map, string dir)
    {
        if (map.Count == 0) return;
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder("{\n");
        var items = map.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        for (int i = 0; i < items.Count; i++)
            sb.Append($"  \"{items[i].Key}\": \"{items[i].Value}\"{(i < items.Count - 1 ? "," : "")}\n");
        sb.Append("}\n");
        File.WriteAllText(Path.Combine(dir, "sourcemap.json"), sb.ToString());
    }

    /// <summary>
    /// Recursively copies a directory tree from src to dst.
    /// </summary>
    internal static void CopyDirectory(string src, string dst)
    {
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), true);
    }

    /// <summary>
    /// Parses a timeout argument of the form "30s", "5m", or "1h" into seconds. An unrecognized
    /// format is a hard error, never a silent default.
    /// </summary>
    internal static int ParseTimeout(string val)
    {
        var m = System.Text.RegularExpressions.Regex.Match(val, @"^(\d+)([smh])$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
            return m.Groups[2].Value switch { "m" => n * 60, "h" => n * 3600, _ => n };
        Fail($"invalid --timeout value '{val}'; expected a duration like 30s, 5m, or 1h");
        return 0;
    }

    /// <summary>
    /// Reports a fatal configuration error and exits.
    /// </summary>
    [DoesNotReturn]
    internal static void Fail(string message, string? hint = null) { Log.Error(message, hint); Environment.Exit(1); }

    #endregion
}
