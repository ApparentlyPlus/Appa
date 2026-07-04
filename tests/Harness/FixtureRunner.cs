namespace Appa.Tests;

using System.Text.RegularExpressions;
using Appa;

/// <summary>
/// Drives the compiler's front-end over a torture fixture the same way the CLI's
/// --pure-transpile build does: env file + entry file through Transpile, then
/// BuildModule and the structural/environment/floor validation passes.
/// </summary>
internal static partial class FixtureRunner
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly string LibgataDir = Path.Combine(FixturesDir, "Libgata");
    private static readonly string EnvPath = Path.Combine(FixturesDir, "Envs", "env.GatOS.g");

    [GeneratedRegex(@"^// EXPECT (G\d+)")]
    private static partial Regex ExpectedCodeHeader();

    /// <summary>
    /// Top-level *.g fixtures directly inside a torture tier folder (Good, Bad, or
    /// Warn), excluding companion files in subdirectories like importpath/.
    /// </summary>
    public static IEnumerable<string> Fixtures(string tier) =>
        Directory.GetFiles(Path.Combine(FixturesDir, "Torture", tier), "*.g")
                 .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// Extracts the expected diagnostic code from a bad/warn fixture's first-line
    /// "// EXPECT Gxxx" header.
    /// </summary>
    public static string ExpectedCode(string fixturePath)
    {
        string firstLine = File.ReadLines(fixturePath).First();
        var m = ExpectedCodeHeader().Match(firstLine);
        if (!m.Success) throw new InvalidOperationException($"{fixturePath}: no '// EXPECT Gxxx' header found");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// Runs the front-end pipeline over one fixture entry file plus the shared
    /// GatOS environment and libgata dir, returning the resulting diagnostics.
    /// </summary>
    public static DiagnosticBag Compile(string entryPath)
    {
        string projectRoot = Path.GetDirectoryName(entryPath)!;
        var inputFiles = new List<string> { EnvPath, entryPath };
        var (programs, _, imports, diag) = Pipeline.Transpile(inputFiles, projectRoot, LibgataDir);
        var visible = Pipeline.VisibleModules(imports);
        var (module, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);

        Pipeline.ValidateEnvironment(programs, diag);
        Pipeline.ValidateFloor(module, diag);
        Pipeline.ValidateStructure(programs, diag);
        return diag;
    }
}
