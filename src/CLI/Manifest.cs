namespace Appa;

using System.Xml.Linq;

// What a build produces / how it is hosted.
//   GatOS: a bootable ISO (the kernel target).
//   Hosted: C against the libc platform (the test/ASAN harness target).
enum Target { GatOS, Hosted }

// Build mode. Debug allows the diagnostic floor (debug/panic) and ships unoptimized
// with debug info. Release strips diagnostics and optimizes.
enum Mode { Debug, Release }

// Where Console output is routed. Framebuffer (default) drives the FRAMEBUFFER
// capability. Serial routes to the serial port instead (no framebuffer subsystem).
enum Output { Framebuffer, Serial }

// Keyboard support level, passed through to GatOS as-is: Default is PS/2 only, External adds
// USB keyboards, Hotplug adds hotplug for them.
enum Keyboard { Default, External, Hotplug }

// On (default): CapabilityScan infers MEM/INPUT/THREADS, so the image carries only what it
// uses. Off: assume all three, the escape valve for a raw native{} body that touches a
// capability through no Gata-visible call, where inference would under-declare.
enum CapabilityDiscovery { On, Off }

// A project's build configuration, read from its <project>.gconf: what to build, how, and the
// explicitly-chosen knobs. gcc flags, env/entry paths and stdlib selection are not here, appa
// owns the flags, @environment is discovered, and the entry is the src/main.g convention.
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
    /// Locates the single *.gconf in a directory. Returns null if none exists, and throws
    /// ManifestError if more than one is found.
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
    /// Parses a .gconf file and returns its Manifest. Throws ManifestError on malformed or missing
    /// required elements.
    /// </summary>
    public static Manifest Load(string path)
    {
        string file = Path.GetFileName(path);
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            throw new ManifestError($"cannot read {file}: {ex.Message}");
        }

        var root = doc.Root ?? throw new ManifestError($"{file} is empty");
        if (root.Name.LocalName != "appa")
            throw new ManifestError($"{file} must have an <appa> root, got <{root.Name.LocalName}>");

        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        var target = ParseEnum(root, "TargetBackend", Target.GatOS);
        var mode = ParseEnum(root, "BuildMode", Mode.Debug);
        var output = ParseEnum(root, "OutputType", Output.Framebuffer);
        var keyboard = ParseEnum(root, "KeyboardSupport", Keyboard.Default);
        var capDisc = ParseEnum(root, "CapabilityDiscovery", CapabilityDiscovery.On);

        // no <ProjectName> means the folder name
        string? name = root.Element("ProjectName")?.Value.Trim();
        if (string.IsNullOrEmpty(name)) name = new DirectoryInfo(dir).Name;

        return new Manifest(dir, name, target, mode, output, keyboard, capDisc);
    }

    /// <summary>
    /// Parses a child element's text into an enum case-insensitively with a clean error listing the
    /// accepted spellings. A missing element returns the default.
    /// </summary>
    private static T ParseEnum<T>(XElement root, string elementName, T dflt) where T : struct, Enum
    {
        string? v = root.Element(elementName)?.Value.Trim();
        if (string.IsNullOrEmpty(v)) return dflt;

        // Enum.TryParse happily takes "3" as a value, which nobody means in a manifest
        if (!char.IsAsciiDigit(v[0]) && Enum.TryParse<T>(v, ignoreCase: true, out var parsed))
            return parsed;

        throw new ManifestError(
            $"'{v}' is not a valid <{elementName}>; expected one of: {string.Join(", ", Enum.GetNames<T>())}");
    }
}

sealed class ManifestError(string message) : Exception(message);
