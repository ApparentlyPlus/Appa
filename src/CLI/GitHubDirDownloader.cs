namespace Appa;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Downloads directory subtrees from a public GitHub repo straight to local paths, no zip involved.
/// One recursive Trees call is the fast path, falling back to per-directory Contents BFS if
/// truncated. Bounded concurrency, retry/backoff, and rate-limit awareness.
/// </summary>
internal static class GitHubDirDownloader
{
    private const int Concurrency = 20;
    private const int MaxAttempts = 4;
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(5);

    private sealed record TreeEntry(string Path, string Type);

    /// <summary>
    /// Downloads every file under each requested directory (the keys of <paramref
    /// name="toLocalPath"/>) into its paired local directory, stripping the source prefix from each
    /// file's path.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, List<string>>> DownloadDirectoriesAsync(
        string owner, string repo, string @ref,
        IReadOnlyDictionary<string, string> toLocalPath,
        HttpClient client,
        CancellationToken ct = default)
    {
        // null when GitHub truncated the tree, in which case we can't trust it for anything
        var tree = await FetchTree(owner, repo, @ref, client, ct);

        var jobs = new List<(TreeEntry Entry, string Dest)>();
        var written = new Dictionary<string, List<string>>();

        foreach (var (prefix, localDir) in toLocalPath)
        {
            List<TreeEntry> matches;
            if (tree == null)
            {
                matches = await WalkContents(owner, repo, @ref, prefix.TrimEnd('/'), client, ct);
            }
            else
            {
                matches = [];
                foreach (var e in tree)
                {
                    if (e.Type == "blob" && e.Path.StartsWith(prefix, StringComparison.Ordinal))
                        matches.Add(e);
                }
            }

            if (matches.Count == 0)
                throw new InvalidOperationException(
                    $"{owner}/{repo}@{@ref} has no files under '{prefix}' - the repository layout has " +
                    "changed, or the ref is wrong; nothing was installed for it");

            var dests = new List<string>();
            foreach (var e in matches)
            {
                string rel = e.Path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.Combine(localDir, rel);
                jobs.Add((e, dest));
                dests.Add(dest);
            }
            written[prefix] = dests;
        }

        using var gate = new SemaphoreSlim(Concurrency);

        async Task Job(TreeEntry entry, string dest)
        {
            await gate.WaitAsync(ct);
            try
            {
                await DownloadFile(owner, repo, @ref, entry, dest, client, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        var running = new List<Task>();
        foreach (var (entry, dest) in jobs)
            running.Add(Job(entry, dest));
        await Task.WhenAll(running);

        return written;
    }

    /// <summary>
    /// Fetches the full recursive tree at the given ref via the Git Trees API. Returns null when the
    /// tree is truncated, since a truncated tree cannot be trusted for any directory's contents.
    /// </summary>
    private static async Task<List<TreeEntry>?> FetchTree(
        string owner, string repo, string @ref, HttpClient client, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(@ref)}?recursive=1";
        using var resp = await WithRetry(() => ApiGet(url, client, ct), ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"GitHub repo/ref not found: {owner}/{repo}@{@ref}");
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("GitHub token rejected (401) - check GITHUB_TOKEN");

        await WaitOutRateLimit(resp, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("message", out var msg))
            throw new InvalidOperationException($"GitHub tree API error for {owner}/{repo}@{@ref}: {msg.GetString()}");

        if (root.TryGetProperty("truncated", out var t) && t.GetBoolean())
            return null;

        var entries = new List<TreeEntry>();
        foreach (var item in root.GetProperty("tree").EnumerateArray())
        {
            string path = item.GetProperty("path").GetString()!;
            string type = item.GetProperty("type").GetString()!;
            entries.Add(new TreeEntry(path, type));
        }
        return entries;
    }

    /// <summary>
    /// Fallback for a truncated tree: walks the Contents API recursively, one request per
    /// directory, following "dir"-typed children. Naturally bounded per-directory, unlike the
    /// whole-repo Trees API call.
    /// </summary>
    private static async Task<List<TreeEntry>> WalkContents(
        string owner, string repo, string @ref, string dir, HttpClient client, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/contents/{dir}?ref={Uri.EscapeDataString(@ref)}";
        using var resp = await WithRetry(() => ApiGet(url, client, ct), ct);

        if (resp.StatusCode == HttpStatusCode.NotFound) return [];

        await WaitOutRateLimit(resp, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg))
            throw new InvalidOperationException($"GitHub contents API error for {owner}/{repo}@{dir}: {msg.GetString()}");

        var found = new List<TreeEntry>();
        foreach (var item in root.EnumerateArray())
        {
            string path = item.GetProperty("path").GetString()!;
            string type = item.GetProperty("type").GetString()!;

            if (type == "file")
                found.Add(new TreeEntry(path, "blob"));
            else if (type == "dir")
                found.AddRange(await WalkContents(owner, repo, @ref, path, client, ct));
        }
        return found;
    }

    /// <summary>
    /// Downloads one file's content (raw CDN for public content, with Git-LFS pointer
    /// detection/re-fetch) and writes it to disk, creating parent directories as needed.
    /// </summary>
    private static async Task DownloadFile(
        string owner, string repo, string @ref, TreeEntry entry, string dest, HttpClient client, CancellationToken ct)
    {
        byte[] bytes = await WithRetry(async () =>
        {
            string rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{@ref}/{EscapePath(entry.Path)}";
            using var resp = await client.GetAsync(rawUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? len = resp.Content.Headers.ContentLength;
            byte[] body = await resp.Content.ReadAsByteArrayAsync(ct);

            // a Git LFS pointer file is ~130 bytes of text, and the real file lives on the media CDN
            bool lfsPointer = len is >= 128 and <= 140 &&
                Encoding.UTF8.GetString(body).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal);
            if (lfsPointer)
            {
                string lfsUrl = $"https://media.githubusercontent.com/media/{owner}/{repo}/{@ref}/{EscapePath(entry.Path)}";
                using var lfs = await client.GetAsync(lfsUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                lfs.EnsureSuccessStatusCode();
                body = await lfs.Content.ReadAsByteArrayAsync(ct);
            }
            return body;
        }, ct);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllBytesAsync(dest, bytes, ct);
    }

    /// <summary>
    /// Escapes a repo-relative path for embedding in a raw.githubusercontent.com/
    /// media.githubusercontent.com URL: literal '%' and '#' first (both CDNs choke on them
    /// unescaped), then per-segment URI escaping for everything else.
    /// </summary>
    private static string EscapePath(string path)
    {
        string escaped = path.Replace("%", "%25").Replace("#", "%23");
        return string.Join('/', escaped.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// Sends a GET to the GitHub REST API, attaching a bearer token from GITHUB_TOKEN if one is set
    /// (optional - raises the rate limit from 60/hr to 5000/hr).
    /// </summary>
    private static Task<HttpResponseMessage> ApiGet(string url, HttpClient client, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("appa-compiler");

        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// If the response reports the rate limit exhausted, waits until the reset time, capped at <see
    /// cref="MaxRateLimitWait"/>, rather than failing fast.
    /// </summary>
    private static async Task WaitOutRateLimit(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) ||
            remaining.FirstOrDefault() != "0")
            return;

        if (!resp.Headers.TryGetValues("X-RateLimit-Reset", out var reset) ||
            !long.TryParse(reset.FirstOrDefault(), out long resetUnix))
            return;

        var wait = DateTimeOffset.FromUnixTimeSeconds(resetUnix) - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero) return;

        if (wait > MaxRateLimitWait)
            throw new InvalidOperationException(
                $"GitHub API rate limit exhausted; reset is {wait.TotalMinutes:F0} minutes away, which exceeds the {MaxRateLimitWait.TotalMinutes:F0}-minute wait cap");

        await Task.Delay(wait, ct);
    }

    /// <summary>
    /// Retries a transient failure a bounded number of times with exponential backoff. Does not
    /// retry the explicit 401/404 failures raised by the callers above, since those already threw
    /// before reaching a retryable state.
    /// </summary>
    private static async Task<T> WithRetry<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }
}
