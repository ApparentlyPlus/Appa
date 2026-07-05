namespace Appa;

using System.Net.Http;
using System.Text.Json;

/// <summary>
/// Downloads one or more directory subtrees from a public GitHub repo at a given ref,
/// writing files straight to local paths with each source prefix stripped - no zip
/// involved. Uses the Trees API (one recursive call, shared across every requested
/// directory) as the fast path, falling back to the Contents API (per-directory BFS)
/// only if the tree response is truncated. Bounded concurrency, small retry/backoff,
/// and GitHub rate-limit awareness on api.github.com calls.
/// </summary>
internal static class GitHubDirDownloader
{
    private const int Concurrency = 20;
    private const int MaxAttempts = 4;
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(5);

    private sealed record TreeEntry(string Path, string Type);

    /// <summary>
    /// Downloads every file under each requested directory (keys of
    /// <paramref name="directoryToLocalPath"/>, e.g. "envs/") into its paired local
    /// directory, stripping the source prefix from each file's path.
    /// </summary>
    public static async Task DownloadDirectoriesAsync(
        string owner, string repo, string @ref,
        IReadOnlyDictionary<string, string> directoryToLocalPath,
        HttpClient client,
        CancellationToken ct = default)
    {
        var (truncated, entries) = await FetchTreeAsync(owner, repo, @ref, client, ct);

        var files = new List<(TreeEntry Entry, string LocalPath)>();
        foreach (var (prefix, localDir) in directoryToLocalPath)
        {
            IReadOnlyList<TreeEntry> matches = truncated
                ? await WalkContentsApiAsync(owner, repo, @ref, prefix.TrimEnd('/'), client, ct)
                : entries.Where(e => e.Type == "blob" && e.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();

            foreach (var entry in matches)
                files.Add((entry, Path.Combine(localDir, entry.Path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar))));
        }

        using var gate = new SemaphoreSlim(Concurrency);
        var tasks = files.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try { await DownloadFileAsync(owner, repo, @ref, f.Entry, f.LocalPath, client, ct); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Fetches the full recursive tree at the given ref via the Git Trees API.
    /// Returns (truncated, entries) - entries is empty when truncated, since a
    /// truncated tree cannot be trusted for any directory's contents.
    /// </summary>
    private static async Task<(bool Truncated, List<TreeEntry> Entries)> FetchTreeAsync(
        string owner, string repo, string @ref, HttpClient client, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(@ref)}?recursive=1";
        using var response = await WithRetryAsync(() => SendApiRequestAsync(url, client, ct), ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"GitHub repo/ref not found: {owner}/{repo}@{@ref}");
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("GitHub token rejected (401) - check GITHUB_TOKEN");
        await RespectRateLimitAsync(response, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("message", out var msg))
            throw new InvalidOperationException($"GitHub tree API error for {owner}/{repo}@{@ref}: {msg.GetString()}");

        bool truncated = root.TryGetProperty("truncated", out var t) && t.GetBoolean();
        if (truncated) return (true, []);

        var list = new List<TreeEntry>();
        foreach (var item in root.GetProperty("tree").EnumerateArray())
            list.Add(new TreeEntry(item.GetProperty("path").GetString()!, item.GetProperty("type").GetString()!));
        return (false, list);
    }

    /// <summary>
    /// Fallback for a truncated tree: walks the Contents API recursively, one request
    /// per directory, following "dir"-typed children. Naturally bounded per-directory,
    /// unlike the whole-repo Trees API call.
    /// </summary>
    private static async Task<List<TreeEntry>> WalkContentsApiAsync(
        string owner, string repo, string @ref, string directory, HttpClient client, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/contents/{directory}?ref={Uri.EscapeDataString(@ref)}";
        using var response = await WithRetryAsync(() => SendApiRequestAsync(url, client, ct), ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        await RespectRateLimitAsync(response, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg))
            throw new InvalidOperationException($"GitHub contents API error for {owner}/{repo}@{directory}: {msg.GetString()}");

        var result = new List<TreeEntry>();
        foreach (var item in root.EnumerateArray())
        {
            string path = item.GetProperty("path").GetString()!;
            string type = item.GetProperty("type").GetString()!;
            if (type == "file") result.Add(new TreeEntry(path, "blob"));
            else if (type == "dir") result.AddRange(await WalkContentsApiAsync(owner, repo, @ref, path, client, ct));
        }
        return result;
    }

    /// <summary>
    /// Downloads one file's content (raw CDN for public content, with Git-LFS pointer
    /// detection/re-fetch) and writes it to disk, creating parent directories as needed.
    /// </summary>
    private static async Task DownloadFileAsync(
        string owner, string repo, string @ref, TreeEntry entry, string destPath, HttpClient client, CancellationToken ct)
    {
        byte[] bytes = await WithRetryAsync(async () =>
        {
            string rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{@ref}/{EscapePath(entry.Path)}";
            using var response = await client.GetAsync(rawUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? len = response.Content.Headers.ContentLength;
            byte[] data = await response.Content.ReadAsByteArrayAsync(ct);

            if (len is >= 128 and <= 140)
            {
                string text = System.Text.Encoding.UTF8.GetString(data);
                if (text.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
                {
                    string lfsUrl = $"https://media.githubusercontent.com/media/{owner}/{repo}/{@ref}/{EscapePath(entry.Path)}";
                    using var lfsResponse = await client.GetAsync(lfsUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    lfsResponse.EnsureSuccessStatusCode();
                    data = await lfsResponse.Content.ReadAsByteArrayAsync(ct);
                }
            }
            return data;
        }, ct);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await File.WriteAllBytesAsync(destPath, bytes, ct);
    }

    /// <summary>
    /// Escapes a repo-relative path for embedding in a raw.githubusercontent.com/
    /// media.githubusercontent.com URL: literal '%' and '#' first (both CDNs choke on
    /// them unescaped), then per-segment URI escaping for everything else.
    /// </summary>
    private static string EscapePath(string path)
    {
        string escaped = path.Replace("%", "%25").Replace("#", "%23");
        return string.Join('/', escaped.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// Sends a GET to the GitHub REST API, attaching a bearer token from GITHUB_TOKEN
    /// if one is set (optional - raises the rate limit from 60/hr to 5000/hr).
    /// </summary>
    private static Task<HttpResponseMessage> SendApiRequestAsync(string url, HttpClient client, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("appa-compiler");
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// If the GitHub API response reports the rate limit is exhausted, waits until the
    /// reset time (capped at <see cref="MaxRateLimitWait"/>) rather than failing fast.
    /// </summary>
    private static async Task RespectRateLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)) return;
        if (remainingValues.FirstOrDefault() != "0") return;
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)) return;
        if (!long.TryParse(resetValues.FirstOrDefault(), out long resetUnix)) return;

        var wait = DateTimeOffset.FromUnixTimeSeconds(resetUnix) - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero) return;
        if (wait > MaxRateLimitWait)
            throw new InvalidOperationException(
                $"GitHub API rate limit exhausted; reset is {wait.TotalMinutes:F0} minutes away, which exceeds the {MaxRateLimitWait.TotalMinutes:F0}-minute wait cap");
        await Task.Delay(wait, ct);
    }

    /// <summary>
    /// Retries a transient failure a bounded number of times with exponential backoff.
    /// Does not retry the explicit 401/404 failures raised by the callers above, since
    /// those already threw before reaching a retryable state.
    /// </summary>
    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (Exception) when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }
}
