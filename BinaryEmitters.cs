using System.Runtime.InteropServices;

namespace BrainFucker;

internal interface IBinaryEmitter
{
    string TargetId { get; }
    string DisplayName { get; }
    string DefaultFileExtension { get; }
    byte[] EmitBinary(string sanitizedSource, CompilerOptions options);
    bool CanExecuteOnCurrentPlatform(out string reason);
}

internal static class BinaryEmitterRegistry
{
    private static readonly IBinaryEmitter[] Emitters =
    [
        MsDosComEmitter.Instance,
        Win32X86PortableExecutableEmitter.Instance,
        Win32X64PortableExecutableEmitter.Instance
    ];

    public static IBinaryEmitter Resolve(string targetId)
    {
        var emitter = Emitters.FirstOrDefault(e => string.Equals(e.TargetId, targetId, StringComparison.OrdinalIgnoreCase));
        return emitter ?? throw new InvalidOperationException(
            $"Unknown target '{targetId}'. Available targets: {string.Join(", ", Emitters.Select(static e => e.TargetId))}.");
    }
}
