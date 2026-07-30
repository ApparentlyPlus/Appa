namespace Appa;

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

/// <summary>
/// The 'appa setup' / 'appa update' installer: downloads the GatOS bundle and libgata, extracts the
/// project template, installs the appa binary, and (optionally, elevated) puts it on PATH.
/// </summary>
internal static class Installer
{

    /// <summary>
    /// Downloads and installs (or re-installs) the GatOS toolchain, libgata, template, and appa
    /// binary.
    /// </summary>
    internal static async Task RunSetup(bool isUpdate)
    {
        Log.Info(isUpdate
            ? "Updating appa toolchain, libgata, and template (overwriting existing)..."
            : "Setting up appa toolchain and resources...");
        Log.Info($"Installation directory: {AppaPaths.Root}");

        // Re-running setup re-downloads everything. If already installed, confirm first
        // (interactive only; `update` is always intentional).
        if (!isUpdate && Directory.Exists(AppaPaths.ToolchainDir) && !Console.IsInputRedirected)
        {
            Console.Write($"{C.YELLOW}appa is already installed at {AppaPaths.Root}. Re-download and overwrite? [y/N]: {C.NC}");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            { Log.Info("Setup cancelled - existing install left untouched."); return; }
        }

        bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        bool wantsPath = false;
        if (!isUpdate && !Console.IsInputRedirected)
        {
            Console.Write($"{C.CYAN}Add appa to your PATH so you can run it from anywhere? [y/N]: {C.NC}");
            wantsPath = Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
            if (wantsPath && !Environment.IsPrivilegedProcess)
            {
                Log.Warn("Adding appa to PATH needs elevated privileges.");
                Log.Info(isWin
                    ? "Re-run 'appa setup' from an Administrator terminal."
                    : "Re-run 'sudo appa setup'.");
                Environment.Exit(1);
            }
        }

        Directory.CreateDirectory(AppaPaths.ToolchainDir);
        Directory.CreateDirectory(AppaPaths.LibgataDir);
        Directory.CreateDirectory(AppaPaths.TemplateDir);
        Directory.CreateDirectory(AppaPaths.BinDir);

        string tcZip = Path.Combine(Path.GetTempPath(), "appa_tc.zip");
        DownloadWithProgress(Urls.Toolchain(), tcZip, "toolchain");
        Log.Step("Extracting toolchain...");
        ZipFile.ExtractToDirectory(tcZip, AppaPaths.ToolchainDir, true);
        File.Delete(tcZip);
        Log.Step("Fetching libgata and envs from GitHub...");
        using (var ghClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            var targets = new Dictionary<string, string>
                { ["envs/"] = AppaPaths.EnvsDir, ["libgata/"] = AppaPaths.LibgataDir };
            var written = await GitHubDirDownloader.DownloadDirectoriesAsync(
                Urls.GataOwner, Urls.GataRepo, Urls.GataRef, targets, ghClient);

            foreach (var (prefix, localDir) in targets)
                PruneStale(localDir, written[prefix]);
        }

        string tmplZip = Path.Combine(Path.GetTempPath(), "appa_template.zip");
        DownloadWithProgress(Urls.Template, tmplZip, "GatOS template");
        Log.Step("Extracting GatOS template...");
        ExtractTemplate(tmplZip, AppaPaths.TemplateDir);
        File.Delete(tmplZip);

        if (!isWin)
        {
            Log.Step("Setting executable permissions...");
            Toolchain.Exec("chmod", $"-R +x \"{AppaPaths.PlatformToolchain}\"", null, silent: true);
        }

        if (isUpdate)
            UpdateAppaBinary(isWin, isMac);
        else
            InstallSelf(isWin);

        if (wantsPath && Environment.IsPrivilegedProcess)
            AddToPath(isWin);

        if (!isWin)
        {
            string? sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
            if (!string.IsNullOrEmpty(sudoUser))
            {
                Log.Step($"Restoring ownership of installation files to {sudoUser}...");
                Toolchain.Exec("chown", $"-R {sudoUser}: \"{AppaPaths.Root}\"", null, silent: true);
            }
        }

        Log.Ok(isUpdate
            ? "Update complete. Toolchain, libgata, template, and appa are now up to date."
            : "Setup complete. Run 'appa init <project>' to create a new project.");
    }

    /// <summary>
    /// True for the failures 'appa setup' can hit through no fault of the compiler: the network, the
    /// GitHub API, a corrupt download, or the filesystem it installs into.
    /// </summary>
    internal static bool IsExpectedSetupFailure(Exception ex) =>
        ex is HttpRequestException
              or TaskCanceledException
              or InvalidOperationException
              or InvalidDataException
              or UnauthorizedAccessException
              or IOException;

    /// <summary>
    /// What to try next, chosen by what actually went wrong. Every one of these leaves the install
    /// partly written, so re-running setup is the recovery in all cases and the hint says so.
    /// </summary>
    internal static string SetupFailureHint(Exception ex) => ex switch
    {
        HttpRequestException or TaskCanceledException =>
            "check the network connection and run 'appa setup' again; the install is incomplete until it succeeds",
        UnauthorizedAccessException =>
            $"appa could not write to {AppaPaths.Root} - check its permissions, or remove it and run 'appa setup' again",
        InvalidDataException =>
            "the download was corrupt; run 'appa setup' again to fetch it fresh",
        InvalidOperationException when ex.Message.Contains("rate limit") =>
            "set GITHUB_TOKEN to a personal access token to raise the limit from 60 to 5000 requests an hour",
        _ => "the install is incomplete; run 'appa setup' again once the cause is fixed",
    };

    /// <summary>
    /// Deletes .g files in a downloaded directory that the download did not write.
    /// </summary>
    internal static void PruneStale(string localDir, IReadOnlyList<string> written)
    {
        if (!Directory.Exists(localDir)) return;
        var keep = new HashSet<string>(written.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.GetFiles(localDir, "*.g", SearchOption.AllDirectories))
        {
            if (keep.Contains(Path.GetFullPath(path))) continue;
            try
            {
                File.Delete(path);
                Log.Info($"Removed {Path.GetFileName(path)} - no longer part of libgata");
            }
            catch (Exception ex) { Log.Warn($"Could not remove stale {path}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Adds the appa bin directory to the system PATH. Unix creates a symlink in /usr/local/bin;
    /// Windows appends to the machine PATH variable. Requires elevated privileges (checked by
    /// caller).
    /// </summary>
    internal static void AddToPath(bool isWin)
    {
        try
        {
            if (isWin)
            {
                string bin = AppaPaths.BinDir;
                string cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
                bool present = cur.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                  .Any(p => string.Equals(p.TrimEnd('\\'), bin.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (present) { Log.Info("appa's bin directory is already on PATH."); return; }
                Environment.SetEnvironmentVariable("PATH", cur.TrimEnd(';') + ";" + bin, EnvironmentVariableTarget.Machine);
                Log.Ok($"Added {bin} to the system PATH. Open a new terminal for it to take effect.");
            }
            else
            {
                const string link = "/usr/local/bin/appa";
                var r = Toolchain.Exec("ln", $"-sf \"{AppaPaths.AppaBin}\" \"{link}\"", null, silent: true, capture: true);
                if (r.ExitCode == 0)
                    Log.Ok($"Linked {link} → {AppaPaths.AppaBin}. 'appa' is now on your PATH.");
                else
                    Log.Warn($"Could not create symlink {link}: {r.Stderr.Trim()}");
            }
        }
        catch (Exception ex) { Log.Warn($"Could not add appa to PATH: {ex.Message}"); }
    }

    /// <summary>
    /// Copies the currently-running appa binary into the bin dir (used by `appa setup`).
    /// </summary>
    internal static void InstallSelf(bool isWin)
    {
        string self = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(self) || !File.Exists(self))
        { Log.Warn("Could not locate the running appa binary to install."); return; }

        string target = AppaPaths.AppaBin;
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Log.Step("Installing appa binary...");
            File.Copy(self, target, true);
            if (!isWin) Toolchain.Exec("chmod", $"+x \"{target}\"", null, silent: true);
            Log.Info($"appa installed to {target}");
        }
        catch (Exception ex) { Log.Warn($"Could not install appa binary: {ex.Message}"); }
    }

    /// <summary>
    /// Downloads the latest appa binary and swaps it in after this process exits. The replacement
    /// is deferred to a detached process because the installed binary may be the one currently
    /// running.
    /// </summary>
    internal static void UpdateAppaBinary(bool isWin, bool isMac)
    {
        string target = AppaPaths.AppaBin;
        string newBin = Path.Combine(Path.GetTempPath(), isWin ? "appa_new.exe" : "appa_new");

        try { DownloadWithProgress(Urls.AppaBinary(), newBin, "appa"); }
        catch (Exception ex) { Log.Warn($"Could not download new appa binary: {ex.Message}"); return; }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            if (isWin)
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & move /Y \"{newBin}\" \"{target}\"")
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi);
            }
            else
            {
                string script = $"sleep 2; mv -f '{newBin}' '{target}'; chmod +x '{target}'; ";
                if (isMac) script += $"xattr -d com.apple.quarantine '{target}' 2>/dev/null; ";
                script += "true";
                var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);
                Process.Start(psi);
            }
            Log.Info("Downloaded the latest appa; it will replace the installed binary momentarily.");
        }
        catch (Exception ex) { Log.Warn($"Could not schedule appa self-update: {ex.Message}"); }
    }

    /// <summary>
    /// Downloads a URL to a local file, printing a progress bar or byte counter while downloading.
    /// </summary>
    internal static void DownloadWithProgress(string url, string dest, string name)
    {
        using var client = new System.Net.Http.HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var response = client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).Result;
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        using var stream = response.Content.ReadAsStream();
        using var outFile = File.Create(dest);
        var buffer = new byte[81920];
        const string spin = @"|/-\";
        long downloaded = 0;
        int read, ticks = 0;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            outFile.Write(buffer, 0, read);
            downloaded += read;
            if (total is > 0)
            {
                int pct = (int)(downloaded * 100 / total.Value);
                int filled = pct * 40 / 100;
                string bar = new string('=', filled) + new string(' ', 40 - filled);
                Out.Redraw($"{name}  |{bar}| {pct}% ({downloaded/1048576.0:F1}/{total.Value/1048576.0:F1} MB)");
            }
            else
                Out.Redraw($"{name}  {spin[ticks++ % 4]} {downloaded/1048576.0:F1} MB");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Extracts the GatOS template zip into destDir, flattening GitHub's single wrapper folder and
    /// keeping only its top-level directories (src/, targets/).
    /// </summary>
    internal static void ExtractTemplate(string zipPath, string destDir)
    {
        string staging = Path.Combine(Path.GetTempPath(), $"appa-tmpl-stage-{Environment.ProcessId}");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zipPath, staging);

        var entries = Directory.GetFileSystemEntries(staging);
        string root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;

        if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(root))
        {
            string dst = Path.Combine(destDir, Path.GetFileName(dir));
            Directory.CreateDirectory(dst);
            Cli.CopyDirectory(dir, dst);
        }
        Directory.Delete(staging, true);
    }

}
