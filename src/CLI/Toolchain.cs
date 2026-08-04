namespace Appa;

using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Toolchain
{

    /// <summary>
    /// Runs an external process, optionally capturing its output and animating a spinner. Reads
    /// must drain async before waiting to avoid a full-pipe deadlock.
    /// </summary>
    internal static (int ExitCode, string Stdout, string Stderr) Exec(
        string exe, string arguments, string? workDir,
        bool silent = false, bool capture = false, string? spinner = null)
    {
        if (!silent && !capture && spinner == null)
            Console.WriteLine($"{C.BLUE}>>> {exe} {arguments}{C.NC}");

        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            WorkingDirectory = workDir ?? ""
        };

        using var proc = Process.Start(psi)!;
        var outTask = capture ? proc.StandardOutput.ReadToEndAsync() : null;
        var errTask = capture ? proc.StandardError.ReadToEndAsync() : null;

        if (spinner != null) Spin.WhileRunning(proc, spinner);
        proc.WaitForExit();
        return (proc.ExitCode, outTask?.Result ?? "", errTask?.Result ?? "");
    }

    /// <summary>
    /// Resolves the final Mem/Input/Threads/Discover capability set for a build, combining the
    /// scanned capabilities with the manifest's discovery setting.
    /// </summary>
    internal static (bool Mem, bool Input, bool Threads, bool Time, bool Discover) ResolveCaps(CapabilityScan caps, Manifest m)
    {
        bool discover = m.CapabilityDiscovery == CapabilityDiscovery.On;
        bool mem = !discover || caps.Mem, input = !discover || caps.Input, threads = !discover || caps.Threads;
        bool time = !discover || caps.Time;

        // Threads pull in the whole multitasking stack, which allocates internally.
        mem = mem || threads;

        // The dashboard keyboard cycling is wired through the keyboard IRQ, which only
        // exists under GATA_CAP_INPUT: THREADS implies INPUT.
        input = input || threads;

        // USB hotplug watch runs as its own kernel thread: HOTPLUG implies THREADS.
        if (m.Keyboard == Keyboard.Hotplug) threads = true;

        // xHCI device enumeration allocates heap structures even for a one-time scan.
        if (m.Keyboard is Keyboard.External or Keyboard.Hotplug) { mem = true; input = true; }

        // ACPI/APIC/timer tick are needed whenever the scheduler (THREADS) or keyboard
        // (INPUT) needs IRQ routing; all three map tables/MMIO through the VMM.
        if (threads || input) mem = mem || threads || input;

        // The time source (get_uptime_ns) is the timer subsystem, which only ticks when
        // the interrupt subsystem is up - TIME implies the same ACPI/APIC/heap floor.
        if (time) mem = true;

        return (mem, input, threads, time, discover);
    }

    /// <summary>
    /// Returns the -D macros for the GatOS gcc build, representing the resolved capability set.
    /// MEM/INPUT/THREADS are inferred; FRAMEBUFFER and keyboard level come from the manifest.
    /// </summary>
    internal static List<string> CapabilityDefines(CapabilityScan caps, Manifest m)
    {
        var r = ResolveCaps(caps, m);

        var d = new List<string>();
        if (r.Mem) d.Add("-DGATA_CAP_MEM");
        if (r.Input) d.Add("-DGATA_CAP_INPUT");
        if (r.Threads) { d.Add("-DGATA_CAP_THREADS"); d.Add("-DGATA_RC_ATOMIC=1"); }
        if (r.Time) d.Add("-DGATA_CAP_TIME");
        d.Add(m.Output == Output.Serial ? "-DGATA_OUTPUT_SERIAL" : "-DGATA_CAP_FRAMEBUFFER");
        d.Add(m.Keyboard switch
        {
            Keyboard.External => "-DGATA_KBD_EXTERNAL",
            Keyboard.Hotplug => "-DGATA_KBD_HOTPLUG",
            _ => "-DGATA_KBD_DEFAULT",
        });

        return d;
    }

    /// <summary>
    /// Returns the human-readable capability summary printed before "Finished".
    /// </summary>
    internal static string CapabilitiesNote(CapabilityScan caps, Manifest m)
    {
        var r = ResolveCaps(caps, m);

        var on = new List<string>();
        if (r.Mem) on.Add("mem"); if (r.Input) on.Add("input"); if (r.Threads) on.Add("threads"); if (r.Time) on.Add("time");
        on.Add(m.Output == Output.Serial ? "serial" : "framebuffer");
        string suffix = r.Discover ? "" : " (discovery off: assumed, not inferred)";
        return $"Capabilities: {string.Join(" ", on)}{suffix} (keyboard={m.Keyboard.ToString().ToLowerInvariant()})";
    }

    /// <summary>
    /// Stages the GatOS template, compiles and links the kernel, builds the ISO, copies it to the
    /// project build dir, and optionally runs QEMU.
    /// </summary>
    internal static void BuildGatOSImage(IReadOnlyList<OutputFile> output, Manifest manifest,
                                string projectRoot, List<string> defines, string capsNote,
                                bool doRun, bool headless, int? timeout)
    {
        if (!Directory.Exists(AppaPaths.TemplateDir) || !Directory.GetDirectories(AppaPaths.TemplateDir).Any())
            Cli.Fail("GatOS template not found. Run 'appa setup' first.");
        if (!File.Exists(AppaPaths.Gcc()))
            Cli.Fail("Toolchain not found. Run 'appa setup' first.");

        string buildDir = Path.Combine(Path.GetTempPath(),
            $"appa-build-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        Directory.CreateDirectory(buildDir);
        var total = Stopwatch.StartNew();

        try
        {
            Spin.Step("Prepared build workspace", () => Cli.CopyDirectory(AppaPaths.TemplateDir, buildDir));

            string kernelSrcDir = Path.Combine(buildDir, "src", "kernel");
            Directory.CreateDirectory(kernelSrcDir);
            Cli.WriteOutputs(output, kernelSrcDir);

            string targetsDir = Path.Combine(buildDir, "targets", "x86_64");
            if (!File.Exists(Path.Combine(targetsDir, "linker.ld")))
                Cli.Fail("Template is missing targets/x86_64/linker.ld.");

            var srcDir = Path.Combine(buildDir, "src");
            var objDir = Path.Combine(buildDir, "build");
            var distDir = Path.Combine(buildDir, "dist", "x86_64");
            var isoDir = Path.Combine(buildDir, "targets", "x86_64", "iso");
            Directory.CreateDirectory(objDir);
            Directory.CreateDirectory(distDir);

            var cFiles = Directory.GetFiles(srcDir, "*.c", SearchOption.AllDirectories).ToList();
            var asmFiles = Directory.GetFiles(srcDir, "*.S", SearchOption.AllDirectories).ToList();
            var objFiles = CompileAll(cFiles, asmFiles, srcDir, objDir, manifest.Mode, defines);

            string kernelBin = Path.Combine(distDir, "kernel.bin");
            LinkKernel(objFiles, kernelBin, targetsDir);

            string isoPath = MakeIso(kernelBin, isoDir, distDir);

            string projectBuildDir = Path.Combine(projectRoot, "build");
            Directory.CreateDirectory(projectBuildDir);
            string outIso = Path.Combine(projectBuildDir, Path.GetFileName(isoPath));
            File.Copy(isoPath, outIso, true);
            Out.Note(capsNote);
            Console.WriteLine();
            Console.WriteLine($"{C.BOLD}Finished{C.NC} in {Spin.Fmt(total.Elapsed)} {C.DIM}→{C.NC} {outIso}");

            if (doRun)
            {
                string artifactsDir = Path.Combine(projectRoot, "artifacts");
                Directory.CreateDirectory(artifactsDir);
                RunQemu(outIso, artifactsDir, headless, timeout);
            }
        }
        finally { try { Directory.Delete(buildDir, true); } catch { } }
    }

    /// <summary>
    /// Returns true if the given translation unit path belongs to the userspace realm rather than
    /// the kernel.
    /// </summary>
    internal static bool IsUserspace(string rel) =>
        rel.StartsWith("ulibc/") || rel == GeneratedUserTu;

    /// <summary>
    /// The one userspace translation unit appa generates rather than GatOS shipping: the lowered
    /// Gata of the userspace realm. Flags that change language semantics must not be applied here.
    /// </summary>
    internal const string GeneratedUserTu = "kernel/uproc.c";

    /// <summary>
    /// The compiler flags for one GatOS translation unit. Split out from the compile loop so the
    /// choices below can be asserted without a toolchain present - the flags that matter most here
    /// are the ones whose effect is invisible in a build that succeeds.
    /// </summary>
    internal static List<string> CFlagsFor(
        string rel, string srcDir, IEnumerable<string> modeFlags, IEnumerable<string> defines, bool isMac)
    {
        var cflags = new List<string>(GatosFlags.Common) { $"-I{srcDir}" };
        cflags.AddRange(modeFlags);
        cflags.AddRange(defines);
        if (!IsUserspace(rel))
        {
            if (!isMac) cflags.Add("-flto");
            if (GatosFlags.InterruptPath.Contains(rel)) cflags.AddRange(GatosFlags.FpuRestrictions);
        }
        return cflags;
    }

    /// <summary>
    /// Compiles all C and assembly files in parallel, reporting in-place progress. On the first
    /// failure, stops scheduling new jobs and prints the failure block.
    /// </summary>
    internal static List<string> CompileAll(List<string> cFiles, List<string> asmFiles,
                                   string srcDir, string objDir, Mode mode, List<string> defines)
    {
        var modeFlags = GatosFlags.For(mode);
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var jobs = new List<(string src, string obj, string[] flags)>();

        foreach (var src in cFiles)
        {
            string rel = Path.GetRelativePath(srcDir, src).Replace('\\', '/');
            string obj = Path.Combine(objDir, rel.Replace('/', '_') + ".o");
            jobs.Add((src, obj, [.. CFlagsFor(rel, srcDir, modeFlags, defines, isMac)]));
        }

        foreach (var src in asmFiles)
        {
            string rel = Path.GetRelativePath(srcDir, src);
            string obj = Path.Combine(objDir, rel.Replace(Path.DirectorySeparatorChar, '_') + ".o");
            var asmFlags = new List<string> { $"-I{srcDir}", "-D__ASSEMBLER__" };
            asmFlags.AddRange(defines);
            jobs.Add((src, obj, asmFlags.ToArray()));
        }

        (string Name, string Stderr)? failure = null;
        object gate = new();
        int completed = 0, total = jobs.Count;
        var sw = Stopwatch.StartNew();
        bool tty = !Console.IsOutputRedirected;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (job, state) =>
            {
                if (failure != null) { state.Stop(); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(job.obj)!);
                var result = Exec(AppaPaths.Gcc(), $"-c {string.Join(' ', job.flags.Select(f => $"\"{f}\""))} \"{job.src}\" -o \"{job.obj}\"", null, silent: true, capture: true);
                string name = Path.GetFileName(job.src);
                lock (gate)
                {
                    if (failure != null) { state.Stop(); return; }
                    if (result.ExitCode != 0)
                    {
                        failure = (name, result.Stderr.TrimEnd());
                        state.Stop();
                    }
                    else
                    {
                        completed++;
                        if (tty) Out.Redraw($"  {C.DIM}⠿ Compiling [{completed}/{total}] {name}{C.NC}");
                    }
                }
            });

        if (failure != null)
        {
            if (tty) Out.ClearRedraw();
            Console.Error.WriteLine($"{C.RED}failed:{C.NC} {failure.Value.Name}");
            Console.Error.WriteLine(failure.Value.Stderr);
            Environment.Exit(1);
        }

        if (tty) Out.ClearRedraw();
        Spin.Done($"Compiled {total} files", sw.Elapsed);
        return [.. jobs.Select(j => j.obj)];
    }

    /// <summary>
    /// Links all object files into a kernel.bin using the cross-gcc linker script. macOS links with
    /// ld directly (no LTO); every other host links through cross-gcc with LTO.
    /// </summary>
    internal static void LinkKernel(List<string> objFiles, string kernelBin, string targetsDir)
    {
        string linkerScript = Path.Combine(targetsDir, "linker.ld");
        string objList = string.Join(' ', objFiles.Select(o => $"\"{o}\""));
        var sw = Stopwatch.StartNew();

        var r = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Exec(AppaPaths.Gcc("x86_64-elf-ld"),
                $"-n -nostdlib --gc-sections -T\"{linkerScript}\" --no-relax -g -o \"{kernelBin}\" {objList}",
                null, capture: true, spinner: "Linking kernel.bin")
            : Exec(AppaPaths.Gcc(),
                $"-nostdlib -flto -g -Wl,-n,--gc-sections,--no-relax,-T\"{linkerScript}\" -o \"{kernelBin}\" {objList}",
                null, capture: true, spinner: "Linking kernel.bin");
        if (r.ExitCode != 0) { Log.Error($"Link failed:\n{r.Stderr}"); Environment.Exit(1); }

        Exec(AppaPaths.Gcc("x86_64-elf-strip"), $"\"{kernelBin}\"", null, capture: true);
        Spin.Done("Linked kernel.bin", sw.Elapsed);
    }

    /// <summary>
    /// Creates a bootable ISO from a kernel.bin using grub-mkstandalone and grub-mkrescue. Returns
    /// the path to the created ISO.
    /// </summary>
    internal static string MakeIso(string kernelBin, string isoDir, string distDir)
    {
        string bootDir = Path.Combine(isoDir, "boot");
        string uefiDir = Path.Combine(isoDir, "EFI", "BOOT");
        string grubCfg = Path.Combine(isoDir, "boot", "grub", "grub.cfg");
        Directory.CreateDirectory(bootDir);
        Directory.CreateDirectory(uefiDir);
        var sw = Stopwatch.StartNew();

        File.Copy(kernelBin, Path.Combine(bootDir, "kernel.bin"), true);

        string uefiGrub = Path.Combine(uefiDir, "BOOTX64.EFI");
        var r1 = Exec(AppaPaths.GrubTool("grub-mkstandalone"),
            $"--directory=\"{Path.Combine(AppaPaths.GrubDir, "x86_64-efi")}\" " +
            $"--format=x86_64-efi --output=\"{uefiGrub}\" --locales= --fonts= " +
            $"\"boot/grub/grub.cfg={grubCfg}\"", null, capture: true, spinner: "Creating ISO image");
        if (r1.ExitCode != 0) { Log.Error($"grub-mkstandalone failed:\n{r1.Stderr}"); Environment.Exit(1); }

        string isoOut = Path.Combine(distDir, "GatOS.iso");
        string mkrescueArgs =
            $"--xorriso=\"{AppaPaths.XorrisoExe}\" --fonts=unicode --themes= -o \"{isoOut}\" \"{isoDir}\"";

        var r2 = Exec(AppaPaths.GrubTool("grub-mkrescue"), mkrescueArgs,
            AppaPaths.GrubDir, capture: true, spinner: "Creating ISO image");
        if (r2.ExitCode != 0) { Log.Error($"grub-mkrescue failed:\n{r2.Stderr}"); Environment.Exit(1); }

        Spin.Done("Created ISO image", sw.Elapsed);
        return isoOut;
    }

    /// <summary>
    /// Launches QEMU with the given ISO and waits for it to exit, optionally with a timeout.
    /// </summary>
    internal static void RunQemu(string isoPath, string artifactsDir, bool headless, int? timeout)
    {
        Console.WriteLine($"Running QEMU [{(headless ? "headless" : "GUI")}]...");
        string debugLog = Path.Combine(artifactsDir, "debug.log");
        string userDebugLog = Path.Combine(artifactsDir, "user-debug.log");
        var qemuArgs = new System.Text.StringBuilder();
        qemuArgs.Append($"-cdrom \"{isoPath}\"");

        // COM1 (mon:stdio - boot markers + serial output), COM2 (GatOS debug.log),
        // COM3 (userspace debug channel).
        qemuArgs.Append($" -serial mon:stdio");
        qemuArgs.Append($" -serial \"file:{debugLog}\"");
        qemuArgs.Append($" -serial \"file:{userDebugLog}\"");
        qemuArgs.Append($" -cpu kvm64,+smep,+smap");
        if (headless) qemuArgs.Append(" -nographic");

        string exe = AppaPaths.QemuExe;
        string finalArgs = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                           exe.EndsWith(".AppImage")
            ? $"qemu-system-x86_64 {qemuArgs}"
            : qemuArgs.ToString();

        var psi = new ProcessStartInfo(exe, finalArgs)
        {
            UseShellExecute = false, RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        using var proc = Process.Start(psi)!;
        if (timeout.HasValue)
        {
            bool exited = proc.WaitForExit(timeout.Value * 1000);
            if (!exited) { try { proc.Kill(entireProcessTree: true); } catch { } }
            Log.Info("QEMU session ended.");
        }
        else proc.WaitForExit();
    }

}
