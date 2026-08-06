namespace Appa;

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

internal static class Installer
{
    private static int _step, _steps;

    /// <summary>
    /// Numbers a step label.
    /// </summary>
    private static string Tag(string label) => $"({++_step}/{_steps}) {label}";

    /// <summary>
    /// Downloads and installs (or re-installs) the GatOS toolchain, libgata, template, and appa
    /// binary.
    /// </summary>
    internal static async Task RunSetup(bool isUpdate, bool? pathPref = null, bool force = false)
    {
        bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        Console.WriteLine();
        Console.WriteLine($"{C.LEAF}{(isUpdate ? "Updating" : "Installing")} appa{C.NC} {C.DIM}{AppaVersion.Current}{C.NC}");
        Console.WriteLine();
        Fmt.Table([
            ($"{C.DIM}into{C.NC}",       AppaPaths.Root),
            ($"{C.DIM}components{C.NC}", "cross-toolchain, libgata, envs, GatOS template, compiler"
                                         + (isUpdate ? " (all overwritten)" : "")),
        ]);
        if (!isUpdate && !force && Directory.Exists(AppaPaths.ToolchainDir) && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            if (!Ask($"appa is already installed at {AppaPaths.Root}. Re-download and overwrite?", defaultYes: false))
            {
                Out.Note($"{C.DIM}Install cancelled - the existing install was left untouched.{C.NC}");
                Console.WriteLine();
                return;
            }
        }

        bool elevated = Environment.IsPrivilegedProcess;
        bool wantsPath = pathPref ?? false;
        if (!isUpdate && pathPref is null && !Console.IsInputRedirected)
        {
            RecommendPath(isWin);
            wantsPath = Ask("Add appa to your PATH?", defaultYes: true);
        }

        if (wantsPath && !elevated) Elevate(isWin);

        bool linkPath = wantsPath;
        string? sudoUser = isWin ? null : Environment.GetEnvironmentVariable("SUDO_USER");
        bool restoreOwner = !isWin && !string.IsNullOrEmpty(sudoUser);

        _step = 0;
        _steps = 6 + (isWin ? 0 : 1) + (linkPath ? 1 : 0) + (restoreOwner ? 1 : 0);

        Directory.CreateDirectory(AppaPaths.ToolchainDir);
        Directory.CreateDirectory(AppaPaths.LibgataDir);
        Directory.CreateDirectory(AppaPaths.TemplateDir);
        Directory.CreateDirectory(AppaPaths.BinDir);

        Console.WriteLine();
        using var scratch = Scratch.Create("appa-install-");
        bool onPath;
        {
            string tcZip = scratch.Combine("toolchain.zip");
            DownloadWithProgress(Urls.Toolchain(), tcZip, Tag("Downloading cross-toolchain"));
            Spin.While(Tag("Extracting cross-toolchain"),
                () => ZipFile.ExtractToDirectory(tcZip, AppaPaths.ToolchainDir, true));
            File.Delete(tcZip);

            using (var ghClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                var targets = new Dictionary<string, string>
                    { ["envs/"] = AppaPaths.EnvsDir, ["libgata/"] = AppaPaths.LibgataDir };
                var written = await Spin.While(Tag("Fetching libgata and envs"),
                    GitHubDirDownloader.DownloadDirectoriesAsync(
                        Urls.GataOwner, Urls.GataRepo, Urls.GataRef, targets, ghClient));

                foreach (var (prefix, localDir) in targets)
                    PruneStale(localDir, written[prefix]);
            }

            string tmplZip = scratch.Combine("template.zip");
            DownloadWithProgress(Urls.Template, tmplZip, Tag("Downloading GatOS template"));
            Spin.While(Tag("Extracting GatOS template"),
                () => ExtractTemplate(tmplZip, AppaPaths.TemplateDir));
            File.Delete(tmplZip);

            if (!isWin)
                Spin.While(Tag("Setting executable permissions"),
                    () => Toolchain.Exec("chmod", $"-R +x \"{AppaPaths.PlatformToolchain}\"", null, silent: true));

            if (isUpdate) UpdateAppaBinary(isWin, isMac);
            else InstallSelf(isWin);

            onPath = linkPath && AddToPath(isWin);

            if (restoreOwner)
                Spin.While(Tag($"Restoring ownership to {sudoUser}"),
                    () => Toolchain.Exec("chown", $"-R {sudoUser}: \"{AppaPaths.Root}\"", null, silent: true));
        }

        Console.WriteLine();
        Console.WriteLine(isUpdate
            ? $"{C.LEAF}✓{C.NC} {C.BOLD}Up to date{C.NC} {C.DIM}- toolchain, libgata, template, and appa{C.NC}"
            : $"{C.LEAF}✓{C.NC} {C.BOLD}Installed{C.NC} {C.DIM}→{C.NC} {AppaPaths.Root}");

        if (!isUpdate)
        {
            Fmt.Section("Next steps");
            if (!onPath)
            {
                Fmt.Para($"{C.DIM}appa is not on your PATH, so invoke it as:{C.NC}");
                Out.Child(AppaPaths.AppaBin);
                Console.WriteLine();
            }
            Out.Note("appa new myos");
            Out.Note("cd myos && appa run");
        }
        Console.WriteLine();

        if (onPath) OfferSelfDelete(isWin);
    }

    /// <summary>
    /// Asks a yes/no question with a visible default, styled like the rest of the installer. The
    /// default is what a bare Enter (or an unreadable stdin) means.
    /// </summary>
    private static bool Ask(string question, bool defaultYes)
    {
        Console.Write($"  {C.FOREST}?{C.NC} {question} {C.DIM}[{(defaultYes ? "Y/n" : "y/N")}]{C.NC} ");
        string answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        return answer.Length == 0 ? defaultYes : answer is "y" or "yes";
    }

    /// <summary>
    /// Explains what being on PATH buys before asking for it. Every command in the book is written
    /// as a bare `appa ...`, so a reader who declines has to mentally rewrite all of them.
    /// </summary>
    private static void RecommendPath(bool isWin)
    {
        Fmt.Section("PATH", "(recommended)");
        Fmt.Para($"{C.DIM}Putting appa on your PATH lets you type {C.NC}appa run{C.DIM} in any project directory " +
                 $"instead of the full path to it. That is how the docs, the book, and every message appa " +
                 $"prints assume you will invoke the compiler.{C.NC}");
        Console.WriteLine();
        Fmt.Para(isWin ? $"{C.DIM}It appends this directory to the machine PATH:{C.NC}"
                       : $"{C.DIM}It creates this symlink:{C.NC}");
        Out.Child(isWin ? AppaPaths.BinDir : $"/usr/local/bin/appa {C.DIM}→{C.NC} {AppaPaths.AppaBin}");
        Console.WriteLine();
    }

    /// <summary>
    /// Re-runs the whole install elevated and exits with whatever that run returns - `sudo` on Unix,
    /// a UAC prompt on Windows. 
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Elevate(bool isWin)
    {
        Fmt.Section("Elevating");
        Fmt.Para(isWin
            ? $"{C.DIM}Adding appa to PATH edits the machine PATH, which needs Administrator. Windows will ask you to confirm.{C.NC}"
            : $"{C.DIM}Adding appa to PATH writes to /usr/local/bin, which needs root. sudo may ask for your password.{C.NC}");
        Console.WriteLine();
        string exe = Environment.ProcessPath
            ?? Cli.Fail<string>("could not determine the appa executable to re-run",
                                isWin ? "start an Administrator terminal and run 'appa install'"
                                      : "run 'sudo appa install'");
        var argv = new List<string>();
        string entry = Environment.GetCommandLineArgs()[0];
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            argv.Add(entry);
        argv.AddRange(["install", "--with-path", "--force"]);

        try
        {
            var psi = new ProcessStartInfo();
            if (isWin)
            {
                psi.FileName = exe;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                foreach (var a in argv) psi.ArgumentList.Add(a);
            }
            else
            {
                psi.FileName = "sudo";
                psi.UseShellExecute = false;
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(exe);
                foreach (var a in argv) psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("the elevated process did not start");
            proc.WaitForExit();
            Environment.Exit(proc.ExitCode);
        }
        catch (Exception ex)
        {
            Cli.Fail($"could not re-run appa with elevated privileges: {ex.Message}",
                isWin ? "accept the Windows prompt, or start an Administrator terminal and run 'appa install'"
                      : "run 'sudo appa install', or 'appa install --no-path' to install without touching PATH");
        }

        throw new UnreachableException();
    }

    /// <summary>
    /// Once appa lives in its install directory and answers to its own name, the downloaded binary
    /// the user ran this from is a stale duplicate sitting in Downloads.
    /// </summary>
    private static void OfferSelfDelete(bool isWin)
    {
        if (Console.IsInputRedirected) return;

        string self = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(self) || !File.Exists(self)) return;
        if (!Path.GetFileNameWithoutExtension(self).StartsWith("appa", StringComparison.OrdinalIgnoreCase)) return;
        string full = Path.GetFullPath(self);
        string root = Path.GetFullPath(AppaPaths.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;

        Fmt.Section("Cleanup");
        Fmt.Para($"{C.DIM}appa now lives at {C.NC}{AppaPaths.AppaBin}{C.DIM} and is on your PATH, so the copy you ran " +
                 $"this from is no longer needed:{C.NC}");
        Out.Note(full);
        Console.WriteLine();
        if (!Ask("Delete it?", defaultYes: true)) { Console.WriteLine(); return; }

        try
        {
            if (isWin)
            {
                DeferOnWindows($"Remove-Item -LiteralPath '{Escape(full)}' -Force -ErrorAction Stop");
                Out.Note($"{C.LEAF}✓{C.NC} {C.DIM}Scheduled - removed as soon as this process exits.{C.NC}");
            }
            else
            {
                File.Delete(full);
                Out.Note($"{C.LEAF}✓{C.NC} {C.DIM}Removed{C.NC} {full}");
            }
        }
        catch (Exception ex) { Log.Warn($"Could not delete {full}: {ex.Message}"); }
        Console.WriteLine();
    }

    /// <summary>
    /// Single-quotes a path for embedding in a PowerShell literal.
    /// </summary>
    private static string Escape(string path) => path.Replace("'", "''");

    /// <summary>
    /// Runs a PowerShell fragment after this process has exited, on Windows, where a running image
    /// can be neither deleted nor overwritten.
    /// </summary>
    private static void DeferOnWindows(string command)
    {
        string script =
            $"$ErrorActionPreference='Stop';" +
            $"try {{ Wait-Process -Id {Environment.ProcessId} -Timeout 120 -ErrorAction SilentlyContinue }} catch {{}};" +
            $"for ($i = 0; $i -lt 20; $i++) {{ try {{ {command}; break }} catch {{ Start-Sleep -Milliseconds 250 }} }}";

        var psi = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        Process.Start(psi);
    }

    /// <summary>
    /// True for the failures 'appa install' can hit through no fault of the compiler: the network, the
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
            "check the network connection and run 'appa install' again; the install is incomplete until it succeeds",
        UnauthorizedAccessException =>
            $"check the permissions on the path named above; if it is inside {AppaPaths.Root}, removing that directory and running 'appa install' again is the clean fix",
        InvalidDataException =>
            "the download was corrupt; run 'appa install' again to fetch it fresh",
        InvalidOperationException when ex.Message.Contains("rate limit") =>
            "set GITHUB_TOKEN to a personal access token to raise the limit from 60 to 5000 requests an hour",
        _ => "the install is incomplete; run 'appa install' again once the cause is fixed",
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
    internal static bool AddToPath(bool isWin)
    {
        string label = Tag("Adding appa to PATH");
        var sw = Stopwatch.StartNew();
        try
        {
            if (isWin)
            {
                string bin = AppaPaths.BinDir;
                string cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
                bool present = cur.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                  .Any(p => string.Equals(p.TrimEnd('\\'), bin.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (!present)
                    Environment.SetEnvironmentVariable("PATH", cur.TrimEnd(';') + ";" + bin, EnvironmentVariableTarget.Machine);
                Out.Step(label, sw.Elapsed);
                Out.Child(present
                    ? $"{C.DIM}{bin} was already there{C.NC}"
                    : $"{C.DIM}{bin} - open a new terminal for it to take effect{C.NC}");
                return true;
            }

            const string link = "/usr/local/bin/appa";
            var r = Toolchain.Exec("ln", $"-sf \"{AppaPaths.AppaBin}\" \"{link}\"", null, silent: true, capture: true);
            if (r.ExitCode != 0)
            {
                Log.Warn($"Could not create symlink {link}: {r.Stderr.Trim()}");
                return false;
            }
            Out.Step(label, sw.Elapsed);
            Out.Child($"{C.DIM}{link} → {AppaPaths.AppaBin}{C.NC}");
            return true;
        }
        catch (Exception ex) { Log.Warn($"Could not add appa to PATH: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Copies the currently-running appa binary into the bin dir (used by `appa install`).
    /// </summary>
    internal static void InstallSelf(bool isWin)
    {
        string label = Tag("Installing the appa compiler");
        var sw = Stopwatch.StartNew();

        string self = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(self) || !File.Exists(self))
        { Log.Warn("Could not locate the running appa binary to install."); return; }

        string target = AppaPaths.AppaBin;
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            Out.Step(label, sw.Elapsed);
            Out.Child($"{C.DIM}already running from {target}{C.NC}");
            return;
        }

        try
        {
            File.Copy(self, target, true);
            if (!isWin) Toolchain.Exec("chmod", $"+x \"{target}\"", null, silent: true);
            Out.Step(label, sw.Elapsed);
            Out.Child($"{C.DIM}{target}{C.NC}");
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
        var stage = Scratch.Create("appa-update-");
        bool deferred = false;
        try
        {
            string newBin = stage.Combine(isWin ? "appa.exe" : "appa");
            try { DownloadWithProgress(Urls.AppaBinary(), newBin, Tag("Downloading the latest appa")); }
            catch (Exception ex) { Log.Warn($"Could not download new appa binary: {ex.Message}"); return; }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (isWin)
            {
                DeferOnWindows(
                    $"Move-Item -LiteralPath '{Escape(newBin)}' -Destination '{Escape(target)}' -Force -ErrorAction Stop;" +
                    $"Remove-Item -LiteralPath '{Escape(stage.Path)}' -Recurse -Force -ErrorAction SilentlyContinue");
                deferred = true;
                Out.Child($"{C.DIM}it replaces {target} as soon as this process exits{C.NC}");
            }
            else
            {
                string staged = target + ".new";
                File.Copy(newBin, staged, true);
                Toolchain.Exec("chmod", $"+x \"{staged}\"", null, silent: true, capture: true);
                if (isMac)
                    try { Toolchain.Exec("xattr", $"-d com.apple.quarantine \"{staged}\"", null, silent: true, capture: true); }
                    catch { /* no quarantine attribute to clear, or no xattr */ }
                File.Move(staged, target, true);
                Out.Child($"{C.DIM}{target}{C.NC}");
            }
        }
        catch (Exception ex) { Log.Warn($"Could not install the new appa binary: {ex.Message}"); }
        finally { if (!deferred) stage.Dispose(); }
    }

    /// <summary>
    /// Downloads a URL to a local file, spinning on one line with the progress in parentheses, then
    /// leaving behind a single finished step line like every other stage of the install.
    /// </summary>
    internal static void DownloadWithProgress(string url, string dest, string label)
    {
        var sw = Stopwatch.StartNew();
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).Result;
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        using var stream = response.Content.ReadAsStream();
        using var outFile = File.Create(dest);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            outFile.Write(buffer, 0, read);
            downloaded += read;
            Spin.Tick(label, total is > 0
                ? $"({downloaded / 1048576.0:F1}/{total.Value / 1048576.0:F1} MB, {downloaded * 100 / total.Value}%)"
                : $"({downloaded / 1048576.0:F1} MB)");
        }
        Spin.Stop();
        Out.Step($"{label} {C.DIM}({downloaded / 1048576.0:F1} MB){C.NC}", sw.Elapsed);
    }

    /// <summary>
    /// Extracts the GatOS template zip into destDir, flattening GitHub's single wrapper folder and
    /// keeping only its top-level directories (src/, targets/).
    /// </summary>
    internal static void ExtractTemplate(string zipPath, string destDir)
    {
        using var staging = Scratch.Create("appa-template-");
        ZipFile.ExtractToDirectory(zipPath, staging.Path);

        var entries = Directory.GetFileSystemEntries(staging.Path);
        string root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging.Path;

        if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(root))
        {
            string dst = Path.Combine(destDir, Path.GetFileName(dir));
            Directory.CreateDirectory(dst);
            Cli.CopyDirectory(dir, dst);
        }
    }

}
