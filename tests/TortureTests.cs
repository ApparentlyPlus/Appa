namespace Appa.Tests;

using Appa;

/// <summary>
/// Data-driven port of the torture/{good,bad,warn} fixture corpus. Each fixture is
/// tagged Core or Stdlib by whether it imports Collections, so the majority
/// (language-core) tier can be filtered independently of the smaller stdlib tier.
/// </summary>
public class TortureTests
{
    // Fixtures whose behavior depends on the Collections module (List[T], Sort,
    // Stack/Queue/Map/Set/PriorityQueue, generic containers) rather than pure
    // language semantics.
    private static readonly HashSet<string> StdlibFixtures = new(StringComparer.OrdinalIgnoreCase)
    {
        "algorithms.g", "generic_container_param.g", "generics.g",
        "indexer_compound_pure_no_hoist.g", "int_long_mem_extras.g", "list_indexer.g",
        "map_rewrite.g", "priority_queue.g", "set_module.g", "string_extras.g",
        "generic_arity_mismatch.g", "generic_error_display_name.g",
        "generic_garbage.g", "generic_void.g",
    };

    private static string Tier(string fixturePath) =>
        StdlibFixtures.Contains(Path.GetFileName(fixturePath)) ? "Stdlib" : "Core";

    /// <summary>
    /// Every torture/good fixture must transpile with zero errors.
    /// </summary>
    public static TheoryData<string> GoodFixtures() => [.. FixtureRunner.Fixtures("Good")];

    [Theory]
    [MemberData(nameof(GoodFixtures))]
    public void GoodFixtureTranspilesCleanly(string fixturePath)
    {
        var diag = FixtureRunner.Compile(fixturePath);
        Assert.False(diag.HasErrors,
            $"[{Tier(fixturePath)}] {Path.GetFileName(fixturePath)} should transpile with no errors but got: " +
            string.Join(", ", diag.All.Where(d => d.Severity == Severity.Error).Select(d => d.Code)));
    }

    /// <summary>
    /// Every torture/bad fixture must fail with exactly the diagnostic code named
    /// in its "// EXPECT Gxxx" header.
    /// </summary>
    public static TheoryData<string> BadFixtures() => [.. FixtureRunner.Fixtures("Bad")];

    [Theory]
    [MemberData(nameof(BadFixtures))]
    public void BadFixtureFailsWithExpectedCode(string fixturePath)
    {
        string expected = FixtureRunner.ExpectedCode(fixturePath);
        var diag = FixtureRunner.Compile(fixturePath);
        Assert.True(diag.HasErrors, $"[{Tier(fixturePath)}] {Path.GetFileName(fixturePath)} expected {expected} but produced no errors");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == expected);
    }

    /// <summary>
    /// Every torture/warn fixture must produce the warning code named in its header.
    /// </summary>
    public static TheoryData<string> WarnFixtures() => [.. FixtureRunner.Fixtures("Warn")];

    [Theory]
    [MemberData(nameof(WarnFixtures))]
    public void WarnFixtureProducesExpectedWarningCode(string fixturePath)
    {
        string expected = FixtureRunner.ExpectedCode(fixturePath);
        var diag = FixtureRunner.Compile(fixturePath);
        Assert.Contains(diag.All, d => d.Severity == Severity.Warning && d.Code == expected);
    }
}
