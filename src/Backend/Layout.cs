namespace Appa;

sealed record EmitOutput(string SharedHeader, string KernelPreamble, string KernelTypes, 
    string KernelFwd, string KernelFuncs, string KernelBoot, string UserPreamble, 
    string UserTypes, string UserFwd, string UserFuncs,IReadOnlyList<IrProcess> Processes, 
    bool HasKernelRealm, bool HasUserRealm);

/// <summary>
/// A named output file produced by the compiler for a single translation unit.
/// </summary>
record OutputFile(string Name, string Content);

/// <summary>
/// Composes emitted sections into translation units. The realms a build emits
/// come from the environment, never a command-line switch.
/// </summary>
static class Layout
{
    /// <summary>
    /// Composes the emitter output into the set of translation-unit files for the build.
    /// </summary>
    public static IReadOnlyList<OutputFile> Compose(EmitOutput o) => throw new NotImplementedException();
}
