using System.Runtime.InteropServices;
using System.Text;
using BrainFudger.Models;

namespace BrainFudger.Emitters;

internal sealed class MacOsArm64MachOEmitter : IBinaryEmitter
{
    public static MacOsArm64MachOEmitter Instance { get; } = new();

    public string TargetId => "osx-arm64";

    public string DisplayName => "macOS arm64 Mach-O executable";

    public string DefaultFileExtension => ".macho";

    public bool CanExecuteOnCurrentPlatform(out string reason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            reason = "The target 'osx-arm64' can only be executed with --run on macOS hosts.";
            return false;
        }

        if (RuntimeInformation.ProcessArchitecture is not Architecture.Arm64)
        {
            reason = $"The target 'osx-arm64' cannot be executed with --run on {RuntimeInformation.ProcessArchitecture} hosts.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public byte[] EmitBinary(string sanitizedSource, CompilerOptions options)
    {
        SectionImage dataImage = BuildDataImage(options);
        Arm64CodeImage codeImage = BuildCodeImage(sanitizedSource);
        return MachOArm64Writer.WriteExecutable(codeImage, dataImage);
    }

    public void PrepareFileForExecution(string outputPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            outputPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
    }

    private static SectionImage BuildDataImage(CompilerOptions options)
    {
        SectionBuilder builder = new();
        builder.Align(16);
        builder.DefineLabel("tape");
        builder.WriteZeros(options.CellCount);
        builder.DefineLabel("tape_end");
        builder.Align(8);
        builder.DefineAsciiString("pointer_before_message", "Pointer moved before the beginning of the tape.\n");
        builder.DefineAsciiString("pointer_past_message", "Pointer moved past the end of the tape.\n");
        return builder.ToImage();
    }

    private static Arm64CodeImage BuildCodeImage(string sanitized)
    {
        Arm64Assembler assembler = new();
        EmitPrologue(assembler);
        EmitProgram(assembler, sanitized);
        assembler.Branch("program_exit");
        EmitErrorPath(assembler, "error_before", "pointer_before_message");
        EmitErrorPath(assembler, "error_past", "pointer_past_message");
        assembler.Label("program_exit");
        assembler.MovImmediate64(0, 1);
        assembler.MovImmediate64(16, 1);
        assembler.Svc(0x80);
        return assembler.ToImage();
    }

    private static void EmitPrologue(Arm64Assembler assembler)
    {
        assembler.AdrpAddLabel(19, "tape");
        assembler.AdrpAddLabel(20, "tape");
        assembler.AdrpAddLabel(21, "tape_end");
    }

    private static void EmitProgram(Arm64Assembler assembler, string sanitized)
    {
        Stack<(string StartLabel, string EndLabel)> loopStack = new();
        int loopCounter = 0;
        int inputCounter = 0;

        for (int i = 0; i < sanitized.Length; i++)
        {
            char token = sanitized[i];

            if (token is '+' or '-' or '>' or '<')
            {
                int count = CountRepeatedTokens(sanitized, i, token);
                EmitCompressedOperation(assembler, token, count);
                i += count - 1;
                continue;
            }

            switch (token)
            {
                case '.':
                    EmitWriteByte(assembler);
                    break;

                case ',':
                    EmitReadByte(assembler, inputCounter);
                    inputCounter++;
                    break;

                case '[':
                    string startLabel = $"loop_start_{loopCounter}";
                    string endLabel = $"loop_end_{loopCounter}";
                    loopCounter++;
                    assembler.Label(startLabel);
                    assembler.LoadByte(9, 19);
                    assembler.CompareImmediate32(9, 0);
                    assembler.BranchConditional(endLabel, Arm64Condition.Equal);
                    loopStack.Push((startLabel, endLabel));
                    break;

                case ']':
                    (string StartLabel, string EndLabel) loop = loopStack.Pop();
                    assembler.LoadByte(9, 19);
                    assembler.CompareImmediate32(9, 0);
                    assembler.BranchConditional(loop.StartLabel, Arm64Condition.NotEqual);
                    assembler.Label(loop.EndLabel);
                    break;
            }
        }
    }

    private static void EmitCompressedOperation(Arm64Assembler assembler, char token, int count)
    {
        switch (token)
        {
            case '+':
                EmitAddToCell(assembler, count & 0xFF);
                break;

            case '-':
                EmitSubtractFromCell(assembler, count & 0xFF);
                break;

            case '>':
                EmitPointerMove(assembler, count, moveRight: true);
                break;

            case '<':
                EmitPointerMove(assembler, count, moveRight: false);
                break;
        }
    }

    private static void EmitAddToCell(Arm64Assembler assembler, int value)
    {
        if (value == 0)
        {
            return;
        }

        assembler.LoadByte(9, 19);
        assembler.AddImmediate32(9, 9, value);
        assembler.StoreByte(9, 19);
    }

    private static void EmitSubtractFromCell(Arm64Assembler assembler, int value)
    {
        if (value == 0)
        {
            return;
        }

        assembler.LoadByte(9, 19);
        assembler.SubtractImmediate32(9, 9, value);
        assembler.StoreByte(9, 19);
    }

    private static void EmitPointerMove(Arm64Assembler assembler, int count, bool moveRight)
    {
        while (count > 0)
        {
            int chunk = Math.Min(count, 4095);
            if (moveRight)
            {
                assembler.AddImmediate64(19, 19, chunk);
                assembler.CompareRegisters64(19, 21);
                assembler.BranchConditional("error_past", Arm64Condition.HigherOrSame);
            }
            else
            {
                assembler.SubtractImmediate64(19, 19, chunk);
                assembler.CompareRegisters64(19, 20);
                assembler.BranchConditional("error_before", Arm64Condition.Lower);
            }

            count -= chunk;
        }
    }

    private static void EmitWriteByte(Arm64Assembler assembler)
    {
        assembler.MovImmediate64(0, 1);
        assembler.MoveRegister64(1, 19);
        assembler.MovImmediate64(2, 1);
        assembler.MovImmediate64(16, 4);
        assembler.Svc(0x80);
    }

    private static void EmitReadByte(Arm64Assembler assembler, int inputIndex)
    {
        string zeroLabel = $"input_zero_{inputIndex}";
        string doneLabel = $"input_done_{inputIndex}";

        assembler.MovImmediate64(0, 0);
        assembler.MoveRegister64(1, 19);
        assembler.MovImmediate64(2, 1);
        assembler.MovImmediate64(16, 3);
        assembler.Svc(0x80);
        assembler.CompareImmediate64(0, 0);
        assembler.BranchConditional(zeroLabel, Arm64Condition.LessOrEqual);
        assembler.Branch(doneLabel);
        assembler.Label(zeroLabel);
        assembler.MovImmediate64(9, 0);
        assembler.StoreByte(9, 19);
        assembler.Label(doneLabel);
    }

    private static void EmitErrorPath(Arm64Assembler assembler, string label, string messageLabel)
    {
        assembler.Label(label);
        assembler.MovImmediate64(0, 2);
        assembler.AdrpAddLabel(1, messageLabel);
        assembler.MovImmediate64(2, GetMessageLength(messageLabel));
        assembler.MovImmediate64(16, 4);
        assembler.Svc(0x80);
        assembler.MovImmediate64(0, 1);
        assembler.MovImmediate64(16, 1);
        assembler.Svc(0x80);
    }

    private static int CountRepeatedTokens(string source, int start, char token)
    {
        int count = 0;
        while (start + count < source.Length && source[start + count] == token)
        {
            count++;
        }

        return count;
    }

    private static int GetMessageLength(string messageLabel) =>
        messageLabel switch
        {
            "pointer_before_message" => Encoding.ASCII.GetByteCount("Pointer moved before the beginning of the tape.\n"),
            "pointer_past_message" => Encoding.ASCII.GetByteCount("Pointer moved past the end of the tape.\n"),
            _ => throw new InvalidOperationException($"Unknown message label '{messageLabel}'.")
        };
}

internal static class MachOArm64Writer
{
    private const uint MhMagic64 = 0xFEEDFACF;
    private const int CpuTypeArm64 = 0x0100000C;
    private const int CpuSubtypeArm64All = 0;
    private const uint MhExecute = 0x2;
    private const uint MhFlags = 0x00200085;
    private const uint LcSegment64 = 0x19;
    private const uint LcLoadDylinker = 0xE;
    private const uint LcLoadDylib = 0xC;
    private const uint LcMain = 0x80000028;
    private const uint LcBuildVersion = 0x32;
    private const uint LcSymtab = 0x2;
    private const uint LcDysymtab = 0xB;
    private const uint VmProtectionRead = 0x1;
    private const uint VmProtectionWrite = 0x2;
    private const uint VmProtectionExecute = 0x4;
    private const uint PlatformMacOs = 1;
    private const ulong ImageBase = 0x0000000100000000;
    private const uint SegmentAlignment = 0x4000;
    private const uint CodeAlignment = SegmentAlignment;
    private const uint Section64Size = 80;
    private const uint TextSectionFlags = 0x80000400;
    private const uint DataSectionFlags = 0x0;
    private const uint LinkEditSlackSize = 0x1000;

    public static byte[] WriteExecutable(Arm64CodeImage codeImage, SectionImage dataImage)
    {
        byte[] dylinkerName = Encoding.ASCII.GetBytes("/usr/lib/dyld\0");
        byte[] libSystemName = Encoding.ASCII.GetBytes("/usr/lib/libSystem.B.dylib\0");
        uint dylinkerCommandSize = Align((uint)(12 + dylinkerName.Length), 8);
        uint loadDylibCommandSize = Align((uint)(24 + libSystemName.Length), 8);
        const uint pageZeroCommandSize = 72;
        uint textCommandSize = 72 + Section64Size;
        uint dataCommandSize = 72 + Section64Size;
        const uint linkEditCommandSize = 72;
        const uint mainCommandSize = 24;
        const uint buildVersionCommandSize = 24;
        const uint symtabCommandSize = 24;
        const uint dysymtabCommandSize = 80;
        uint sizeOfCommands =
            pageZeroCommandSize +
            textCommandSize +
            dataCommandSize +
            linkEditCommandSize +
            dylinkerCommandSize +
            loadDylibCommandSize +
            mainCommandSize +
            buildVersionCommandSize +
            symtabCommandSize +
            dysymtabCommandSize;

        uint headerSize = 32 + sizeOfCommands;
        uint codeOffset = Align(headerSize, CodeAlignment);

        uint textSectionSize = (uint)codeImage.Content.Length;
        uint textFileSize = Align(codeOffset + textSectionSize, SegmentAlignment);
        uint textVmSize = textFileSize;
        uint dataFileOffset = textFileSize;
        uint dataRawSize = (uint)dataImage.Content.Length;
        uint dataVmSize = Align(Math.Max(1u, dataRawSize), SegmentAlignment);
        ulong textSectionAddress = ImageBase + codeOffset;
        ulong dataVmAddress = ImageBase + textVmSize;
        uint dataFileSize = Align(Math.Max(1u, dataRawSize), SegmentAlignment);
        uint linkEditFileOffset = dataFileOffset + dataFileSize;
        uint stringTableOffset = linkEditFileOffset;
        uint stringTableSize = LinkEditSlackSize;
        uint linkEditFileSize = stringTableSize;
        uint linkEditVmSize = Align(Math.Max(1u, linkEditFileSize), SegmentAlignment);
        ulong linkEditVmAddress = dataVmAddress + dataVmSize;
        ulong entryPointOffset = codeOffset;
        byte[] patchedCode = PatchCode(codeImage, dataImage, codeOffset, dataVmAddress);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        WriteMachHeader(writer, 10, sizeOfCommands);
        WritePageZeroSegment(writer);
        WriteTextSegment(writer, codeOffset, textSectionSize, textFileSize);
        WriteDataSegment(writer, dataFileOffset, dataRawSize, dataVmAddress, dataVmSize);
        WriteLinkEditSegment(writer, linkEditFileOffset, linkEditFileSize, linkEditVmAddress, linkEditVmSize);
        WriteDylinkerCommand(writer, dylinkerName, dylinkerCommandSize);
        WriteLoadDylibCommand(writer, libSystemName, loadDylibCommandSize);
        WriteMainCommand(writer, entryPointOffset);
        WriteBuildVersionCommand(writer);
        WriteSymtabCommand(writer, stringTableOffset, stringTableSize);
        WriteDysymtabCommand(writer);
        PadTo(writer, codeOffset);
        writer.Write(patchedCode);
        PadTo(writer, textFileSize);
        writer.Write(dataImage.Content);
        PadTo(writer, linkEditFileOffset);
        WriteLinkEditPlaceholder(writer, stringTableSize);

        return stream.ToArray();
    }

    private static byte[] PatchCode(Arm64CodeImage codeImage, SectionImage dataImage, uint codeOffset, ulong dataVmAddress)
    {
        byte[] content = (byte[])codeImage.Content.Clone();

        foreach (Arm64Patch patch in codeImage.Patches)
        {
            ulong targetAddress;
            if (codeImage.Labels.TryGetValue(patch.LabelName, out int textOffset))
            {
                targetAddress = ImageBase + codeOffset + (uint)textOffset;
            }
            else if (dataImage.Labels.TryGetValue(patch.LabelName, out int dataOffset))
            {
                targetAddress = dataVmAddress + (uint)dataOffset;
            }
            else
            {
                throw new InvalidOperationException($"Unknown Mach-O patch target '{patch.LabelName}'.");
            }

            ulong instructionAddress = ImageBase + codeOffset + (uint)patch.InstructionOffset;
            uint encoded = patch.Kind switch
            {
                Arm64PatchKind.Branch26 => EncodeBranch26(patch.OpcodeBase, instructionAddress, targetAddress),
                Arm64PatchKind.ConditionalBranch19 => EncodeConditionalBranch19(patch.OpcodeBase, instructionAddress, targetAddress),
                Arm64PatchKind.Adrp => EncodeAdrp(patch.Register, instructionAddress, targetAddress),
                Arm64PatchKind.AddAbsoluteLow12 => EncodeAddLow12(patch.Register, targetAddress),
                _ => throw new InvalidOperationException($"Unsupported Mach-O patch kind '{patch.Kind}'.")
            };

            Array.Copy(BitConverter.GetBytes(encoded), 0, content, patch.InstructionOffset, 4);
        }

        return content;
    }

    private static void WriteMachHeader(BinaryWriter writer, uint commandCount, uint sizeOfCommands)
    {
        writer.Write(MhMagic64);
        writer.Write(CpuTypeArm64);
        writer.Write(CpuSubtypeArm64All);
        writer.Write(MhExecute);
        writer.Write(commandCount);
        writer.Write(sizeOfCommands);
        writer.Write(MhFlags);
        writer.Write(0u);
    }

    private static void WritePageZeroSegment(BinaryWriter writer)
    {
        writer.Write(LcSegment64);
        writer.Write(72u);
        WriteFixedLengthAscii(writer, "__PAGEZERO", 16);
        writer.Write(0ul);
        writer.Write(ImageBase);
        writer.Write(0ul);
        writer.Write(0ul);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
    }

    private static void WriteTextSegment(BinaryWriter writer, uint codeOffset, uint textSectionSize, uint textFileSize)
    {
        writer.Write(LcSegment64);
        writer.Write(72u + Section64Size);
        WriteFixedLengthAscii(writer, "__TEXT", 16);
        writer.Write(ImageBase);
        writer.Write((ulong)textFileSize);
        writer.Write(0ul);
        writer.Write((ulong)textFileSize);
        writer.Write(VmProtectionRead | VmProtectionExecute);
        writer.Write(VmProtectionRead | VmProtectionExecute);
        writer.Write(1u);
        writer.Write(0u);
        WriteSection(
            writer,
            "__text",
            "__TEXT",
            ImageBase + codeOffset,
            textSectionSize,
            codeOffset,
            alignPower: 4,
            TextSectionFlags);
    }

    private static void WriteDataSegment(BinaryWriter writer, uint dataFileOffset, uint dataRawSize, ulong dataVmAddress, uint dataVmSize)
    {
        writer.Write(LcSegment64);
        writer.Write(72u + Section64Size);
        WriteFixedLengthAscii(writer, "__DATA", 16);
        writer.Write(dataVmAddress);
        writer.Write((ulong)dataVmSize);
        writer.Write((ulong)dataFileOffset);
        writer.Write((ulong)dataRawSize);
        writer.Write(VmProtectionRead | VmProtectionWrite);
        writer.Write(VmProtectionRead | VmProtectionWrite);
        writer.Write(1u);
        writer.Write(0u);
        WriteSection(
            writer,
            "__data",
            "__DATA",
            dataVmAddress,
            dataRawSize,
            dataFileOffset,
            alignPower: 3,
            DataSectionFlags);
    }

    private static void WriteLinkEditSegment(BinaryWriter writer, uint linkEditFileOffset, uint linkEditFileSize, ulong linkEditVmAddress, uint linkEditVmSize)
    {
        writer.Write(LcSegment64);
        writer.Write(72u);
        WriteFixedLengthAscii(writer, "__LINKEDIT", 16);
        writer.Write(linkEditVmAddress);
        writer.Write((ulong)linkEditVmSize);
        writer.Write((ulong)linkEditFileOffset);
        writer.Write((ulong)linkEditFileSize);
        writer.Write(VmProtectionRead);
        writer.Write(VmProtectionRead);
        writer.Write(0u);
        writer.Write(0u);
    }

    private static void WriteDylinkerCommand(BinaryWriter writer, byte[] dylinkerName, uint commandSize)
    {
        long commandStart = writer.BaseStream.Position;
        writer.Write(LcLoadDylinker);
        writer.Write(commandSize);
        writer.Write(12u);
        writer.Write(dylinkerName);
        PadTo(writer, (uint)(commandStart + commandSize));
    }

    private static void WriteMainCommand(BinaryWriter writer, ulong entryPointOffset)
    {
        writer.Write(LcMain);
        writer.Write(24u);
        writer.Write(entryPointOffset);
        writer.Write(0ul);
    }

    private static void WriteLoadDylibCommand(BinaryWriter writer, byte[] dylibName, uint commandSize)
    {
        long commandStart = writer.BaseStream.Position;
        writer.Write(LcLoadDylib);
        writer.Write(commandSize);
        writer.Write(24u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(dylibName);
        PadTo(writer, (uint)(commandStart + commandSize));
    }

    private static void WriteBuildVersionCommand(BinaryWriter writer)
    {
        writer.Write(LcBuildVersion);
        writer.Write(24u);
        writer.Write(PlatformMacOs);
        writer.Write(EncodeMachVersion(11, 0, 0));
        writer.Write(EncodeMachVersion(11, 0, 0));
        writer.Write(0u);
    }

    private static void WriteSymtabCommand(BinaryWriter writer, uint stringTableOffset, uint stringTableSize)
    {
        writer.Write(LcSymtab);
        writer.Write(24u);
        writer.Write(stringTableOffset);
        writer.Write(0u);
        writer.Write(stringTableOffset);
        writer.Write(stringTableSize);
    }

    private static void WriteDysymtabCommand(BinaryWriter writer)
    {
        writer.Write(LcDysymtab);
        writer.Write(80u);
        for (int i = 0; i < 18; i++)
        {
            writer.Write(0u);
        }
    }

    private static void WriteSection(BinaryWriter writer, string sectionName, string segmentName, ulong address, uint size, uint offset, uint alignPower, uint flags)
    {
        WriteFixedLengthAscii(writer, sectionName, 16);
        WriteFixedLengthAscii(writer, segmentName, 16);
        writer.Write(address);
        writer.Write((ulong)size);
        writer.Write(offset);
        writer.Write(alignPower);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(flags);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
    }

    private static void WriteLinkEditPlaceholder(BinaryWriter writer, uint size)
    {
        writer.Write((byte)0);
        for (uint i = 1; i < size; i++)
        {
            writer.Write((byte)0);
        }
    }

    private static uint EncodeMachVersion(int major, int minor, int patch) => ((uint)major << 16) | ((uint)minor << 8) | (uint)patch;

    private static uint EncodeBranch26(uint opcodeBase, ulong instructionAddress, ulong targetAddress)
    {
        long displacement = (long)targetAddress - (long)instructionAddress;
        if ((displacement & 0x3) != 0)
        {
            throw new InvalidOperationException("Branch target is not 4-byte aligned.");
        }

        long immediate = displacement >> 2;
        if (immediate < -(1 << 25) || immediate >= (1 << 25))
        {
            throw new InvalidOperationException("Mach-O branch target is out of range.");
        }

        return opcodeBase | ((uint)immediate & 0x03FFFFFFu);
    }

    private static uint EncodeConditionalBranch19(uint opcodeBase, ulong instructionAddress, ulong targetAddress)
    {
        long displacement = (long)targetAddress - (long)instructionAddress;
        if ((displacement & 0x3) != 0)
        {
            throw new InvalidOperationException("Conditional branch target is not 4-byte aligned.");
        }

        long immediate = displacement >> 2;
        if (immediate < -(1 << 18) || immediate >= (1 << 18))
        {
            throw new InvalidOperationException("Mach-O conditional branch target is out of range.");
        }

        return opcodeBase | (((uint)immediate & 0x7FFFFu) << 5);
    }

    private static uint EncodeAddLow12(int register, ulong targetAddress)
    {
        return 0x91000000u | (((uint)targetAddress & 0xFFFu) << 10) | ((uint)register << 5) | (uint)register;
    }

    private static uint EncodeAdrp(int register, ulong instructionAddress, ulong targetAddress)
    {
        long instructionPage = (long)(instructionAddress & ~0xFFFul);
        long targetPage = (long)(targetAddress & ~0xFFFul);
        long pageDelta = (targetPage - instructionPage) >> 12;
        if (pageDelta < -(1 << 20) || pageDelta >= (1 << 20))
        {
            throw new InvalidOperationException("ADRP target is out of range.");
        }

        uint imm = (uint)(pageDelta & 0x1FFFFF);
        uint immlo = imm & 0x3;
        uint immhi = (imm >> 2) & 0x7FFFF;
        return 0x90000000u | (immlo << 29) | (immhi << 5) | (uint)register;
    }

    private static void WriteFixedLengthAscii(BinaryWriter writer, string value, int length)
    {
        byte[] bytes = new byte[length];
        Encoding.ASCII.GetBytes(value, 0, Math.Min(value.Length, length), bytes, 0);
        writer.Write(bytes);
    }

    private static uint Align(uint value, uint alignment) => ((value + alignment - 1) / alignment) * alignment;

    private static void PadTo(BinaryWriter writer, uint targetOffset)
    {
        while (writer.BaseStream.Position < targetOffset)
        {
            writer.Write((byte)0);
        }
    }
}

internal sealed record Arm64CodeImage(byte[] Content, Dictionary<string, int> Labels, List<Arm64Patch> Patches);

internal sealed record Arm64Patch(string LabelName, int InstructionOffset, Arm64PatchKind Kind, uint OpcodeBase, int Register);

internal enum Arm64PatchKind
{
    Branch26,
    ConditionalBranch19,
    Adrp,
    AddAbsoluteLow12
}

internal enum Arm64Condition : uint
{
    Equal = 0,
    NotEqual = 1,
    HigherOrSame = 2,
    Lower = 3,
    GreaterThan = 12,
    LessOrEqual = 13
}

internal sealed class Arm64Assembler
{
    private readonly List<byte> _bytes = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private readonly List<Arm64Patch> _patches = [];

    public void Label(string name) => _labels[name] = _bytes.Count;

    public void MovImmediate64(int register, long value)
    {
        if (value < 0 || value > ushort.MaxValue)
        {
            throw new InvalidOperationException("This minimal Mach-O emitter currently supports only 16-bit immediate moves.");
        }

        EmitUInt32(0xD2800000u | ((uint)value << 5) | (uint)register);
    }

    public void MoveRegister64(int destinationRegister, int sourceRegister)
    {
        EmitUInt32(0x91000000u | ((uint)sourceRegister << 5) | (uint)destinationRegister);
    }

    public void AddImmediate64(int destinationRegister, int sourceRegister, int immediate)
    {
        EmitImmediateArithmetic(0x91000000u, destinationRegister, sourceRegister, immediate);
    }

    public void SubtractImmediate64(int destinationRegister, int sourceRegister, int immediate)
    {
        EmitImmediateArithmetic(0xD1000000u, destinationRegister, sourceRegister, immediate);
    }

    public void AddImmediate32(int destinationRegister, int sourceRegister, int immediate)
    {
        EmitImmediateArithmetic(0x11000000u, destinationRegister, sourceRegister, immediate);
    }

    public void SubtractImmediate32(int destinationRegister, int sourceRegister, int immediate)
    {
        EmitImmediateArithmetic(0x51000000u, destinationRegister, sourceRegister, immediate);
    }

    public void CompareImmediate64(int register, int immediate)
    {
        EmitImmediateArithmetic(0xF100001Fu, 31, register, immediate);
    }

    public void CompareImmediate32(int register, int immediate)
    {
        EmitImmediateArithmetic(0x7100001Fu, 31, register, immediate);
    }

    public void CompareRegisters64(int leftRegister, int rightRegister)
    {
        EmitUInt32(0xEB00001Fu | ((uint)rightRegister << 16) | ((uint)leftRegister << 5));
    }

    public void LoadByte(int destinationRegister, int baseRegister)
    {
        EmitUInt32(0x39400000u | ((uint)baseRegister << 5) | (uint)destinationRegister);
    }

    public void StoreByte(int sourceRegister, int baseRegister)
    {
        EmitUInt32(0x39000000u | ((uint)baseRegister << 5) | (uint)sourceRegister);
    }

    public void Branch(string labelName)
    {
        AddPatch(labelName, Arm64PatchKind.Branch26, 0x14000000u);
        EmitUInt32(0);
    }

    public void BranchConditional(string labelName, Arm64Condition condition)
    {
        AddPatch(labelName, Arm64PatchKind.ConditionalBranch19, 0x54000000u | (uint)condition);
        EmitUInt32(0);
    }

    public void AdrpAddLabel(int register, string labelName)
    {
        AddPatch(labelName, Arm64PatchKind.Adrp, 0, register);
        EmitUInt32(0);
        AddPatch(labelName, Arm64PatchKind.AddAbsoluteLow12, 0, register);
        EmitUInt32(0);
    }

    public void Svc(ushort immediate)
    {
        EmitUInt32(0xD4000001u | ((uint)immediate << 5));
    }

    public Arm64CodeImage ToImage()
    {
        return new Arm64CodeImage([.. _bytes], new Dictionary<string, int>(_labels, StringComparer.Ordinal), [.. _patches]);
    }

    private void EmitImmediateArithmetic(uint opcodeBase, int destinationRegister, int sourceRegister, int immediate)
    {
        if ((uint)immediate > 4095)
        {
            throw new InvalidOperationException("Immediate arithmetic exceeded the supported 12-bit range.");
        }

        EmitUInt32(opcodeBase | ((uint)immediate << 10) | ((uint)sourceRegister << 5) | (uint)destinationRegister);
    }

    private void AddPatch(string labelName, Arm64PatchKind kind, uint opcodeBase, int register = 0)
    {
        _patches.Add(new Arm64Patch(labelName, _bytes.Count, kind, opcodeBase, register));
    }

    private void EmitUInt32(uint value) => _bytes.AddRange(BitConverter.GetBytes(value));
}
