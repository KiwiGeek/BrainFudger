using System.Text;
using System.Runtime.InteropServices;
using BrainFudger.Models;

namespace BrainFudger.Emitters;

internal sealed class Win32X86PortableExecutableEmitter : IBinaryEmitter
{
    public static Win32X86PortableExecutableEmitter Instance { get; } = new();

    public string TargetId => "win32-x86";

    public string DisplayName => "Win32 x86 executable";

    public string DefaultFileExtension => ".exe";

    public bool CanExecuteOnCurrentPlatform(out string reason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            reason = "The target 'win32-x86' can only be executed with --run on Windows hosts.";
            return false;
        }

        if (RuntimeInformation.ProcessArchitecture is not Architecture.X86 and not Architecture.X64 and not Architecture.Arm64)
        {
            reason = $"The target 'win32-x86' cannot be executed with --run on {RuntimeInformation.ProcessArchitecture} hosts.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public byte[] EmitBinary(string sanitizedSource, CompilerOptions options)
    {
        SectionImage dataSection = BuildDataSection(options);
        SectionImage importSection = BuildImportSection();
        X86CodeImage codeImage = BuildCodeImage(sanitizedSource);
        return PortableExecutableWriter32.WriteExecutable(codeImage, importSection, dataSection);
    }

    private static SectionImage BuildDataSection(CompilerOptions options)
    {
        SectionBuilder builder = new();
        builder.Align(16);
        builder.DefineLabel("stdin_handle");
        builder.WriteUInt32(0);
        builder.DefineLabel("stdout_handle");
        builder.WriteUInt32(0);
        builder.DefineLabel("stderr_handle");
        builder.WriteUInt32(0);
        builder.DefineLabel("tape");
        builder.WriteZeros(options.CellCount);
        builder.DefineLabel("tape_end");
        builder.Align(4);
        builder.DefineLabel("io_result");
        builder.WriteUInt32(0);
        builder.DefineAsciiString("pointer_before_message", "Pointer moved before the beginning of the tape.\r\n");
        builder.DefineAsciiString("pointer_past_message", "Pointer moved past the end of the tape.\r\n");
        return builder.ToImage();
    }

    private static SectionImage BuildImportSection()
    {
        SectionBuilder builder = new();
        builder.DefineLabel("import_descriptor");
        builder.WriteLabelReference32("import_lookup_table");
        builder.WriteUInt32(0);
        builder.WriteUInt32(0);
        builder.WriteLabelReference32("kernel32_name");
        builder.WriteLabelReference32("GetStdHandle_iat");
        builder.WriteZeros(20);

        builder.DefineLabel("import_lookup_table");
        builder.WriteLabelReference32("GetStdHandle_hint");
        builder.WriteLabelReference32("ReadFile_hint");
        builder.WriteLabelReference32("WriteFile_hint");
        builder.WriteLabelReference32("ExitProcess_hint");
        builder.WriteUInt32(0);

        builder.DefineLabel("GetStdHandle_iat");
        builder.WriteLabelReference32("GetStdHandle_hint");
        builder.DefineLabel("ReadFile_iat");
        builder.WriteLabelReference32("ReadFile_hint");
        builder.DefineLabel("WriteFile_iat");
        builder.WriteLabelReference32("WriteFile_hint");
        builder.DefineLabel("ExitProcess_iat");
        builder.WriteLabelReference32("ExitProcess_hint");
        builder.WriteUInt32(0);

        builder.DefineAsciiString("kernel32_name", "KERNEL32.dll");

        builder.Align(2);
        builder.DefineLabel("GetStdHandle_hint");
        builder.WriteUInt16(0);
        builder.WriteAsciiStringRaw("GetStdHandle");

        builder.Align(2);
        builder.DefineLabel("ReadFile_hint");
        builder.WriteUInt16(0);
        builder.WriteAsciiStringRaw("ReadFile");

        builder.Align(2);
        builder.DefineLabel("WriteFile_hint");
        builder.WriteUInt16(0);
        builder.WriteAsciiStringRaw("WriteFile");

        builder.Align(2);
        builder.DefineLabel("ExitProcess_hint");
        builder.WriteUInt16(0);
        builder.WriteAsciiStringRaw("ExitProcess");

        return builder.ToImage();
    }

    private static X86CodeImage BuildCodeImage(string sanitized)
    {
        X86Assembler assembler = new();
        EmitPrologue(assembler);
        EmitProgram(assembler, sanitized);
        assembler.Jump("program_exit");
        EmitErrorPath(assembler, "error_before", "pointer_before_message");
        EmitErrorPath(assembler, "error_past", "pointer_past_message");
        assembler.Label("program_exit");
        assembler.PushImmediate32(0);
        assembler.CallIat("ExitProcess_iat");
        return assembler.ToImage();
    }

    private static void EmitPrologue(X86Assembler assembler)
    {
        assembler.PushImmediate32(unchecked((int)0xFFFFFFF6));
        assembler.CallIat("GetStdHandle_iat");
        assembler.MovDwordPtrLabelReg("stdin_handle", X86Register.Eax);

        assembler.PushImmediate32(unchecked((int)0xFFFFFFF5));
        assembler.CallIat("GetStdHandle_iat");
        assembler.MovDwordPtrLabelReg("stdout_handle", X86Register.Eax);

        assembler.PushImmediate32(unchecked((int)0xFFFFFFF4));
        assembler.CallIat("GetStdHandle_iat");
        assembler.MovDwordPtrLabelReg("stderr_handle", X86Register.Eax);

        assembler.MovRegLabelAddress(X86Register.Ebx, "tape");
        assembler.MovRegReg(X86Register.Esi, X86Register.Ebx);
        assembler.MovRegLabelAddress(X86Register.Edi, "tape_end");
    }

    private static void EmitProgram(X86Assembler assembler, string sanitized)
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
                    assembler.CmpBytePtrEbxImmediate(0);
                    assembler.JumpEqual(endLabel);
                    loopStack.Push((startLabel, endLabel));
                    break;

                case ']':
                    (string StartLabel, string EndLabel) loop = loopStack.Pop();
                    assembler.CmpBytePtrEbxImmediate(0);
                    assembler.JumpNotEqual(loop.StartLabel);
                    assembler.Label(loop.EndLabel);
                    break;
            }
        }
    }

    private static void EmitCompressedOperation(X86Assembler assembler, char token, int count)
    {
        switch (token)
        {
            case '+':
                assembler.AddBytePtrEbxImmediate((byte)(count & 0xFF));
                break;

            case '-':
                assembler.SubBytePtrEbxImmediate((byte)(count & 0xFF));
                break;

            case '>':
                assembler.AddReg32Immediate(X86Register.Ebx, count);
                assembler.CmpRegReg(X86Register.Ebx, X86Register.Edi);
                assembler.JumpAboveOrEqual("error_past");
                break;

            case '<':
                assembler.MovRegReg(X86Register.Eax, X86Register.Esi);
                assembler.AddReg32Immediate(X86Register.Eax, count);
                assembler.CmpRegReg(X86Register.Ebx, X86Register.Eax);
                assembler.JumpBelow("error_before");
                assembler.SubReg32Immediate(X86Register.Ebx, count);
                break;
        }
    }

    private static void EmitWriteByte(X86Assembler assembler)
    {
        assembler.PushImmediate32(0);
        assembler.PushLabelAddress("io_result");
        assembler.PushImmediate32(1);
        assembler.PushReg(X86Register.Ebx);
        assembler.MovRegDwordPtrLabel(X86Register.Eax, "stdout_handle");
        assembler.PushReg(X86Register.Eax);
        assembler.CallIat("WriteFile_iat");
    }

    private static void EmitReadByte(X86Assembler assembler, int inputIndex)
    {
        string zeroLabel = $"input_zero_{inputIndex}";
        string doneLabel = $"input_done_{inputIndex}";

        assembler.PushImmediate32(0);
        assembler.PushLabelAddress("io_result");
        assembler.PushImmediate32(1);
        assembler.PushReg(X86Register.Ebx);
        assembler.MovRegDwordPtrLabel(X86Register.Eax, "stdin_handle");
        assembler.PushReg(X86Register.Eax);
        assembler.CallIat("ReadFile_iat");
        assembler.TestEaxEax();
        assembler.JumpEqual(zeroLabel);
        assembler.CmpDwordPtrLabelImmediate("io_result", 0);
        assembler.JumpNotEqual(doneLabel);
        assembler.Label(zeroLabel);
        assembler.MovBytePtrEbxImmediate(0);
        assembler.Label(doneLabel);
    }

    private static void EmitErrorPath(X86Assembler assembler, string label, string messageLabel)
    {
        assembler.Label(label);
        assembler.PushImmediate32(0);
        assembler.PushLabelAddress("io_result");
        assembler.PushImmediate32(GetMessageLength(messageLabel));
        assembler.PushLabelAddress(messageLabel);
        assembler.MovRegDwordPtrLabel(X86Register.Eax, "stderr_handle");
        assembler.PushReg(X86Register.Eax);
        assembler.CallIat("WriteFile_iat");
        assembler.PushImmediate32(1);
        assembler.CallIat("ExitProcess_iat");
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
            "pointer_before_message" => Encoding.ASCII.GetByteCount("Pointer moved before the beginning of the tape.\r\n"),
            "pointer_past_message" => Encoding.ASCII.GetByteCount("Pointer moved past the end of the tape.\r\n"),
            _ => throw new InvalidOperationException($"Unknown message label '{messageLabel}'.")
        };
}

internal sealed class Win32X64PortableExecutableEmitter : IBinaryEmitter
{
    public static Win32X64PortableExecutableEmitter Instance { get; } = new();

    public string TargetId => "win32-x64";

    public string DisplayName => "Win32 x64 executable";

    public string DefaultFileExtension => ".exe";

    public bool CanExecuteOnCurrentPlatform(out string reason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            reason = "The target 'win32-x64' can only be executed with --run on Windows hosts.";
            return false;
        }

        if (RuntimeInformation.ProcessArchitecture is not Architecture.X64 and not Architecture.Arm64)
        {
            reason = $"The target 'win32-x64' cannot be executed with --run on {RuntimeInformation.ProcessArchitecture} hosts.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public byte[] EmitBinary(string sanitizedSource, CompilerOptions options)
    {
        SectionImage dataSection = BuildDataSection(options);
        SectionImage importSection = BuildImportSection();
        CodeImage codeImage = BuildCodeImage(sanitizedSource);
        return PortableExecutableWriter.WriteExecutable(codeImage, importSection, dataSection);
    }

    private static SectionImage BuildDataSection(CompilerOptions options)
    {
        SectionBuilder builder = new();
        builder.Align(16);
        builder.DefineLabel("tape");
        builder.WriteZeros(options.CellCount);
        builder.DefineLabel("tape_end");
        builder.Align(8);
        builder.DefineLabel("io_result");
        builder.WriteZeros(8);
        builder.DefineAsciiString("pointer_before_message", "Pointer moved before the beginning of the tape.\r\n");
        builder.DefineAsciiString("pointer_past_message", "Pointer moved past the end of the tape.\r\n");
        return builder.ToImage();
    }

    private static SectionImage BuildImportSection()
    {
        SectionBuilder builder = new();
        builder.DefineLabel("import_descriptor");
        builder.WriteLabelReference32("import_lookup_table");
        builder.WriteUInt32(0);
        builder.WriteUInt32(0);
        builder.WriteLabelReference32("kernel32_name");
        builder.WriteLabelReference32("GetStdHandle_iat");
        builder.WriteZeros(20);

        builder.DefineLabel("import_lookup_table");
        builder.WriteLabelReference64("GetStdHandle_hint");
        builder.WriteLabelReference64("ReadFile_hint");
        builder.WriteLabelReference64("WriteFile_hint");
        builder.WriteLabelReference64("ExitProcess_hint");
        builder.WriteUInt64(0);

        builder.DefineLabel("GetStdHandle_iat");
        builder.WriteLabelReference64("GetStdHandle_hint");
        builder.DefineLabel("ReadFile_iat");
        builder.WriteLabelReference64("ReadFile_hint");
        builder.DefineLabel("WriteFile_iat");
        builder.WriteLabelReference64("WriteFile_hint");
        builder.DefineLabel("ExitProcess_iat");
        builder.WriteLabelReference64("ExitProcess_hint");
        builder.WriteUInt64(0);

        builder.DefineAsciiString("kernel32_name", "KERNEL32.dll");

        builder.Align(2);
        builder.DefineLabel("GetStdHandle_hint");
        builder.WriteUInt16(0);
        builder.WriteAsciiStringRaw("GetStdHandle");

        builder.Align(2);
        builder.DefineLabel("ReadFile_hint");
        builder.WriteUInt16(0);
        builder.WriteAsciiStringRaw("ReadFile");

        builder.Align(2);
        builder.DefineLabel("WriteFile_hint");
        builder.WriteUInt16(0);
        builder.WriteAsciiStringRaw("WriteFile");

        builder.Align(2);
        builder.DefineLabel("ExitProcess_hint");
        builder.WriteUInt16(0);
        builder.WriteAsciiStringRaw("ExitProcess");

        return builder.ToImage();
    }

    private static CodeImage BuildCodeImage(string sanitized)
    {
        X64Assembler assembler = new();
        EmitPrologue(assembler);
        EmitProgram(assembler, sanitized);
        assembler.Jump("program_exit");
        EmitErrorPath(assembler, "error_before", "pointer_before_message");
        EmitErrorPath(assembler, "error_past", "pointer_past_message");
        assembler.Label("program_exit");
        assembler.XorReg32(AssemblerRegister.Rcx, AssemblerRegister.Rcx);
        assembler.CallIat("ExitProcess_iat");
        return assembler.ToImage();
    }

    private static void EmitPrologue(X64Assembler assembler)
    {
        assembler.SubRsp(40);

        assembler.MovReg32Immediate(AssemblerRegister.Rcx, unchecked((int)0xFFFFFFF6));
        assembler.CallIat("GetStdHandle_iat");
        assembler.MovRegReg(AssemblerRegister.R14, AssemblerRegister.Rax);

        assembler.MovReg32Immediate(AssemblerRegister.Rcx, unchecked((int)0xFFFFFFF5));
        assembler.CallIat("GetStdHandle_iat");
        assembler.MovRegReg(AssemblerRegister.R15, AssemblerRegister.Rax);

        assembler.MovReg32Immediate(AssemblerRegister.Rcx, unchecked((int)0xFFFFFFF4));
        assembler.CallIat("GetStdHandle_iat");
        assembler.MovRegReg(AssemblerRegister.Rdi, AssemblerRegister.Rax);

        assembler.LeaRipLabel(AssemblerRegister.Rbx, "tape");
        assembler.MovRegReg(AssemblerRegister.R12, AssemblerRegister.Rbx);
        assembler.LeaRipLabel(AssemblerRegister.R13, "tape_end");
    }

    private static void EmitProgram(X64Assembler assembler, string sanitized)
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
                    assembler.CmpBytePtrRbxImmediate(0);
                    assembler.JumpEqual(endLabel);
                    loopStack.Push((startLabel, endLabel));
                    break;

                case ']':
                    (string StartLabel, string EndLabel) loop = loopStack.Pop();
                    assembler.CmpBytePtrRbxImmediate(0);
                    assembler.JumpNotEqual(loop.StartLabel);
                    assembler.Label(loop.EndLabel);
                    break;
            }
        }
    }

    private static void EmitCompressedOperation(X64Assembler assembler, char token, int count)
    {
        switch (token)
        {
            case '+':
                assembler.AddBytePtrRbxImmediate((byte)(count & 0xFF));
                break;

            case '-':
                assembler.SubBytePtrRbxImmediate((byte)(count & 0xFF));
                break;

            case '>':
                assembler.AddReg64Immediate(AssemblerRegister.Rbx, count);
                assembler.CmpRegReg(AssemblerRegister.Rbx, AssemblerRegister.R13);
                assembler.JumpAboveOrEqual("error_past");
                break;

            case '<':
                assembler.MovRegReg(AssemblerRegister.Rax, AssemblerRegister.R12);
                assembler.AddReg64Immediate(AssemblerRegister.Rax, count);
                assembler.CmpRegReg(AssemblerRegister.Rbx, AssemblerRegister.Rax);
                assembler.JumpBelow("error_before");
                assembler.SubReg64Immediate(AssemblerRegister.Rbx, count);
                break;
        }
    }

    private static void EmitWriteByte(X64Assembler assembler)
    {
        assembler.MovRegReg(AssemblerRegister.Rcx, AssemblerRegister.R15);
        assembler.MovRegReg(AssemblerRegister.Rdx, AssemblerRegister.Rbx);
        assembler.MovReg32Immediate(AssemblerRegister.R8, 1);
        assembler.LeaRipLabel(AssemblerRegister.R9, "io_result");
        assembler.MovStackQwordImmediate32(32, 0);
        assembler.CallIat("WriteFile_iat");
    }

    private static void EmitReadByte(X64Assembler assembler, int inputIndex)
    {
        string zeroLabel = $"input_zero_{inputIndex}";
        string doneLabel = $"input_done_{inputIndex}";

        assembler.MovRegReg(AssemblerRegister.Rcx, AssemblerRegister.R14);
        assembler.MovRegReg(AssemblerRegister.Rdx, AssemblerRegister.Rbx);
        assembler.MovReg32Immediate(AssemblerRegister.R8, 1);
        assembler.LeaRipLabel(AssemblerRegister.R9, "io_result");
        assembler.MovStackQwordImmediate32(32, 0);
        assembler.CallIat("ReadFile_iat");
        assembler.TestEaxEax();
        assembler.JumpEqual(zeroLabel);
        assembler.CmpDwordRipLabelImmediate("io_result", 0);
        assembler.JumpNotEqual(doneLabel);
        assembler.Label(zeroLabel);
        assembler.MovBytePtrRbxImmediate(0);
        assembler.Label(doneLabel);
    }

    private static void EmitErrorPath(X64Assembler assembler, string label, string messageLabel)
    {
        assembler.Label(label);
        assembler.MovRegReg(AssemblerRegister.Rcx, AssemblerRegister.Rdi);
        assembler.LeaRipLabel(AssemblerRegister.Rdx, messageLabel);
        assembler.MovReg32Immediate(AssemblerRegister.R8, GetMessageLength(messageLabel));
        assembler.LeaRipLabel(AssemblerRegister.R9, "io_result");
        assembler.MovStackQwordImmediate32(32, 0);
        assembler.CallIat("WriteFile_iat");
        assembler.MovReg32Immediate(AssemblerRegister.Rcx, 1);
        assembler.CallIat("ExitProcess_iat");
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
            "pointer_before_message" => Encoding.ASCII.GetByteCount("Pointer moved before the beginning of the tape.\r\n"),
            "pointer_past_message" => Encoding.ASCII.GetByteCount("Pointer moved past the end of the tape.\r\n"),
            _ => throw new InvalidOperationException($"Unknown message label '{messageLabel}'.")
        };
}

internal sealed class SectionBuilder
{
    private readonly List<byte> _bytes = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private readonly List<LabelReference> _references = [];

    public void DefineLabel(string name)
    {
        _labels[name] = _bytes.Count;
    }

    public void Align(int alignment)
    {
        while (_bytes.Count % alignment != 0)
        {
            _bytes.Add(0);
        }
    }

    public void WriteZeros(int count)
    {
        _bytes.AddRange(Enumerable.Repeat((byte)0, count));
    }

    public void WriteUInt16(ushort value)
    {
        _bytes.AddRange(BitConverter.GetBytes(value));
    }

    public void WriteUInt32(uint value)
    {
        _bytes.AddRange(BitConverter.GetBytes(value));
    }

    public void WriteUInt64(ulong value)
    {
        _bytes.AddRange(BitConverter.GetBytes(value));
    }

    public void WriteLabelReference32(string labelName)
    {
        _references.Add(new LabelReference(_bytes.Count, labelName, 4));
        WriteUInt32(0);
    }

    public void WriteLabelReference64(string labelName)
    {
        _references.Add(new LabelReference(_bytes.Count, labelName, 8));
        WriteUInt64(0);
    }

    public void DefineAsciiString(string labelName, string value)
    {
        DefineLabel(labelName);
        WriteAsciiStringRaw(value);
    }

    public void WriteAsciiStringRaw(string value)
    {
        _bytes.AddRange(Encoding.ASCII.GetBytes(value));
        _bytes.Add(0);
    }

    public SectionImage ToImage()
    {
        return new SectionImage([.. _bytes], new Dictionary<string, int>(_labels, StringComparer.Ordinal), [.. _references]);
    }
}

internal sealed record SectionImage(byte[] Content, Dictionary<string, int> Labels, List<LabelReference> References);

internal sealed record LabelReference(int Offset, string LabelName, int Width);

internal static class PortableExecutableWriter32
{
    private const uint ImageBase = 0x00400000;
    private const uint SectionAlignment = 0x1000;
    private const uint FileAlignment = 0x200;

    public static byte[] WriteExecutable(X86CodeImage codeImage, SectionImage importSection, SectionImage dataSection)
    {
        PeSection textSection = new(".text", codeImage.Content, 0x60000020);
        PeSection importPeSection = new(".idata", importSection.Content, 0x40000040);
        PeSection dataPeSection = new(".data", dataSection.Content, 0xC0000040);
        List<PeSection> sections = new() { textSection, importPeSection, dataPeSection };

        uint sizeOfHeaders = Align(0x80u + 4u + 20u + 0xE0u + (uint)(sections.Count * 40), FileAlignment);
        uint currentRawPointer = sizeOfHeaders;
        foreach (PeSection section in sections)
        {
            section.PointerToRawData = currentRawPointer;
            section.SizeOfRawData = Align((uint)section.Content.Length, FileAlignment);
            currentRawPointer += section.SizeOfRawData;
        }

        uint currentRva = SectionAlignment;
        foreach (PeSection section in sections)
        {
            section.VirtualAddress = currentRva;
            section.VirtualSize = (uint)section.Content.Length;
            currentRva += Align(section.VirtualSize, SectionAlignment);
        }

        SectionImage fixedImportSection = PatchImportSection(importSection, importPeSection.VirtualAddress);
        importPeSection.Content = fixedImportSection.Content;

        uint importDescriptorRva = importPeSection.VirtualAddress + (uint)fixedImportSection.Labels["import_descriptor"];
        uint importDirectorySize = 40u;
        uint iatRva = importPeSection.VirtualAddress + (uint)fixedImportSection.Labels["GetStdHandle_iat"];
        uint iatSize = 20u;
        uint sizeOfImage = currentRva;

        textSection.Content = PatchTextSection(codeImage, fixedImportSection, dataSection, textSection.VirtualAddress, importPeSection.VirtualAddress, dataPeSection.VirtualAddress);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        WriteDosHeader(writer);
        WritePeHeaders(writer, sections, sizeOfHeaders, sizeOfImage, importDescriptorRva, importDirectorySize, iatRva, iatSize);
        PadTo(writer, sizeOfHeaders);

        foreach (PeSection section in sections)
        {
            WriteSection(writer, section);
        }

        return stream.ToArray();
    }

    private static SectionImage PatchImportSection(SectionImage section, uint sectionRva)
    {
        byte[] content = (byte[])section.Content.Clone();
        foreach (LabelReference reference in section.References)
        {
            uint targetRva = sectionRva + (uint)section.Labels[reference.LabelName];
            Array.Copy(BitConverter.GetBytes(targetRva), 0, content, reference.Offset, 4);
        }

        return new SectionImage(content, section.Labels, []);
    }

    private static byte[] PatchTextSection(X86CodeImage codeImage, SectionImage importSection, SectionImage dataSection, uint textRva, uint importRva, uint dataRva)
    {
        byte[] content = (byte[])codeImage.Content.Clone();
        foreach (X86TextPatch patch in codeImage.Patches)
        {
            uint targetRva;
            if (codeImage.Labels.TryGetValue(patch.LabelName, out int textOffset))
            {
                targetRva = textRva + (uint)textOffset;
            }
            else if (importSection.Labels.TryGetValue(patch.LabelName, out int importOffset))
            {
                targetRva = importRva + (uint)importOffset;
            }
            else if (dataSection.Labels.TryGetValue(patch.LabelName, out int dataOffset))
            {
                targetRva = dataRva + (uint)dataOffset;
            }
            else
            {
                throw new InvalidOperationException($"Unknown patch target '{patch.LabelName}'.");
            }

            if (patch.Kind == X86PatchKind.Relative32)
            {
                uint sourceNextRva = textRva + (uint)patch.NextInstructionOffset;
                int displacement = unchecked((int)(targetRva - sourceNextRva));
                Array.Copy(BitConverter.GetBytes(displacement), 0, content, patch.PatchOffset, 4);
            }
            else
            {
                uint absoluteAddress = ImageBase + targetRva;
                Array.Copy(BitConverter.GetBytes(absoluteAddress), 0, content, patch.PatchOffset, 4);
            }
        }

        return content;
    }

    private static void WriteDosHeader(BinaryWriter writer)
    {
        writer.Write((ushort)0x5A4D);
        writer.Write(new byte[58]);
        writer.Write(0x80);
        writer.Write(new byte[0x80 - 64]);
    }

    private static void WritePeHeaders(
        BinaryWriter writer,
        List<PeSection> sections,
        uint sizeOfHeaders,
        uint sizeOfImage,
        uint importDescriptorRva,
        uint importDirectorySize,
        uint iatRva,
        uint iatSize)
    {
        writer.Write(Encoding.ASCII.GetBytes("PE\0\0"));
        writer.Write((ushort)0x014C);
        writer.Write((ushort)sections.Count);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0xE0);
        writer.Write((ushort)0x0102);

        writer.Write((ushort)0x10B);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write(sections[0].SizeOfRawData);
        writer.Write(sections[1].SizeOfRawData + sections[2].SizeOfRawData);
        writer.Write(0u);
        writer.Write(sections[0].VirtualAddress);
        writer.Write(sections[0].VirtualAddress);
        writer.Write(sections[2].VirtualAddress);
        writer.Write(ImageBase);
        writer.Write(SectionAlignment);
        writer.Write(FileAlignment);
        writer.Write((ushort)6);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)6);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(sizeOfImage);
        writer.Write(sizeOfHeaders);
        writer.Write(0u);
        writer.Write((ushort)3);
        writer.Write((ushort)0);
        writer.Write(0x00100000u);
        writer.Write(0x00001000u);
        writer.Write(0x00100000u);
        writer.Write(0x00001000u);
        writer.Write(0u);
        writer.Write(16u);

        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, importDescriptorRva, importDirectorySize);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, iatRva, iatSize);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);

        foreach (PeSection section in sections)
        {
            WriteSectionHeader(writer, section);
        }
    }

    private static void WriteDataDirectory(BinaryWriter writer, uint virtualAddress, uint size)
    {
        writer.Write(virtualAddress);
        writer.Write(size);
    }

    private static void WriteSectionHeader(BinaryWriter writer, PeSection section)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(section.Name);
        byte[] paddedName = new byte[8];
        Array.Copy(nameBytes, paddedName, Math.Min(nameBytes.Length, paddedName.Length));
        writer.Write(paddedName);
        writer.Write(section.VirtualSize);
        writer.Write(section.VirtualAddress);
        writer.Write(section.SizeOfRawData);
        writer.Write(section.PointerToRawData);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(section.Characteristics);
    }

    private static void WriteSection(BinaryWriter writer, PeSection section)
    {
        PadTo(writer, section.PointerToRawData);
        writer.Write(section.Content);
        PadTo(writer, section.PointerToRawData + section.SizeOfRawData);
    }

    private static void PadTo(BinaryWriter writer, uint targetOffset)
    {
        while (writer.BaseStream.Position < targetOffset)
        {
            writer.Write((byte)0);
        }
    }

    private static uint Align(uint value, uint alignment) => ((value + alignment - 1) / alignment) * alignment;
}

internal static class PortableExecutableWriter
{
    private const ulong ImageBase = 0x0000000140000000;
    private const uint SectionAlignment = 0x1000;
    private const uint FileAlignment = 0x200;

    public static byte[] WriteExecutable(CodeImage codeImage, SectionImage importSection, SectionImage dataSection)
    {
        PeSection textSection = new(".text", codeImage.Content, 0x60000020);
        PeSection importPeSection = new(".idata", importSection.Content, 0x40000040);
        PeSection dataPeSection = new(".data", dataSection.Content, 0xC0000040);
        List<PeSection> sections = new() { textSection, importPeSection, dataPeSection };

        uint sizeOfHeaders = Align(0x80u + 4u + 20u + 0xF0u + (uint)(sections.Count * 40), FileAlignment);
        uint currentRawPointer = sizeOfHeaders;
        foreach (PeSection section in sections)
        {
            section.PointerToRawData = currentRawPointer;
            section.SizeOfRawData = Align((uint)section.Content.Length, FileAlignment);
            currentRawPointer += section.SizeOfRawData;
        }

        uint currentRva = SectionAlignment;
        foreach (PeSection section in sections)
        {
            section.VirtualAddress = currentRva;
            section.VirtualSize = (uint)section.Content.Length;
            currentRva += Align(section.VirtualSize, SectionAlignment);
        }

        SectionImage fixedImportSection = PatchImportSection(importSection, importPeSection.VirtualAddress);
        importPeSection.Content = fixedImportSection.Content;

        uint importDescriptorRva = importPeSection.VirtualAddress + (uint)fixedImportSection.Labels["import_descriptor"];
        uint importDirectorySize = 40u;
        uint iatRva = importPeSection.VirtualAddress + (uint)fixedImportSection.Labels["GetStdHandle_iat"];
        uint iatSize = 40u;
        uint sizeOfImage = currentRva;

        textSection.Content = PatchTextSection(codeImage, fixedImportSection, dataSection, textSection.VirtualAddress, importPeSection.VirtualAddress, dataPeSection.VirtualAddress);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        WriteDosHeader(writer);
        WritePeHeaders(writer, sections, sizeOfHeaders, sizeOfImage, importDescriptorRva, importDirectorySize, iatRva, iatSize);
        PadTo(writer, sizeOfHeaders);

        foreach (PeSection section in sections)
        {
            WriteSection(writer, section);
        }

        return stream.ToArray();
    }

    private static SectionImage PatchImportSection(SectionImage section, uint sectionRva)
    {
        byte[] content = (byte[])section.Content.Clone();
        foreach (LabelReference reference in section.References)
        {
            uint targetRva = sectionRva + (uint)section.Labels[reference.LabelName];
            if (reference.Width == 4)
            {
                Array.Copy(BitConverter.GetBytes(targetRva), 0, content, reference.Offset, 4);
            }
            else
            {
                Array.Copy(BitConverter.GetBytes((ulong)targetRva), 0, content, reference.Offset, 8);
            }
        }

        return new SectionImage(content, section.Labels, []);
    }

    private static byte[] PatchTextSection(CodeImage codeImage, SectionImage importSection, SectionImage dataSection, uint textRva, uint importRva, uint dataRva)
    {
        byte[] content = (byte[])codeImage.Content.Clone();
        foreach (TextPatch patch in codeImage.Patches)
        {
            uint targetRva;
            if (codeImage.Labels.TryGetValue(patch.LabelName, out int textOffset))
            {
                targetRva = textRva + (uint)textOffset;
            }
            else if (importSection.Labels.TryGetValue(patch.LabelName, out int importOffset))
            {
                targetRva = importRva + (uint)importOffset;
            }
            else if (dataSection.Labels.TryGetValue(patch.LabelName, out int dataOffset))
            {
                targetRva = dataRva + (uint)dataOffset;
            }
            else
            {
                throw new InvalidOperationException($"Unknown patch target '{patch.LabelName}'.");
            }

            uint sourceNextRva = textRva + (uint)patch.NextInstructionOffset;
            int displacement = unchecked((int)(targetRva - sourceNextRva));
            Array.Copy(BitConverter.GetBytes(displacement), 0, content, patch.PatchOffset, 4);
        }

        return content;
    }

    private static void WriteDosHeader(BinaryWriter writer)
    {
        writer.Write((ushort)0x5A4D);
        writer.Write(new byte[58]);
        writer.Write(0x80);
        writer.Write(new byte[0x80 - 64]);
    }

    private static void WritePeHeaders(
        BinaryWriter writer,
        List<PeSection> sections,
        uint sizeOfHeaders,
        uint sizeOfImage,
        uint importDescriptorRva,
        uint importDirectorySize,
        uint iatRva,
        uint iatSize)
    {
        writer.Write(Encoding.ASCII.GetBytes("PE\0\0"));
        writer.Write((ushort)0x8664);
        writer.Write((ushort)sections.Count);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0xF0);
        writer.Write((ushort)0x0022);

        writer.Write((ushort)0x20B);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write(sections[0].SizeOfRawData);
        writer.Write(sections[1].SizeOfRawData + sections[2].SizeOfRawData);
        writer.Write(0u);
        writer.Write(sections[0].VirtualAddress);
        writer.Write(sections[0].VirtualAddress);
        writer.Write(ImageBase);
        writer.Write(SectionAlignment);
        writer.Write(FileAlignment);
        writer.Write((ushort)6);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)6);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(sizeOfImage);
        writer.Write(sizeOfHeaders);
        writer.Write(0u);
        writer.Write((ushort)3);
        writer.Write((ushort)0x8100);
        writer.Write((ulong)0x100000);
        writer.Write((ulong)0x1000);
        writer.Write((ulong)0x100000);
        writer.Write((ulong)0x1000);
        writer.Write(0u);
        writer.Write(16u);

        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, importDescriptorRva, importDirectorySize);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, iatRva, iatSize);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);
        WriteDataDirectory(writer, 0u, 0u);

        foreach (PeSection section in sections)
        {
            WriteSectionHeader(writer, section);
        }
    }

    private static void WriteDataDirectory(BinaryWriter writer, uint virtualAddress, uint size)
    {
        writer.Write(virtualAddress);
        writer.Write(size);
    }

    private static void WriteSectionHeader(BinaryWriter writer, PeSection section)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(section.Name);
        byte[] paddedName = new byte[8];
        Array.Copy(nameBytes, paddedName, Math.Min(nameBytes.Length, paddedName.Length));
        writer.Write(paddedName);
        writer.Write(section.VirtualSize);
        writer.Write(section.VirtualAddress);
        writer.Write(section.SizeOfRawData);
        writer.Write(section.PointerToRawData);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(section.Characteristics);
    }

    private static void WriteSection(BinaryWriter writer, PeSection section)
    {
        PadTo(writer, section.PointerToRawData);
        writer.Write(section.Content);
        PadTo(writer, section.PointerToRawData + section.SizeOfRawData);
    }

    private static void PadTo(BinaryWriter writer, uint targetOffset)
    {
        while (writer.BaseStream.Position < targetOffset)
        {
            writer.Write((byte)0);
        }
    }

    private static uint Align(uint value, uint alignment) => ((value + alignment - 1) / alignment) * alignment;
}

internal sealed class PeSection
{
    public PeSection(string name, byte[] content, uint characteristics)
    {
        Name = name;
        Content = content;
        Characteristics = characteristics;
    }

    public string Name { get; }
    public byte[] Content { get; set; }
    public uint Characteristics { get; }
    public uint VirtualSize { get; set; }
    public uint VirtualAddress { get; set; }
    public uint SizeOfRawData { get; set; }
    public uint PointerToRawData { get; set; }
}

internal sealed record CodeImage(byte[] Content, Dictionary<string, int> Labels, List<TextPatch> Patches);

internal sealed record TextPatch(string LabelName, int PatchOffset, int NextInstructionOffset);

internal sealed record X86CodeImage(byte[] Content, Dictionary<string, int> Labels, List<X86TextPatch> Patches);

internal sealed record X86TextPatch(string LabelName, int PatchOffset, int NextInstructionOffset, X86PatchKind Kind);

internal enum X86PatchKind
{
    Relative32,
    Absolute32
}

internal enum AssemblerRegister
{
    Rax = 0,
    Rcx = 1,
    Rdx = 2,
    Rbx = 3,
    Rsp = 4,
    Rbp = 5,
    Rsi = 6,
    Rdi = 7,
    R8 = 8,
    R9 = 9,
    R10 = 10,
    R11 = 11,
    R12 = 12,
    R13 = 13,
    R14 = 14,
    R15 = 15
}

internal enum X86Register
{
    Eax = 0,
    Ecx = 1,
    Edx = 2,
    Ebx = 3,
    Esp = 4,
    Ebp = 5,
    Esi = 6,
    Edi = 7
}

internal sealed class X86Assembler
{
    private readonly List<byte> _bytes = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private readonly List<X86TextPatch> _patches = [];

    public void Label(string name) => _labels[name] = _bytes.Count;

    public void PushImmediate32(int value)
    {
        EmitByte(0x68);
        EmitInt32(value);
    }

    public void PushLabelAddress(string labelName)
    {
        EmitByte(0x68);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4, X86PatchKind.Absolute32);
        EmitInt32(0);
    }

    public void PushReg(X86Register register) => EmitByte((byte)(0x50 + (int)register));

    public void MovRegImmediate32(X86Register register, int value)
    {
        EmitByte((byte)(0xB8 + (int)register));
        EmitInt32(value);
    }

    public void MovRegLabelAddress(X86Register register, string labelName)
    {
        EmitByte((byte)(0xB8 + (int)register));
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4, X86PatchKind.Absolute32);
        EmitInt32(0);
    }

    public void MovRegReg(X86Register destination, X86Register source)
    {
        EmitBytes(0x89, BuildModRm(0b11, (int)source, (int)destination));
    }

    public void MovRegDwordPtrLabel(X86Register destination, string labelName)
    {
        EmitBytes(0x8B, BuildModRm(0b00, (int)destination, 0b101));
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4, X86PatchKind.Absolute32);
        EmitInt32(0);
    }

    public void MovDwordPtrLabelReg(string labelName, X86Register source)
    {
        EmitBytes(0x89, BuildModRm(0b00, (int)source, 0b101));
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4, X86PatchKind.Absolute32);
        EmitInt32(0);
    }

    public void CallIat(string labelName)
    {
        EmitBytes(0xFF, 0x15);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4, X86PatchKind.Absolute32);
        EmitInt32(0);
    }

    public void AddBytePtrEbxImmediate(byte value) => EmitBytes(0x80, 0x03, value);

    public void SubBytePtrEbxImmediate(byte value) => EmitBytes(0x80, 0x2B, value);

    public void AddReg32Immediate(X86Register register, int value)
    {
        EmitBytes(0x81, BuildModRm(0b11, 0, (int)register));
        EmitInt32(value);
    }

    public void SubReg32Immediate(X86Register register, int value)
    {
        EmitBytes(0x81, BuildModRm(0b11, 5, (int)register));
        EmitInt32(value);
    }

    public void CmpRegReg(X86Register left, X86Register right)
    {
        EmitBytes(0x39, BuildModRm(0b11, (int)right, (int)left));
    }

    public void JumpEqual(string labelName) => EmitRel32Branch(0x84, labelName);

    public void JumpNotEqual(string labelName) => EmitRel32Branch(0x85, labelName);

    public void JumpBelow(string labelName) => EmitRel32Branch(0x82, labelName);

    public void JumpAboveOrEqual(string labelName) => EmitRel32Branch(0x83, labelName);

    public void Jump(string labelName)
    {
        EmitByte(0xE9);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4, X86PatchKind.Relative32);
        EmitInt32(0);
    }

    public void CmpBytePtrEbxImmediate(byte value) => EmitBytes(0x80, 0x3B, value);

    public void TestEaxEax() => EmitBytes(0x85, 0xC0);

    public void CmpDwordPtrLabelImmediate(string labelName, byte value)
    {
        EmitBytes(0x83, 0x3D);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4, X86PatchKind.Absolute32);
        EmitInt32(0);
        EmitByte(value);
    }

    public void MovBytePtrEbxImmediate(byte value) => EmitBytes(0xC6, 0x03, value);

    public X86CodeImage ToImage()
    {
        return new X86CodeImage([.. _bytes], new Dictionary<string, int>(_labels, StringComparer.Ordinal), [.. _patches]);
    }

    private void EmitRel32Branch(byte conditionOpcode, string labelName)
    {
        EmitBytes(0x0F, conditionOpcode);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4, X86PatchKind.Relative32);
        EmitInt32(0);
    }

    private void AddPatch(string labelName, int patchOffset, int nextInstructionOffset, X86PatchKind kind)
    {
        _patches.Add(new X86TextPatch(labelName, patchOffset, nextInstructionOffset, kind));
    }

    private static byte BuildModRm(int mod, int reg, int rm) => (byte)((mod << 6) | (reg << 3) | rm);

    private void EmitBytes(params byte[] bytes) => _bytes.AddRange(bytes);

    private void EmitByte(byte value) => _bytes.Add(value);

    private void EmitInt32(int value) => _bytes.AddRange(BitConverter.GetBytes(value));
}

internal sealed class X64Assembler
{
    private readonly List<byte> _bytes = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private readonly List<TextPatch> _patches = [];

    public void Label(string name) => _labels[name] = _bytes.Count;

    public void SubRsp(byte value) => EmitBytes(0x48, 0x83, 0xEC, value);

    public void MovReg32Immediate(AssemblerRegister register, int value)
    {
        int reg = (int)register;
        EmitRex(false, false, false, reg >= 8);
        EmitByte((byte)(0xB8 + (reg & 7)));
        EmitInt32(value);
    }

    public void MovRegReg(AssemblerRegister destination, AssemblerRegister source)
    {
        EmitRex(true, ((int)source & 8) != 0, false, ((int)destination & 8) != 0);
        EmitBytes(0x89, BuildModRm(0b11, (int)source & 7, (int)destination & 7));
    }

    public void LeaRipLabel(AssemblerRegister destination, string labelName)
    {
        EmitRex(true, ((int)destination & 8) != 0, false, false);
        EmitBytes(0x8D, BuildModRm(0b00, (int)destination & 7, 0b101));
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4);
        EmitInt32(0);
    }

    public void CallIat(string labelName)
    {
        EmitBytes(0xFF, 0x15);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4);
        EmitInt32(0);
    }

    public void XorReg32(AssemblerRegister destination, AssemblerRegister source)
    {
        EmitRex(false, ((int)source & 8) != 0, false, ((int)destination & 8) != 0);
        EmitBytes(0x31, BuildModRm(0b11, (int)source & 7, (int)destination & 7));
    }

    public void AddBytePtrRbxImmediate(byte value) => EmitBytes(0x80, 0x03, value);

    public void SubBytePtrRbxImmediate(byte value) => EmitBytes(0x80, 0x2B, value);

    public void AddReg64Immediate(AssemblerRegister register, int value)
    {
        EmitRex(true, false, false, ((int)register & 8) != 0);
        EmitBytes(0x81, BuildModRm(0b11, 0, (int)register & 7));
        EmitInt32(value);
    }

    public void SubReg64Immediate(AssemblerRegister register, int value)
    {
        EmitRex(true, false, false, ((int)register & 8) != 0);
        EmitBytes(0x81, BuildModRm(0b11, 5, (int)register & 7));
        EmitInt32(value);
    }

    public void CmpRegReg(AssemblerRegister left, AssemblerRegister right)
    {
        EmitRex(true, ((int)right & 8) != 0, false, ((int)left & 8) != 0);
        EmitBytes(0x39, BuildModRm(0b11, (int)right & 7, (int)left & 7));
    }

    public void JumpEqual(string labelName) => EmitRel32Branch(0x84, labelName);

    public void JumpNotEqual(string labelName) => EmitRel32Branch(0x85, labelName);

    public void JumpBelow(string labelName) => EmitRel32Branch(0x82, labelName);

    public void JumpAboveOrEqual(string labelName) => EmitRel32Branch(0x83, labelName);

    public void Jump(string labelName)
    {
        EmitByte(0xE9);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4);
        EmitInt32(0);
    }

    public void CmpBytePtrRbxImmediate(byte value) => EmitBytes(0x80, 0x3B, value);

    public void MovStackQwordImmediate32(byte stackOffset, int value)
    {
        EmitBytes(0x48, 0xC7, 0x44, 0x24, stackOffset);
        EmitInt32(value);
    }

    public void TestEaxEax() => EmitBytes(0x85, 0xC0);

    public void CmpDwordRipLabelImmediate(string labelName, byte value)
    {
        EmitBytes(0x83, 0x3D);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 5);
        EmitInt32(0);
        EmitByte(value);
    }

    public void MovBytePtrRbxImmediate(byte value) => EmitBytes(0xC6, 0x03, value);

    public CodeImage ToImage()
    {
        return new CodeImage([.. _bytes], new Dictionary<string, int>(_labels, StringComparer.Ordinal), [.. _patches]);
    }

    private void EmitRel32Branch(byte conditionOpcode, string labelName)
    {
        EmitBytes(0x0F, conditionOpcode);
        AddPatch(labelName, _bytes.Count, _bytes.Count + 4);
        EmitInt32(0);
    }

    private void AddPatch(string labelName, int patchOffset, int nextInstructionOffset)
    {
        _patches.Add(new TextPatch(labelName, patchOffset, nextInstructionOffset));
    }

    private static byte BuildModRm(int mod, int reg, int rm) => (byte)((mod << 6) | (reg << 3) | rm);

    private void EmitRex(bool w, bool r, bool x, bool b)
    {
        int rex = 0x40
                  | (w ? 0x08 : 0)
                  | (r ? 0x04 : 0)
                  | (x ? 0x02 : 0)
                  | (b ? 0x01 : 0);
        EmitByte((byte)rex);
    }

    private void EmitBytes(params byte[] bytes) => _bytes.AddRange(bytes);

    private void EmitByte(byte value) => _bytes.Add(value);

    private void EmitInt32(int value) => _bytes.AddRange(BitConverter.GetBytes(value));
}
