namespace Appa;

sealed class Emitter(IrModule module, DiagnosticBag diag)
{
    readonly DiagnosticBag _diag = diag;
    readonly CodeWriter _sharedH = new();
    readonly CodeWriter _kPre = new();
    readonly CodeWriter _kTypes = new();
    readonly CodeWriter _kFwd = new();
    readonly CodeWriter _kFuncs = new();
    readonly CodeWriter _kBoot = new();
    readonly CodeWriter _uPre = new();
    readonly CodeWriter _uTypes = new();
    readonly CodeWriter _uFwd = new();
    readonly CodeWriter _uFunc = new();

    // Per-writer type dedup. Each distinct (writer, key) is emitted exactly once
    // into that translation unit. Keys are namespaced T: (forward typedef),
    // S: (struct or aggregate def), FP: (function-pointer typedef).
    readonly Dictionary<CodeWriter, HashSet<(char Kind, string Name)>> _emitted = [];

    // ARC-managed classes: every non-module Gata class carries a refcount header
    // and a generated destructor.
    readonly HashSet<string> _managed = InitializeManaged(module);

    /// <summary>
    /// Populates and returns the set of ARC-managed class names from the module.
    /// </summary>
    private static HashSet<string> InitializeManaged(IrModule module)
    {
        var set = new HashSet<string>(module.Classes.Count);
        foreach (var c in module.Classes)
        {
            if (!c.IsModule) set.Add(c.Name);
        }
        return set;
    }

    /// <summary>
    /// Returns true the first time the given key is seen for the given writer,
    /// suppressing duplicate emission within a single translation unit.
    /// </summary>
    bool FirstInto(CodeWriter w, char kind, string name)
    {
        if (!_emitted.TryGetValue(w, out var s)) _emitted[w] = s = [];
        return s.Add((kind, name));
    }

    /// <summary>
    /// Returns true if the IR type is a reference to an ARC-managed class.
    /// </summary>
    bool IsManaged(IrType t) => t is IrClassRef cr && _managed.Contains(cr.ClassName);

    /// <summary>
    /// Emits all sections and returns them for Layout to compose into files.
    /// </summary>
    public EmitOutput Build() => throw new NotImplementedException();

    /// <summary>
    /// Forward-declares every Gata class struct in the shared header so any file
    /// can use a class pointer before its full struct is defined.
    /// </summary>
    void EmitForwardTypedefs()
    {
        bool any = false;
        foreach (var cls in module.Classes)
            if (FirstInto(_sharedH, 'T', cls.Name))
            {
                _sharedH.Line($"typedef struct {cls.CName} {cls.CName};");
                any = true;
            }
        if (any) _sharedH.Line("");
    }
}
