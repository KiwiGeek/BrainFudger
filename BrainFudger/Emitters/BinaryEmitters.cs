using System.Runtime.InteropServices;
using BrainFudger.Models;

namespace BrainFudger.Emitters;

public interface IBinaryEmitter
{
    string TargetId { get; }
    string DisplayName { get; }
    string DefaultFileExtension { get; }
    IReadOnlyList<string> Aliases => [];
    byte[] EmitBinary(IntermediateProgram program, CompilerOptions options);
    bool CanExecuteOnCurrentPlatform(out string reason);
    void PrepareFileForExecution(string outputPath) { }
}

public static class BinaryEmitterRegistry
{
    private static readonly IBinaryEmitter[] Emitters =
    [
        LinuxArm64ElfEmitter.Instance,
        LinuxX64ElfEmitter.Instance,
        LinuxX86ElfEmitter.Instance,
        MacOsArm64MachOEmitter.Instance,
        MsDosComEmitter.Instance,
        MsDosExeEmitter.Instance,
        Win32X86PortableExecutableEmitter.Instance,
        Win32X64PortableExecutableEmitter.Instance
    ];

    public static IReadOnlyList<IBinaryEmitter> GetAll() => Emitters;

    public static string GetDefaultTargetForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "linux-arm64",
                Architecture.X86 => "linux-x86",
                Architecture.X64 => "linux-x64",
                _ => "linux-x64"
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return "osx-arm64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X86)
        {
            return "win-x86";
        }

        return "win-x64";
    }

    public static IBinaryEmitter Resolve(string targetId)
    {
        IBinaryEmitter? emitter = Emitters.FirstOrDefault(e =>
            string.Equals(e.TargetId, targetId, StringComparison.OrdinalIgnoreCase) ||
            e.Aliases.Any(alias => string.Equals(alias, targetId, StringComparison.OrdinalIgnoreCase)));

        return emitter ?? throw new InvalidOperationException(
            $"Unknown target '{targetId}'. Available targets: {string.Join(", ", Emitters.Select(static e => e.TargetId))}.");
    }
}
