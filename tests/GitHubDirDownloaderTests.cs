namespace Appa.Tests;

using System.Net;
using System.Net.Http;
using Appa;

/// <summary>
/// A fake HttpMessageHandler that dispatches by URL prefix, so tests never touch the
/// real network. Each registered responder is a function from the request URL to a
/// response, allowing per-call-count behavior (e.g. "fail once, then succeed").
/// </summary>
internal sealed class FakeGitHubHandler : HttpMessageHandler
{
    public List<string> RequestedUrls { get; } = [];
    private readonly List<(Func<string, bool> Match, Func<string, HttpResponseMessage> Respond)> _routes = [];

    public void On(Func<string, bool> match, Func<string, HttpResponseMessage> respond) => _routes.Add((match, respond));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.ToString();
        RequestedUrls.Add(url);
        foreach (var (match, respond) in _routes)
            if (match(url)) return Task.FromResult(respond(url));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") });
    }
}

/// <summary>
/// Exercises GitHubDirDownloader's pure logic (tree filtering, the truncation fallback,
/// LFS-pointer detection, and clear failures on 404/401) against a fake HTTP handler -
/// no real network access.
/// </summary>
public class GitHubDirDownloaderTests
{
    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task DownloadsFilesUnderRequestedDirectoriesFromASingleTreeFetch()
    {
        var handler = new FakeGitHubHandler();
        handler.On(u => u.Contains("/git/trees/"), _ => Json("""
            {"truncated": false, "tree": [
                {"path": "envs/env.GatOS.g", "type": "blob"},
                {"path": "envs/env.hosted.g", "type": "blob"},
                {"path": "libgata/String.g", "type": "blob"},
                {"path": "libgata", "type": "tree"},
                {"path": "editors", "type": "tree"},
                {"path": "editors/vscode/readme.md", "type": "blob"}
            ]}
            """));
        handler.On(u => u.Contains("raw.githubusercontent.com") && u.EndsWith("env.GatOS.g"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("gatos-env-content") });
        handler.On(u => u.Contains("raw.githubusercontent.com") && u.EndsWith("env.hosted.g"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hosted-env-content") });
        handler.On(u => u.Contains("raw.githubusercontent.com") && u.EndsWith("String.g"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("string-content") });

        using var client = new HttpClient(handler);
        string root = Path.Combine(Path.GetTempPath(), "appa-ghdl-test-" + Guid.NewGuid());
        string envsDir = Path.Combine(root, "envs");
        string libgataDir = Path.Combine(root, "libgata");

        try
        {
            await GitHubDirDownloader.DownloadDirectoriesAsync("Owner", "Repo", "main",
                new Dictionary<string, string> { ["envs/"] = envsDir, ["libgata/"] = libgataDir }, client);

            Assert.Equal("gatos-env-content", await File.ReadAllTextAsync(Path.Combine(envsDir, "env.GatOS.g")));
            Assert.Equal("hosted-env-content", await File.ReadAllTextAsync(Path.Combine(envsDir, "env.hosted.g")));
            Assert.Equal("string-content", await File.ReadAllTextAsync(Path.Combine(libgataDir, "String.g")));
            // The unrelated editors/ tree entries must not be pulled in.
            Assert.False(Directory.Exists(Path.Combine(root, "editors")));
            // One tree fetch shared across both requested directories, not one per directory.
            Assert.Single(handler.RequestedUrls, u => u.Contains("/git/trees/"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FallsBackToContentsApiWhenTreeIsTruncated()
    {
        var handler = new FakeGitHubHandler();
        handler.On(u => u.Contains("/git/trees/"), _ => Json("""{"truncated": true, "tree": []}"""));
        handler.On(u => u.Contains("/contents/envs?"), _ => Json("""
            [{"path": "envs/env.GatOS.g", "type": "file"}, {"path": "envs/sub", "type": "dir"}]
            """));
        handler.On(u => u.Contains("/contents/envs/sub?"), _ => Json("""
            [{"path": "envs/sub/nested.g", "type": "file"}]
            """));
        handler.On(u => u.Contains("raw.githubusercontent.com") && u.EndsWith("env.GatOS.g"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("top-level") });
        handler.On(u => u.Contains("raw.githubusercontent.com") && u.EndsWith("nested.g"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("nested") });

        using var client = new HttpClient(handler);
        string root = Path.Combine(Path.GetTempPath(), "appa-ghdl-test-" + Guid.NewGuid());

        try
        {
            await GitHubDirDownloader.DownloadDirectoriesAsync("Owner", "Repo", "main",
                new Dictionary<string, string> { ["envs/"] = root }, client);

            Assert.Equal("top-level", await File.ReadAllTextAsync(Path.Combine(root, "env.GatOS.g")));
            Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(root, "sub", "nested.g")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DetectsGitLfsPointerAndRefetchesFromMediaCdn()
    {
        var handler = new FakeGitHubHandler();
        handler.On(u => u.Contains("/git/trees/"), _ => Json("""
            {"truncated": false, "tree": [{"path": "envs/big.bin", "type": "blob"}]}
            """));
        // Must land in the 128-140 byte range the downloader treats as "possibly an LFS pointer".
        const string pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:0000000000000000000000000000000000000000000000000000000000000000\nsize 123\n";
        handler.On(u => u.Contains("raw.githubusercontent.com"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pointer) });
        handler.On(u => u.Contains("media.githubusercontent.com"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("real-binary-content") });

        using var client = new HttpClient(handler);
        string root = Path.Combine(Path.GetTempPath(), "appa-ghdl-test-" + Guid.NewGuid());

        try
        {
            await GitHubDirDownloader.DownloadDirectoriesAsync("Owner", "Repo", "main",
                new Dictionary<string, string> { ["envs/"] = root }, client);

            Assert.Equal("real-binary-content", await File.ReadAllTextAsync(Path.Combine(root, "big.bin")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ThrowsClearErrorOn404()
    {
        var handler = new FakeGitHubHandler();
        handler.On(u => u.Contains("/git/trees/"), _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") });
        using var client = new HttpClient(handler);
        string root = Path.Combine(Path.GetTempPath(), "appa-ghdl-test-" + Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubDirDownloader.DownloadDirectoriesAsync("Owner", "Repo", "main",
                new Dictionary<string, string> { ["envs/"] = root }, client));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ThrowsClearErrorOn401()
    {
        var handler = new FakeGitHubHandler();
        handler.On(u => u.Contains("/git/trees/"), _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(handler);
        string root = Path.Combine(Path.GetTempPath(), "appa-ghdl-test-" + Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubDirDownloader.DownloadDirectoriesAsync("Owner", "Repo", "main",
                new Dictionary<string, string> { ["envs/"] = root }, client));
        Assert.Contains("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
