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
    private TempDir? _root;

    /// <summary>The downloaded envs/ directory, or null if the toolchain wasn't installed.</summary>
    public string? EnvsDir { get; private set; }

    /// <summary>The downloaded libgata/ directory, or null if the toolchain wasn't installed.</summary>
    public string? LibgataDir { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!ToolchainProbe.HasGatOSToolchain()) return;

        _root = TempDir.Create("appa-boot-fixture-");
        EnvsDir = _root.Combine("envs");
        LibgataDir = _root.Combine("libgata");

        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        await GitHubDirDownloader.DownloadDirectoriesAsync(
            Urls.GataOwner, Urls.GataRepo, Urls.GataRef,
            new Dictionary<string, string> { ["envs/"] = EnvsDir, ["libgata/"] = LibgataDir },
            client);
    }

    public ValueTask DisposeAsync()
    {
        _root?.Dispose();
        return ValueTask.CompletedTask;
    }
}

[CollectionDefinition("Boot")]
public sealed class BootCollection : ICollectionFixture<BootFixture>;
