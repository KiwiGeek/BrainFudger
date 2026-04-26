using System.Runtime.InteropServices;
using System.Text;
using BrainFudger.Models;

namespace BrainFudger.Emitters;

internal sealed class MsDosComEmitter : IBinaryEmitter
{
    private const ushort ComOrigin = 0x0100;

    public static MsDosComEmitter Instance { get; } = new();

    public string TargetId => "msdos-com";

    public string DisplayName => "MS-DOS .COM program";

    public string DefaultFileExtension => ".com";

    public bool CanExecuteOnCurrentPlatform(out string reason)
    {
        reason = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "The target 'msdos-com' cannot be executed with --run on this host. Use DOSBox, FreeDOS, or a real DOS environment."
            : "The target 'msdos-com' cannot be executed with --run on non-DOS hosts. Use DOSBox, FreeDOS, or a real DOS environment.";
        return false;
    }

    public byte[] EmitBinary(IntermediateProgram program, CompilerOptions options)
    {
        DosProgramImage image = DosBrainFudgerEmitter.EmitProgramImage(program, options);
        return DosBrainFudgerEmitter.PatchAndFlatten(image.CodeImage, image.DataImage, ComOrigin);
    }
}

internal sealed class MsDosExeEmitter : IBinaryEmitter
{
    private const int StackSize = 1024;

    public static MsDosExeEmitter Instance { get; } = new();

    public string TargetId => "msdos-exe";

    public string DisplayName => "MS-DOS MZ executable";

    public string DefaultFileExtension => ".exe";

    public bool CanExecuteOnCurrentPlatform(out string reason)
    {
        reason = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "The target 'msdos-exe' cannot be executed with --run on this host. Use DOSBox, FreeDOS, or a real DOS environment."
            : "The target 'msdos-exe' cannot be executed with --run on non-DOS hosts. Use DOSBox, FreeDOS, or a real DOS environment.";
        return false;
    }

    public byte[] EmitBinary(IntermediateProgram program, CompilerOptions options)
    {
        DosProgramImage image = DosBrainFudgerEmitter.EmitProgramImage(program, options);
        byte[] imageBytes = DosBrainFudgerEmitter.PatchAndFlatten(image.CodeImage, image.DataImage, origin: 0);
        return DosMzExecutableWriter.WriteExecutable(imageBytes, StackSize);
    }
}

internal static class DosBrainFudgerEmitter
{
    public static DosProgramImage EmitProgramImage(IntermediateProgram program, CompilerOptions options)
    {
        DosAssembler assembler = new();
        EmitPrologue(assembler);
        EmitProgram(assembler, program);
        EmitExit(assembler, 0);
        EmitErrorPath(assembler, "error_before", "pointer_before_message", 1);
        EmitErrorPath(assembler, "error_past", "pointer_past_message", 1);

        DosCodeImage codeImage = assembler.ToImage();
        DosDataImage dataImage = BuildDataImage(options);
        return new DosProgramImage(codeImage, dataImage);
    }

    public static byte[] PatchAndFlatten(DosCodeImage codeImage, DosDataImage dataImage, ushort origin)
    {
        byte[] output = new byte[codeImage.Content.Length + dataImage.Content.Length];
        Array.Copy(codeImage.Content, output, codeImage.Content.Length);
        Array.Copy(dataImage.Content, 0, output, codeImage.Content.Length, dataImage.Content.Length);

        foreach (DosTextPatch patch in codeImage.Patches)
        {
            if (!TryResolveTargetOffset(codeImage, dataImage, patch.LabelName, out int targetOffset))
            {
                throw new InvalidOperationException($"Unknown patch target '{patch.LabelName}'.");
            }

            if (patch.Kind == DosPatchKind.Absolute16)
            {
                ushort absoluteOffset = (ushort)(origin + targetOffset);
                Array.Copy(BitConverter.GetBytes(absoluteOffset), 0, output, patch.PatchOffset, 2);
            }
            else
            {
                short displacement = unchecked((short)(targetOffset - patch.NextInstructionOffset));
                Array.Copy(BitConverter.GetBytes(displacement), 0, output, patch.PatchOffset, 2);
            }
        }

        return output;
    }

    private static DosDataImage BuildDataImage(CompilerOptions options)
    {
        List<byte> bytes = new();
        Dictionary<string, int> labels = new(StringComparer.Ordinal);

        static void DefineLabel(Dictionary<string, int> labels, string name, int offset) => labels[name] = offset;

        DefineLabel(labels, "tape", bytes.Count);
        bytes.AddRange(Enumerable.Repeat((byte)0, options.CellCount));
        DefineLabel(labels, "tape_end", bytes.Count);
        DefineLabel(labels, EmitterRuntimeSupport.RngStateLabel, bytes.Count);
        bytes.Add(0);
        DefineLabel(labels, EmitterRuntimeSupport.ClearTerminalLabel, bytes.Count);
        bytes.AddRange(EmitterRuntimeSupport.ClearTerminalSequence.ToArray());

        DefineDosString(labels, bytes, "pointer_before_message", "Pointer moved before the beginning of the tape.\r\n");
        DefineDosString(labels, bytes, "pointer_past_message", "Pointer moved past the end of the tape.\r\n");

        return new DosDataImage([.. bytes], labels);
    }

    private static void DefineDosString(Dictionary<string, int> labels, List<byte> bytes, string labelName, string value)
    {
        labels[labelName] = bytes.Count;
        bytes.AddRange(Encoding.ASCII.GetBytes(value));
        bytes.Add((byte)'$');
    }

    private static bool TryResolveTargetOffset(DosCodeImage codeImage, DosDataImage dataImage, string labelName, out int targetOffset)
    {
        if (codeImage.Labels.TryGetValue(labelName, out targetOffset))
        {
            return true;
        }

        if (dataImage.Labels.TryGetValue(labelName, out int dataOffset))
        {
            targetOffset = codeImage.Content.Length + dataOffset;
            return true;
        }

        targetOffset = 0;
        return false;
    }

    private static void EmitPrologue(DosAssembler assembler)
    {
        assembler.PushCs();
        assembler.PopDs();
        assembler.PushDs();
        assembler.PopEs();

        assembler.MovRegLabelOffset(DosRegister.Bx, "tape");
        assembler.MovRegReg(DosRegister.Si, DosRegister.Bx);
        assembler.MovRegLabelOffset(DosRegister.Di, "tape_end");
    }

    private static void EmitProgram(DosAssembler assembler, IntermediateProgram program)
    {
        int inputCounter = 0;
        for (int i = 0; i < program.Instructions.Count; i++)
        {
            IntermediateInstruction instruction = program.Instructions[i];
            switch (instruction.Opcode)
            {
                case IntermediateOpcode.MovePointer:
                    EmitPointerMove(assembler, instruction.Operand);
                    break;

                case IntermediateOpcode.AddToCell:
                    EmitAddToCell(assembler, instruction.Operand);
                    break;

                case IntermediateOpcode.WriteByte:
                    EmitWriteByte(assembler);
                    break;

                case IntermediateOpcode.ReadByte:
                    EmitReadByte(assembler, inputCounter);
                    inputCounter++;
                    break;

                case IntermediateOpcode.LoopStart:
                    assembler.Label(GetLoopStartLabel(i));
                    assembler.CmpBytePtrBxImmediate(0);
                    assembler.JumpEqual(GetLoopEndLabel(instruction.MatchingInstructionIndex));
                    break;

                case IntermediateOpcode.LoopEnd:
                    assembler.CmpBytePtrBxImmediate(0);
                    assembler.JumpNotEqual(GetLoopStartLabel(instruction.MatchingInstructionIndex));
                    assembler.Label(GetLoopEndLabel(i));
                    break;

                case IntermediateOpcode.RandomByte:
                    EmitRandomByte(assembler);
                    break;

                case IntermediateOpcode.ClearTerminal:
                    EmitClearTerminal(assembler);
                    break;

                case IntermediateOpcode.Delay:
                    EmitDelay(assembler, i);
                    break;
            }
        }
    }

    private static void EmitPointerMove(DosAssembler assembler, int count)
    {
        if (count > 0)
        {
            assembler.AddRegImmediate16(DosRegister.Bx, (ushort)count);
            assembler.CmpRegReg(DosRegister.Bx, DosRegister.Di);
            assembler.JumpAboveOrEqual("error_past");
            return;
        }

        if (count < 0)
        {
            int distance = -count;
            assembler.MovRegReg(DosRegister.Ax, DosRegister.Si);
            assembler.AddRegImmediate16(DosRegister.Ax, (ushort)distance);
            assembler.CmpRegReg(DosRegister.Bx, DosRegister.Ax);
            assembler.JumpBelow("error_before");
            assembler.SubRegImmediate16(DosRegister.Bx, (ushort)distance);
        }
    }

    private static void EmitAddToCell(DosAssembler assembler, int count)
    {
        if (count > 0)
        {
            assembler.AddBytePtrBxImmediate((byte)(count & 0xFF));
        }
        else if (count < 0)
        {
            assembler.SubBytePtrBxImmediate((byte)((-count) & 0xFF));
        }
    }

    private static void EmitWriteByte(DosAssembler assembler)
    {
        assembler.MovDlBytePtrBx();
        assembler.MovAhImmediate(0x02);
        assembler.Int21();
    }

    private static void EmitReadByte(DosAssembler assembler, int inputIndex)
    {
        string zeroLabel = $"input_zero_{inputIndex}";
        string doneLabel = $"input_done_{inputIndex}";

        assembler.PushReg(DosRegister.Bx);
        assembler.MovRegReg(DosRegister.Dx, DosRegister.Bx);
        assembler.XorRegReg(DosRegister.Bx, DosRegister.Bx);
        assembler.MovRegImmediate16(DosRegister.Cx, 1);
        assembler.MovAhImmediate(0x3F);
        assembler.Int21();
        assembler.PopReg(DosRegister.Bx);
        assembler.TestRegReg(DosRegister.Ax, DosRegister.Ax);
        assembler.JumpEqual(zeroLabel);
        assembler.Jump(doneLabel);
        assembler.Label(zeroLabel);
        assembler.MovBytePtrBxImmediate(0);
        assembler.Label(doneLabel);
    }

    private static void EmitRandomByte(DosAssembler assembler)
    {
        assembler.MovRegLabelOffset(DosRegister.Si, EmitterRuntimeSupport.RngStateLabel);
        assembler.XorRegReg(DosRegister.Ax, DosRegister.Ax);
        assembler.MovAlBytePtrReg(DosRegister.Si);
        assembler.MovRegReg(DosRegister.Cx, DosRegister.Ax);
        assembler.ShiftLeftRegImmediate(DosRegister.Ax, 4);
        assembler.AddRegReg(DosRegister.Ax, DosRegister.Cx);
        assembler.AddRegImmediate16(DosRegister.Ax, 29);
        assembler.MovBytePtrRegAl(DosRegister.Si);
        assembler.MovBytePtrRegAl(DosRegister.Bx);
    }

    private static void EmitClearTerminal(DosAssembler assembler)
    {
        assembler.MovRegLabelOffset(DosRegister.Si, EmitterRuntimeSupport.ClearTerminalLabel);
        for (int i = 0; i < EmitterRuntimeSupport.ClearTerminalLength; i++)
        {
            assembler.MovDlBytePtrReg(DosRegister.Si);
            assembler.MovAhImmediate(0x02);
            assembler.Int21();
            if (i + 1 < EmitterRuntimeSupport.ClearTerminalLength)
            {
                assembler.AddRegImmediate16(DosRegister.Si, 1);
            }
        }
    }

    private static void EmitDelay(DosAssembler assembler, int instructionIndex)
    {
        string doneLabel = $"delay_done_{instructionIndex}";
        string outerLabel = $"delay_outer_{instructionIndex}";
        string innerLabel = $"delay_inner_{instructionIndex}";

        assembler.XorRegReg(DosRegister.Ax, DosRegister.Ax);
        assembler.MovAlBytePtrReg(DosRegister.Bx);
        assembler.TestRegReg(DosRegister.Ax, DosRegister.Ax);
        assembler.JumpEqual(doneLabel);
        assembler.Label(outerLabel);
        assembler.MovRegImmediate16(DosRegister.Cx, (ushort)EmitterRuntimeSupport.DelayInnerLoopCount);
        assembler.Label(innerLabel);
        assembler.SubRegImmediate16(DosRegister.Cx, 1);
        assembler.JumpNotEqual(innerLabel);
        assembler.SubRegImmediate16(DosRegister.Ax, 1);
        assembler.JumpNotEqual(outerLabel);
        assembler.Label(doneLabel);
    }

    private static void EmitExit(DosAssembler assembler, byte exitCode)
    {
        assembler.MovAhImmediate(0x4C);
        assembler.MovAlImmediate(exitCode);
        assembler.Int21();
    }

    private static void EmitErrorPath(DosAssembler assembler, string label, string messageLabel, byte exitCode)
    {
        assembler.Label(label);
        assembler.MovRegLabelOffset(DosRegister.Dx, messageLabel);
        assembler.MovAhImmediate(0x09);
        assembler.Int21();
        EmitExit(assembler, exitCode);
    }

    private static string GetLoopStartLabel(int instructionIndex) => $"loop_start_{instructionIndex}";

    private static string GetLoopEndLabel(int instructionIndex) => $"loop_end_{instructionIndex}";
}

internal static class DosMzExecutableWriter
{
    private const ushort HeaderParagraphs = 2;

    public static byte[] WriteExecutable(byte[] imageBytes, int stackSize)
    {
        byte[] imageWithStack = new byte[imageBytes.Length + stackSize];
        Array.Copy(imageBytes, imageWithStack, imageBytes.Length);

        int headerSizeBytes = HeaderParagraphs * 16;
        int fileSize = headerSizeBytes + imageWithStack.Length;
        ushort blocksInFile = (ushort)((fileSize + 511) / 512);
        ushort bytesInLastBlock = (ushort)(fileSize % 512);
        if (bytesInLastBlock == 0)
        {
            bytesInLastBlock = 512;
        }

        int totalImageSize = imageWithStack.Length;
        if (totalImageSize > 0xFFF0)
        {
            throw new InvalidOperationException("The msdos-exe target currently supports only single-segment images under 64 KB.");
        }

        int minAllocParagraphs = 0;
        int maxAllocParagraphs = 0xFFFF;
        ushort initialSp = (ushort)totalImageSize;

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write((ushort)0x5A4D);
        writer.Write(bytesInLastBlock);
        writer.Write(blocksInFile);
        writer.Write((ushort)0);
        writer.Write(HeaderParagraphs);
        writer.Write((ushort)minAllocParagraphs);
        writer.Write((ushort)maxAllocParagraphs);
        writer.Write((ushort)0);
        writer.Write(initialSp);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0x001C);
        writer.Write((ushort)0);
        writer.Write(new byte[4]);

        while (stream.Position < headerSizeBytes)
        {
            writer.Write((byte)0);
        }

        writer.Write(imageWithStack);
        return stream.ToArray();
    }
}

internal sealed record DosProgramImage(DosCodeImage CodeImage, DosDataImage DataImage);

internal sealed record DosDataImage(byte[] Content, Dictionary<string, int> Labels);

internal sealed record DosCodeImage(byte[] Content, Dictionary<string, int> Labels, List<DosTextPatch> Patches);

internal sealed record DosTextPatch(string LabelName, int PatchOffset, int NextInstructionOffset, DosPatchKind Kind);

internal enum DosPatchKind
{
    Relative16,
    Absolute16
}

internal enum DosRegister
{
    Ax = 0,
    Cx = 1,
    Dx = 2,
    Bx = 3,
    Sp = 4,
    Bp = 5,
    Si = 6,
    Di = 7
}

internal sealed class DosAssembler
{
    private readonly List<byte> _bytes = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private readonly List<DosTextPatch> _patches = [];

    public void Label(string name) => _labels[name] = _bytes.Count;

    public void PushCs() => EmitByte(0x0E);

    public void PopDs() => EmitByte(0x1F);

    public void PushDs() => EmitByte(0x1E);

    public void PopEs() => EmitByte(0x07);

    public void PushReg(DosRegister register) => EmitByte((byte)(0x50 + (int)register));

    public void PopReg(DosRegister register) => EmitByte((byte)(0x58 + (int)register));

    public void MovRegImmediate16(DosRegister register, ushort value)
    {
        EmitByte((byte)(0xB8 + (int)register));
        EmitUInt16(value);
    }

    public void MovRegLabelOffset(DosRegister register, string labelName)
    {
        EmitByte((byte)(0xB8 + (int)register));
        AddPatch(labelName, _bytes.Count, _bytes.Count + 2, DosPatchKind.Absolute16);
        EmitUInt16(0);
    }

    public void MovRegReg(DosRegister destination, DosRegister source)
    {
        EmitBytes(0x89, BuildModRm(0b11, (int)source, (int)destination));
    }

    public void MovAhImmediate(byte value) => EmitBytes(0xB4, value);

    public void MovAlImmediate(byte value) => EmitBytes(0xB0, value);

    public void MovDlBytePtrBx() => EmitBytes(0x8A, 0x17);

    public void MovDlBytePtrReg(DosRegister register)
    {
        EmitBytes(0x8A, BuildModRm(0b00, 2, GetMemoryEncoding(register)));
    }

    public void MovAlBytePtrReg(DosRegister register)
    {
        EmitBytes(0x8A, BuildModRm(0b00, 0, GetMemoryEncoding(register)));
    }

    public void MovBytePtrRegAl(DosRegister register)
    {
        EmitBytes(0x88, BuildModRm(0b00, 0, GetMemoryEncoding(register)));
    }

    public void MovBytePtrBxImmediate(byte value) => EmitBytes(0xC6, 0x07, value);

    public void AddBytePtrBxImmediate(byte value) => EmitBytes(0x80, 0x07, value);

    public void SubBytePtrBxImmediate(byte value) => EmitBytes(0x80, 0x2F, value);

    public void AddRegImmediate16(DosRegister register, ushort value)
    {
        EmitBytes(0x81, BuildModRm(0b11, 0, (int)register));
        EmitUInt16(value);
    }

    public void SubRegImmediate16(DosRegister register, ushort value)
    {
        EmitBytes(0x81, BuildModRm(0b11, 5, (int)register));
        EmitUInt16(value);
    }

    public void XorRegReg(DosRegister destination, DosRegister source)
    {
        EmitBytes(0x31, BuildModRm(0b11, (int)source, (int)destination));
    }

    public void AddRegReg(DosRegister destination, DosRegister source)
    {
        EmitBytes(0x01, BuildModRm(0b11, (int)source, (int)destination));
    }

    public void ShiftLeftRegImmediate(DosRegister register, byte value)
    {
        EmitBytes(0xC1, BuildModRm(0b11, 4, (int)register), value);
    }

    public void TestRegReg(DosRegister left, DosRegister right)
    {
        EmitBytes(0x85, BuildModRm(0b11, (int)right, (int)left));
    }

    public void CmpRegReg(DosRegister left, DosRegister right)
    {
        EmitBytes(0x39, BuildModRm(0b11, (int)right, (int)left));
    }

    public void CmpBytePtrBxImmediate(byte value) => EmitBytes(0x80, 0x3F, value);

    public void Int21() => EmitBytes(0xCD, 0x21);

    public void Jump(string labelName)
    {
        EmitByte(0xE9);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 2, DosPatchKind.Relative16);
        EmitUInt16(0);
    }

    public void JumpEqual(string labelName) => EmitConditionalJumpSequence(0x75, labelName);

    public void JumpNotEqual(string labelName) => EmitConditionalJumpSequence(0x74, labelName);

    public void JumpBelow(string labelName) => EmitConditionalJumpSequence(0x73, labelName);

    public void JumpAboveOrEqual(string labelName) => EmitConditionalJumpSequence(0x72, labelName);

    public DosCodeImage ToImage()
    {
        return new DosCodeImage([.. _bytes], new Dictionary<string, int>(_labels, StringComparer.Ordinal), [.. _patches]);
    }

    private void EmitConditionalJumpSequence(byte shortOppositeOpcode, string labelName)
    {
        EmitBytes(shortOppositeOpcode, 0x03);
        EmitByte(0xE9);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 2, DosPatchKind.Relative16);
        EmitUInt16(0);
    }

    private void AddPatch(string labelName, int patchOffset, int nextInstructionOffset, DosPatchKind kind)
    {
        _patches.Add(new DosTextPatch(labelName, patchOffset, nextInstructionOffset, kind));
    }

    private static byte BuildModRm(int mod, int reg, int rm) => (byte)((mod << 6) | (reg << 3) | rm);

    private static int GetMemoryEncoding(DosRegister register) =>
        register switch
        {
            DosRegister.Si => 0b100,
            DosRegister.Di => 0b101,
            DosRegister.Bx => 0b111,
            DosRegister.Bp => 0b110,
            _ => throw new InvalidOperationException($"Register '{register}' is not supported as a direct 16-bit memory base.")
        };

    private void EmitBytes(params byte[] bytes) => _bytes.AddRange(bytes);

    private void EmitByte(byte value) => _bytes.Add(value);

    private void EmitUInt16(ushort value) => _bytes.AddRange(BitConverter.GetBytes(value));
}
