using System.Runtime.InteropServices;
using BrainFudger.Models;

namespace BrainFudger.Emitters;

internal interface IBinaryEmitter
{
    string TargetId { get; }
    string DisplayName { get; }
    string DefaultFileExtension { get; }
    IReadOnlyList<string> Aliases => [];
    byte[] EmitBinary(string sanitizedSource, CompilerOptions options);
    bool CanExecuteOnCurrentPlatform(out string reason);
    void PrepareFileForExecution(string outputPath) { }
}

internal static class BinaryEmitterRegistry
{
    private static readonly IBinaryEmitter[] Emitters =
    [
        MacOsArm64MachOEmitter.Instance,
        MsDosComEmitter.Instance,
        MsDosExeEmitter.Instance,
        Win32X86PortableExecutableEmitter.Instance,
        Win32X64PortableExecutableEmitter.Instance
    ];

    public static IReadOnlyList<IBinaryEmitter> GetAll() => Emitters;

    public static IBinaryEmitter Resolve(string targetId)
    {
        IBinaryEmitter? emitter = Emitters.FirstOrDefault(e =>
            string.Equals(e.TargetId, targetId, StringComparison.OrdinalIgnoreCase) ||
            e.Aliases.Any(alias => string.Equals(alias, targetId, StringComparison.OrdinalIgnoreCase)));

        return emitter ?? throw new InvalidOperationException(
            $"Unknown target '{targetId}'. Available targets: {string.Join(", ", Emitters.Select(static e => e.TargetId))}.");
    }
}
