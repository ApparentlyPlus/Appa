namespace Appa;

using System.Xml.Linq;

// What a build produces / how it is hosted.
//   GatOS - a bootable ISO (the kernel target).
//   Hosted - C against the libc platform (the test/ASAN harness target).
enum Target { GatOS, Hosted }

// Build mode. Debug allows the diagnostic floor (debug/panic) and ships unoptimized
// with debug info; Release strips diagnostics and optimizes.
enum Mode { Debug, Release }

// Where Console output is routed. Framebuffer (default) drives the FRAMEBUFFER
// capability; Serial routes to the serial port instead (no framebuffer subsystem).
enum Output { Framebuffer, Serial }

// Keyboard support level - explicitly chosen, passed through to GatOS as-is.
//   Default - PS/2 only (laptops)
//   External - PS/2 plus external (USB) keyboards
//   Hotplug - PS/2 plus external keyboards with hotplug
enum Keyboard { Default, External, Hotplug }

// On (default): MEM/INPUT/THREADS are inferred from the program by CapabilityScan,
// so the image only carries the subsystems it actually uses. Off: skip inference
// and assume all three are needed - the escape valve for a native blind spot
// CapabilityScan can't see through (a raw native{} body that touches a capability
// with no Gata-visible call), where inference would otherwise under-declare.
enum CapabilityDiscovery { On, Off }

// A project's build configuration, read from its <project>.gconf. The lean source of
// truth: what to build (target), how (mode), and the explicitly-chosen knobs (output
// routing, keyboard level, whether to trust capability inference). gcc flags,
// env/entry paths, and stdlib selection are NOT here - appa owns the flags, the
// environment is auto-discovered (@environment), and the entry is the src/main.g
// convention.
sealed record Manifest(
    string Dir,
    string ProjectName,
    Target Target,
    Mode Mode,
    Output Output,
    Keyboard Keyboard,
    CapabilityDiscovery CapabilityDiscovery);

static class ManifestReader
{
    /// <summary>
    /// Locates the single *.gconf in a directory.
    /// Returns null if none exists, and throws ManifestError if more than one is found.
    /// </summary>
    public static string? Discover(string dir)
    {
        var found = Directory.GetFiles(dir, "*.gconf");
        if (found.Length == 0) return null;
        if (found.Length > 1)
            throw new ManifestError($"multiple .gconf files in {dir}; expected exactly one");
        return found[0];
    }

    /// <summary>
    /// Parses a .gconf file and returns its Manifest.
    /// Throws ManifestError on malformed or missing required elements.
    /// </summary>
    public static Manifest Load(string path)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (Exception ex) { throw new ManifestError($"cannot read {Path.GetFileName(path)}: {ex.Message}"); }

        var root = doc.Root ?? throw new ManifestError($"{Path.GetFileName(path)} is empty");
        if (root.Name.LocalName != "appa")
            throw new ManifestError($"{Path.GetFileName(path)} must have an <appa> root, got <{root.Name.LocalName}>");
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        Target target = ParseEnum<Target>(root, "TargetBackend", Target.GatOS);
        Mode mode = ParseEnum<Mode>(root, "BuildMode", Mode.Debug);
        Output output = ParseEnum<Output>(root, "OutputType", Output.Framebuffer);
        Keyboard keyboard = ParseEnum<Keyboard>(root, "KeyboardSupport", Keyboard.Default);
        CapabilityDiscovery capDisc = ParseEnum<CapabilityDiscovery>(root, "CapabilityDiscovery", CapabilityDiscovery.On);
        string name = root.Element("ProjectName")?.Value.Trim() is { Length: > 0 } n
            ? n : new DirectoryInfo(dir).Name;

        return new Manifest(dir, name, target, mode, output, keyboard, capDisc);
    }

    /// <summary>
    /// Parses a child element's text into an enum case-insensitively with a clean
    /// error listing the accepted spellings. A missing element returns the default.
    /// </summary>
    private static T ParseEnum<T>(XElement root, string elementName, T dflt) where T : struct, Enum
    {
        string? v = root.Element(elementName)?.Value.Trim();
        if (string.IsNullOrEmpty(v)) return dflt;
        // TryParse also accepts the enum's numeric underlying value; reject that so a
        // manifest can only spell a member by name, matching the previous strict behavior.
        if (!char.IsAsciiDigit(v[0]) && Enum.TryParse<T>(v, ignoreCase: true, out var result)) return result;
        throw new ManifestError(
            $"'{v}' is not a valid <{elementName}>; expected one of: {string.Join(", ", Enum.GetNames<T>())}");
    }
}

sealed class ManifestError(string message) : Exception(message);
