using System.Runtime.InteropServices;
using System.Text;

namespace BrainFucker;

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

    public byte[] EmitBinary(string sanitizedSource, CompilerOptions options)
    {
        var assembler = new DosAssembler();
        EmitPrologue(assembler);
        EmitProgram(assembler, sanitizedSource);
        EmitExit(assembler, 0);
        EmitErrorPath(assembler, "error_before", "pointer_before_message", 1);
        EmitErrorPath(assembler, "error_past", "pointer_past_message", 1);

        var codeImage = assembler.ToImage();
        var dataImage = BuildDataImage(options);
        return PatchAndFlatten(codeImage, dataImage);
    }

    private static DosDataImage BuildDataImage(CompilerOptions options)
    {
        var bytes = new List<byte>();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);

        static void DefineLabel(Dictionary<string, int> labels, string name, int offset) => labels[name] = offset;

        DefineLabel(labels, "tape", bytes.Count);
        bytes.AddRange(Enumerable.Repeat((byte)0, options.CellCount));
        DefineLabel(labels, "tape_end", bytes.Count);

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

    private static byte[] PatchAndFlatten(DosCodeImage codeImage, DosDataImage dataImage)
    {
        var output = new byte[codeImage.Content.Length + dataImage.Content.Length];
        Array.Copy(codeImage.Content, output, codeImage.Content.Length);
        Array.Copy(dataImage.Content, 0, output, codeImage.Content.Length, dataImage.Content.Length);

        foreach (var patch in codeImage.Patches)
        {
            if (!TryResolveTargetOffset(codeImage, dataImage, patch.LabelName, out var targetOffset))
            {
                throw new InvalidOperationException($"Unknown patch target '{patch.LabelName}'.");
            }

            if (patch.Kind == DosPatchKind.Absolute16)
            {
                var absoluteOffset = (ushort)(ComOrigin + targetOffset);
                Array.Copy(BitConverter.GetBytes(absoluteOffset), 0, output, patch.PatchOffset, 2);
            }
            else
            {
                var displacement = unchecked((short)(targetOffset - patch.NextInstructionOffset));
                Array.Copy(BitConverter.GetBytes(displacement), 0, output, patch.PatchOffset, 2);
            }
        }

        return output;
    }

    private static bool TryResolveTargetOffset(DosCodeImage codeImage, DosDataImage dataImage, string labelName, out int targetOffset)
    {
        if (codeImage.Labels.TryGetValue(labelName, out targetOffset))
        {
            return true;
        }

        if (dataImage.Labels.TryGetValue(labelName, out var dataOffset))
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

    private static void EmitProgram(DosAssembler assembler, string sanitized)
    {
        var loopStack = new Stack<(string StartLabel, string EndLabel)>();
        var loopCounter = 0;
        var inputCounter = 0;

        for (var i = 0; i < sanitized.Length; i++)
        {
            var token = sanitized[i];

            if (token is '+' or '-' or '>' or '<')
            {
                var count = CountRepeatedTokens(sanitized, i, token);
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
                    var startLabel = $"loop_start_{loopCounter}";
                    var endLabel = $"loop_end_{loopCounter}";
                    loopCounter++;
                    assembler.Label(startLabel);
                    assembler.CmpBytePtrBxImmediate(0);
                    assembler.JumpEqual(endLabel);
                    loopStack.Push((startLabel, endLabel));
                    break;

                case ']':
                    var loop = loopStack.Pop();
                    assembler.CmpBytePtrBxImmediate(0);
                    assembler.JumpNotEqual(loop.StartLabel);
                    assembler.Label(loop.EndLabel);
                    break;
            }
        }
    }

    private static void EmitCompressedOperation(DosAssembler assembler, char token, int count)
    {
        switch (token)
        {
            case '+':
                assembler.AddBytePtrBxImmediate((byte)(count & 0xFF));
                break;

            case '-':
                assembler.SubBytePtrBxImmediate((byte)(count & 0xFF));
                break;

            case '>':
                assembler.AddRegImmediate16(DosRegister.Bx, (ushort)count);
                assembler.CmpRegReg(DosRegister.Bx, DosRegister.Di);
                assembler.JumpAboveOrEqual("error_past");
                break;

            case '<':
                assembler.MovRegReg(DosRegister.Ax, DosRegister.Si);
                assembler.AddRegImmediate16(DosRegister.Ax, (ushort)count);
                assembler.CmpRegReg(DosRegister.Bx, DosRegister.Ax);
                assembler.JumpBelow("error_before");
                assembler.SubRegImmediate16(DosRegister.Bx, (ushort)count);
                break;
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
        var zeroLabel = $"input_zero_{inputIndex}";
        var doneLabel = $"input_done_{inputIndex}";

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

    private static int CountRepeatedTokens(string source, int start, char token)
    {
        var count = 0;
        while (start + count < source.Length && source[start + count] == token)
        {
            count++;
        }

        return count;
    }
}

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

    private void EmitBytes(params byte[] bytes) => _bytes.AddRange(bytes);

    private void EmitByte(byte value) => _bytes.Add(value);

    private void EmitUInt16(ushort value) => _bytes.AddRange(BitConverter.GetBytes(value));
}
