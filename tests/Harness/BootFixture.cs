namespace Appa.Tests;

using Appa;

/// <summary>
/// Downloads envs/ and libgata/ from the Gata repo once per test run, into a
/// run-scoped temp directory, so BootTests no longer needs a checked-in duplicate
/// of env.GatOS.g. Only downloads when the GatOS toolchain is actually installed -
/// BootTests skips otherwise, so there's no point doing the network fetch first.
/// </summary>
public sealed class BootFixture : IAsyncLifetime
{
    private string? _root;

    /// <summary>The downloaded envs/ directory, or null if the toolchain wasn't installed.</summary>
    public string? EnvsDir { get; private set; }

    /// <summary>The downloaded libgata/ directory, or null if the toolchain wasn't installed.</summary>
    public string? LibgataDir { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!ToolchainProbe.HasGatOSToolchain()) return;

        _root = Directory.CreateTempSubdirectory("appa-boot-fixture-").FullName;
        EnvsDir = Path.Combine(_root, "envs");
        LibgataDir = Path.Combine(_root, "libgata");

        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        await GitHubDirDownloader.DownloadDirectoriesAsync(
            Urls.GataOwner, Urls.GataRepo, Urls.GataRef,
            new Dictionary<string, string> { ["envs/"] = EnvsDir, ["libgata/"] = LibgataDir },
            client);
    }

    public ValueTask DisposeAsync()
    {
        if (_root != null) try { Directory.Delete(_root, recursive: true); } catch { }
        return ValueTask.CompletedTask;
    }
}

[CollectionDefinition("Boot")]
public sealed class BootCollection : ICollectionFixture<BootFixture>;
