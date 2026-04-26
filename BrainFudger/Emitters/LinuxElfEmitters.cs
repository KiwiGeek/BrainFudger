using System.Runtime.InteropServices;
using System.Text;
using BrainFudger.Models;

namespace BrainFudger.Emitters;

internal sealed class LinuxX64ElfEmitter : IBinaryEmitter
{
    public static LinuxX64ElfEmitter Instance { get; } = new();

    public string TargetId => "linux-x64";

    public IReadOnlyList<string> Aliases => ["linux-amd64"];

    public string DisplayName => "Linux x64 ELF executable";

    public string DefaultFileExtension => string.Empty;

    public bool CanExecuteOnCurrentPlatform(out string reason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            reason = "The target 'linux-x64' can only be executed with --run on Linux hosts.";
            return false;
        }

        if (RuntimeInformation.ProcessArchitecture is not Architecture.X64)
        {
            reason = $"The target 'linux-x64' cannot be executed with --run on {RuntimeInformation.ProcessArchitecture} hosts.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public byte[] EmitBinary(IntermediateProgram program, CompilerOptions options)
    {
        SectionImage dataSection = LinuxElfRuntime.BuildDataSection(options);
        CodeImage codeImage = BuildCodeImage(program);
        return ElfExecutableWriter.WriteX64Executable(codeImage, dataSection);
    }

    public void PrepareFileForExecution(string outputPath) => LinuxElfRuntime.PrepareFileForExecution(outputPath);

    private static CodeImage BuildCodeImage(IntermediateProgram program)
    {
        X64Assembler assembler = new();
        EmitPrologue(assembler);
        EmitProgram(assembler, program);
        assembler.Jump("program_exit");
        EmitErrorPath(assembler, "error_before", "pointer_before_message");
        EmitErrorPath(assembler, "error_past", "pointer_past_message");
        assembler.Label("program_exit");
        assembler.MovReg32Immediate(AssemblerRegister.Rax, 60);
        assembler.XorReg32(AssemblerRegister.Rdi, AssemblerRegister.Rdi);
        assembler.Syscall();
        return assembler.ToImage();
    }

    private static void EmitPrologue(X64Assembler assembler)
    {
        assembler.LeaRipLabel(AssemblerRegister.Rbx, "tape");
        assembler.MovRegReg(AssemblerRegister.R12, AssemblerRegister.Rbx);
        assembler.LeaRipLabel(AssemblerRegister.R13, "tape_end");
    }

    private static void EmitProgram(X64Assembler assembler, IntermediateProgram program)
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
                    EmitReadByte(assembler, inputCounter++);
                    break;

                case IntermediateOpcode.LoopStart:
                    assembler.Label(GetLoopStartLabel(i));
                    assembler.CmpBytePtrRbxImmediate(0);
                    assembler.JumpEqual(GetLoopEndLabel(instruction.MatchingInstructionIndex));
                    break;

                case IntermediateOpcode.LoopEnd:
                    assembler.CmpBytePtrRbxImmediate(0);
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

    private static void EmitPointerMove(X64Assembler assembler, int count)
    {
        if (count > 0)
        {
            assembler.AddReg64Immediate(AssemblerRegister.Rbx, count);
            assembler.CmpRegReg(AssemblerRegister.Rbx, AssemblerRegister.R13);
            assembler.JumpAboveOrEqual("error_past");
            return;
        }

        if (count < 0)
        {
            int distance = -count;
            assembler.MovRegReg(AssemblerRegister.Rax, AssemblerRegister.R12);
            assembler.AddReg64Immediate(AssemblerRegister.Rax, distance);
            assembler.CmpRegReg(AssemblerRegister.Rbx, AssemblerRegister.Rax);
            assembler.JumpBelow("error_before");
            assembler.SubReg64Immediate(AssemblerRegister.Rbx, distance);
        }
    }

    private static void EmitAddToCell(X64Assembler assembler, int count)
    {
        if (count > 0)
        {
            assembler.AddBytePtrRbxImmediate((byte)(count & 0xFF));
        }
        else if (count < 0)
        {
            assembler.SubBytePtrRbxImmediate((byte)((-count) & 0xFF));
        }
    }

    private static void EmitWriteByte(X64Assembler assembler)
    {
        assembler.MovReg32Immediate(AssemblerRegister.Rax, 1);
        assembler.MovReg32Immediate(AssemblerRegister.Rdi, 1);
        assembler.MovRegReg(AssemblerRegister.Rsi, AssemblerRegister.Rbx);
        assembler.MovReg32Immediate(AssemblerRegister.Rdx, 1);
        assembler.Syscall();
    }

    private static void EmitReadByte(X64Assembler assembler, int inputIndex)
    {
        string zeroLabel = $"input_zero_{inputIndex}";
        string doneLabel = $"input_done_{inputIndex}";

        assembler.XorReg32(AssemblerRegister.Rax, AssemblerRegister.Rax);
        assembler.XorReg32(AssemblerRegister.Rdi, AssemblerRegister.Rdi);
        assembler.MovRegReg(AssemblerRegister.Rsi, AssemblerRegister.Rbx);
        assembler.MovReg32Immediate(AssemblerRegister.Rdx, 1);
        assembler.Syscall();
        assembler.TestEaxEax();
        assembler.JumpEqual(zeroLabel);
        assembler.Jump(doneLabel);
        assembler.Label(zeroLabel);
        assembler.MovBytePtrRbxImmediate(0);
        assembler.Label(doneLabel);
    }

    private static void EmitRandomByte(X64Assembler assembler)
    {
        assembler.LeaRipLabel(AssemblerRegister.Rcx, EmitterRuntimeSupport.RngStateLabel);
        assembler.XorReg32(AssemblerRegister.Rax, AssemblerRegister.Rax);
        assembler.MovAlBytePtrReg(AssemblerRegister.Rcx);
        assembler.MovRegReg(AssemblerRegister.Rdx, AssemblerRegister.Rax);
        assembler.ShiftLeftReg32Immediate(AssemblerRegister.Rax, 4);
        assembler.AddReg32Reg32(AssemblerRegister.Rax, AssemblerRegister.Rdx);
        assembler.AddReg64Immediate(AssemblerRegister.Rax, 29);
        assembler.MovBytePtrRegAl(AssemblerRegister.Rcx);
        assembler.MovBytePtrRegAl(AssemblerRegister.Rbx);
    }

    private static void EmitClearTerminal(X64Assembler assembler)
    {
        assembler.MovReg32Immediate(AssemblerRegister.Rax, 1);
        assembler.MovReg32Immediate(AssemblerRegister.Rdi, 1);
        assembler.LeaRipLabel(AssemblerRegister.Rsi, EmitterRuntimeSupport.ClearTerminalLabel);
        assembler.MovReg32Immediate(AssemblerRegister.Rdx, EmitterRuntimeSupport.ClearTerminalLength);
        assembler.Syscall();
    }

    private static void EmitDelay(X64Assembler assembler, int instructionIndex)
    {
        string doneLabel = $"delay_done_{instructionIndex}";
        string outerLabel = $"delay_outer_{instructionIndex}";
        string innerLabel = $"delay_inner_{instructionIndex}";

        assembler.XorReg32(AssemblerRegister.Rax, AssemblerRegister.Rax);
        assembler.MovAlBytePtrReg(AssemblerRegister.Rbx);
        assembler.TestEaxEax();
        assembler.JumpEqual(doneLabel);
        assembler.Label(outerLabel);
        assembler.MovReg32Immediate(AssemblerRegister.Rcx, EmitterRuntimeSupport.DelayInnerLoopCount);
        assembler.Label(innerLabel);
        assembler.SubReg64Immediate(AssemblerRegister.Rcx, 1);
        assembler.JumpNotEqual(innerLabel);
        assembler.SubReg64Immediate(AssemblerRegister.Rax, 1);
        assembler.JumpNotEqual(outerLabel);
        assembler.Label(doneLabel);
    }

    private static void EmitErrorPath(X64Assembler assembler, string label, string messageLabel)
    {
        assembler.Label(label);
        assembler.MovReg32Immediate(AssemblerRegister.Rax, 1);
        assembler.MovReg32Immediate(AssemblerRegister.Rdi, 2);
        assembler.LeaRipLabel(AssemblerRegister.Rsi, messageLabel);
        assembler.MovReg32Immediate(AssemblerRegister.Rdx, LinuxElfRuntime.GetMessageLength(messageLabel));
        assembler.Syscall();
        assembler.MovReg32Immediate(AssemblerRegister.Rax, 60);
        assembler.MovReg32Immediate(AssemblerRegister.Rdi, 1);
        assembler.Syscall();
    }

    private static string GetLoopStartLabel(int instructionIndex) => $"loop_start_{instructionIndex}";

    private static string GetLoopEndLabel(int instructionIndex) => $"loop_end_{instructionIndex}";
}

internal sealed class LinuxX86ElfEmitter : IBinaryEmitter
{
    public static LinuxX86ElfEmitter Instance { get; } = new();

    public string TargetId => "linux-x86";

    public IReadOnlyList<string> Aliases => ["linux-i386"];

    public string DisplayName => "Linux x86 ELF executable";

    public string DefaultFileExtension => string.Empty;

    public bool CanExecuteOnCurrentPlatform(out string reason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            reason = "The target 'linux-x86' can only be executed with --run on Linux hosts.";
            return false;
        }

        if (RuntimeInformation.ProcessArchitecture is not Architecture.X86 and not Architecture.X64)
        {
            reason = $"The target 'linux-x86' cannot be executed with --run on {RuntimeInformation.ProcessArchitecture} hosts.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public byte[] EmitBinary(IntermediateProgram program, CompilerOptions options)
    {
        SectionImage dataSection = LinuxElfRuntime.BuildDataSection(options);
        X86CodeImage codeImage = BuildCodeImage(program);
        return ElfExecutableWriter.WriteX86Executable(codeImage, dataSection);
    }

    public void PrepareFileForExecution(string outputPath) => LinuxElfRuntime.PrepareFileForExecution(outputPath);

    private static X86CodeImage BuildCodeImage(IntermediateProgram program)
    {
        X86Assembler assembler = new();
        EmitPrologue(assembler);
        EmitProgram(assembler, program);
        assembler.Jump("program_exit");
        EmitErrorPath(assembler, "error_before", "pointer_before_message");
        EmitErrorPath(assembler, "error_past", "pointer_past_message");
        assembler.Label("program_exit");
        assembler.MovRegImmediate32(X86Register.Eax, 1);
        assembler.MovRegImmediate32(X86Register.Ebx, 0);
        assembler.Int80();
        return assembler.ToImage();
    }

    private static void EmitPrologue(X86Assembler assembler)
    {
        assembler.MovRegLabelAddress(X86Register.Ebx, "tape");
        assembler.MovRegReg(X86Register.Esi, X86Register.Ebx);
        assembler.MovRegLabelAddress(X86Register.Edi, "tape_end");
    }

    private static void EmitProgram(X86Assembler assembler, IntermediateProgram program)
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
                    EmitReadByte(assembler, inputCounter++);
                    break;

                case IntermediateOpcode.LoopStart:
                    assembler.Label(GetLoopStartLabel(i));
                    assembler.CmpBytePtrEbxImmediate(0);
                    assembler.JumpEqual(GetLoopEndLabel(instruction.MatchingInstructionIndex));
                    break;

                case IntermediateOpcode.LoopEnd:
                    assembler.CmpBytePtrEbxImmediate(0);
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

    private static void EmitPointerMove(X86Assembler assembler, int count)
    {
        if (count > 0)
        {
            assembler.AddReg32Immediate(X86Register.Ebx, count);
            assembler.CmpRegReg(X86Register.Ebx, X86Register.Edi);
            assembler.JumpAboveOrEqual("error_past");
            return;
        }

        if (count < 0)
        {
            int distance = -count;
            assembler.MovRegReg(X86Register.Eax, X86Register.Esi);
            assembler.AddReg32Immediate(X86Register.Eax, distance);
            assembler.CmpRegReg(X86Register.Ebx, X86Register.Eax);
            assembler.JumpBelow("error_before");
            assembler.SubReg32Immediate(X86Register.Ebx, distance);
        }
    }

    private static void EmitAddToCell(X86Assembler assembler, int count)
    {
        if (count > 0)
        {
            assembler.AddBytePtrEbxImmediate((byte)(count & 0xFF));
        }
        else if (count < 0)
        {
            assembler.SubBytePtrEbxImmediate((byte)((-count) & 0xFF));
        }
    }

    private static void EmitWriteByte(X86Assembler assembler)
    {
        assembler.PushReg(X86Register.Ebx);
        assembler.MovRegReg(X86Register.Ecx, X86Register.Ebx);
        assembler.MovRegImmediate32(X86Register.Edx, 1);
        assembler.MovRegImmediate32(X86Register.Ebx, 1);
        assembler.MovRegImmediate32(X86Register.Eax, 4);
        assembler.Int80();
        assembler.PopReg(X86Register.Ebx);
    }

    private static void EmitReadByte(X86Assembler assembler, int inputIndex)
    {
        string zeroLabel = $"input_zero_{inputIndex}";
        string doneLabel = $"input_done_{inputIndex}";

        assembler.PushReg(X86Register.Ebx);
        assembler.MovRegReg(X86Register.Ecx, X86Register.Ebx);
        assembler.MovRegImmediate32(X86Register.Edx, 1);
        assembler.MovRegImmediate32(X86Register.Ebx, 0);
        assembler.MovRegImmediate32(X86Register.Eax, 3);
        assembler.Int80();
        assembler.PopReg(X86Register.Ebx);
        assembler.TestEaxEax();
        assembler.JumpEqual(zeroLabel);
        assembler.Jump(doneLabel);
        assembler.Label(zeroLabel);
        assembler.MovBytePtrEbxImmediate(0);
        assembler.Label(doneLabel);
    }

    private static void EmitRandomByte(X86Assembler assembler)
    {
        assembler.MovRegLabelAddress(X86Register.Ecx, EmitterRuntimeSupport.RngStateLabel);
        assembler.XorRegReg(X86Register.Eax, X86Register.Eax);
        assembler.MovAlBytePtrReg(X86Register.Ecx);
        assembler.MovRegReg(X86Register.Edx, X86Register.Eax);
        assembler.ShiftLeftRegImmediate(X86Register.Eax, 4);
        assembler.AddRegReg(X86Register.Eax, X86Register.Edx);
        assembler.AddReg32Immediate(X86Register.Eax, 29);
        assembler.MovBytePtrRegAl(X86Register.Ecx);
        assembler.MovBytePtrRegAl(X86Register.Ebx);
    }

    private static void EmitClearTerminal(X86Assembler assembler)
    {
        assembler.PushReg(X86Register.Ebx);
        assembler.MovRegLabelAddress(X86Register.Ecx, EmitterRuntimeSupport.ClearTerminalLabel);
        assembler.MovRegImmediate32(X86Register.Edx, EmitterRuntimeSupport.ClearTerminalLength);
        assembler.MovRegImmediate32(X86Register.Ebx, 1);
        assembler.MovRegImmediate32(X86Register.Eax, 4);
        assembler.Int80();
        assembler.PopReg(X86Register.Ebx);
    }

    private static void EmitDelay(X86Assembler assembler, int instructionIndex)
    {
        string doneLabel = $"delay_done_{instructionIndex}";
        string outerLabel = $"delay_outer_{instructionIndex}";
        string innerLabel = $"delay_inner_{instructionIndex}";

        assembler.XorRegReg(X86Register.Eax, X86Register.Eax);
        assembler.MovAlBytePtrReg(X86Register.Ebx);
        assembler.TestEaxEax();
        assembler.JumpEqual(doneLabel);
        assembler.Label(outerLabel);
        assembler.MovRegImmediate32(X86Register.Ecx, EmitterRuntimeSupport.DelayInnerLoopCount);
        assembler.Label(innerLabel);
        assembler.SubReg32Immediate(X86Register.Ecx, 1);
        assembler.JumpNotEqual(innerLabel);
        assembler.SubReg32Immediate(X86Register.Eax, 1);
        assembler.JumpNotEqual(outerLabel);
        assembler.Label(doneLabel);
    }

    private static void EmitErrorPath(X86Assembler assembler, string label, string messageLabel)
    {
        assembler.Label(label);
        assembler.MovRegImmediate32(X86Register.Eax, 4);
        assembler.MovRegImmediate32(X86Register.Ebx, 2);
        assembler.MovRegLabelAddress(X86Register.Ecx, messageLabel);
        assembler.MovRegImmediate32(X86Register.Edx, LinuxElfRuntime.GetMessageLength(messageLabel));
        assembler.Int80();
        assembler.MovRegImmediate32(X86Register.Eax, 1);
        assembler.MovRegImmediate32(X86Register.Ebx, 1);
        assembler.Int80();
    }

    private static string GetLoopStartLabel(int instructionIndex) => $"loop_start_{instructionIndex}";

    private static string GetLoopEndLabel(int instructionIndex) => $"loop_end_{instructionIndex}";
}

internal sealed class LinuxArm64ElfEmitter : IBinaryEmitter
{
    public static LinuxArm64ElfEmitter Instance { get; } = new();

    public string TargetId => "linux-arm64";

    public IReadOnlyList<string> Aliases => ["linux-aarch64"];

    public string DisplayName => "Linux arm64 ELF executable";

    public string DefaultFileExtension => string.Empty;

    public bool CanExecuteOnCurrentPlatform(out string reason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            reason = "The target 'linux-arm64' can only be executed with --run on Linux hosts.";
            return false;
        }

        if (RuntimeInformation.ProcessArchitecture is not Architecture.Arm64)
        {
            reason = $"The target 'linux-arm64' cannot be executed with --run on {RuntimeInformation.ProcessArchitecture} hosts.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public byte[] EmitBinary(IntermediateProgram program, CompilerOptions options)
    {
        SectionImage dataSection = LinuxElfRuntime.BuildDataSection(options);
        Arm64CodeImage codeImage = BuildCodeImage(program);
        return ElfExecutableWriter.WriteArm64Executable(codeImage, dataSection);
    }

    public void PrepareFileForExecution(string outputPath) => LinuxElfRuntime.PrepareFileForExecution(outputPath);

    private static Arm64CodeImage BuildCodeImage(IntermediateProgram program)
    {
        Arm64Assembler assembler = new();
        EmitPrologue(assembler);
        EmitProgram(assembler, program);
        assembler.Branch("program_exit");
        EmitErrorPath(assembler, "error_before", "pointer_before_message");
        EmitErrorPath(assembler, "error_past", "pointer_past_message");
        assembler.Label("program_exit");
        assembler.MovImmediate64(0, 0);
        assembler.MovImmediate64(8, 93);
        assembler.Svc(0);
        return assembler.ToImage();
    }

    private static void EmitPrologue(Arm64Assembler assembler)
    {
        assembler.AdrpAddLabel(19, "tape");
        assembler.AdrpAddLabel(20, "tape");
        assembler.AdrpAddLabel(21, "tape_end");
    }

    private static void EmitProgram(Arm64Assembler assembler, IntermediateProgram program)
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
                    EmitReadByte(assembler, inputCounter++);
                    break;

                case IntermediateOpcode.LoopStart:
                    assembler.Label(GetLoopStartLabel(i));
                    assembler.LoadByte(9, 19);
                    assembler.CompareImmediate32(9, 0);
                    assembler.BranchConditional(GetLoopEndLabel(instruction.MatchingInstructionIndex), Arm64Condition.Equal);
                    break;

                case IntermediateOpcode.LoopEnd:
                    assembler.LoadByte(9, 19);
                    assembler.CompareImmediate32(9, 0);
                    assembler.BranchConditional(GetLoopStartLabel(instruction.MatchingInstructionIndex), Arm64Condition.NotEqual);
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

    private static void EmitPointerMove(Arm64Assembler assembler, int count)
    {
        if (count > 0)
        {
            LinuxArm64Runtime.MovePointer(assembler, count, moveRight: true);
            return;
        }

        if (count < 0)
        {
            LinuxArm64Runtime.MovePointer(assembler, -count, moveRight: false);
        }
    }

    private static void EmitAddToCell(Arm64Assembler assembler, int count)
    {
        if (count > 0)
        {
            LinuxArm64Runtime.AddToCell(assembler, count & 0xFF);
        }
        else if (count < 0)
        {
            LinuxArm64Runtime.SubtractFromCell(assembler, (-count) & 0xFF);
        }
    }

    private static void EmitWriteByte(Arm64Assembler assembler)
    {
        assembler.MovImmediate64(0, 1);
        assembler.MoveRegister64(1, 19);
        assembler.MovImmediate64(2, 1);
        assembler.MovImmediate64(8, 64);
        assembler.Svc(0);
    }

    private static void EmitReadByte(Arm64Assembler assembler, int inputIndex)
    {
        string zeroLabel = $"input_zero_{inputIndex}";
        string doneLabel = $"input_done_{inputIndex}";

        assembler.MovImmediate64(0, 0);
        assembler.MoveRegister64(1, 19);
        assembler.MovImmediate64(2, 1);
        assembler.MovImmediate64(8, 63);
        assembler.Svc(0);
        assembler.CompareImmediate64(0, 0);
        assembler.BranchConditional(zeroLabel, Arm64Condition.LessOrEqual);
        assembler.Branch(doneLabel);
        assembler.Label(zeroLabel);
        assembler.MovImmediate64(9, 0);
        assembler.StoreByte(9, 19);
        assembler.Label(doneLabel);
    }

    private static void EmitRandomByte(Arm64Assembler assembler)
    {
        assembler.AdrpAddLabel(22, EmitterRuntimeSupport.RngStateLabel);
        assembler.LoadByte(9, 22);
        assembler.MoveRegister64(10, 9);
        assembler.AddRegister32(9, 9, 9);
        assembler.AddRegister32(9, 9, 9);
        assembler.AddRegister32(9, 9, 9);
        assembler.AddRegister32(9, 9, 9);
        assembler.AddRegister32(9, 9, 10);
        assembler.AddImmediate32(9, 9, 29);
        assembler.StoreByte(9, 22);
        assembler.StoreByte(9, 19);
    }

    private static void EmitClearTerminal(Arm64Assembler assembler)
    {
        assembler.MovImmediate64(0, 1);
        assembler.AdrpAddLabel(1, EmitterRuntimeSupport.ClearTerminalLabel);
        assembler.MovImmediate64(2, EmitterRuntimeSupport.ClearTerminalLength);
        assembler.MovImmediate64(8, 64);
        assembler.Svc(0);
    }

    private static void EmitDelay(Arm64Assembler assembler, int instructionIndex)
    {
        string doneLabel = $"delay_done_{instructionIndex}";
        string outerLabel = $"delay_outer_{instructionIndex}";
        string innerLabel = $"delay_inner_{instructionIndex}";

        assembler.LoadByte(9, 19);
        assembler.CompareImmediate32(9, 0);
        assembler.BranchConditional(doneLabel, Arm64Condition.Equal);
        assembler.Label(outerLabel);
        assembler.MovImmediate64(10, EmitterRuntimeSupport.DelayInnerLoopCount);
        assembler.Label(innerLabel);
        assembler.SubtractImmediate64(10, 10, 1);
        assembler.CompareImmediate64(10, 0);
        assembler.BranchConditional(innerLabel, Arm64Condition.NotEqual);
        assembler.SubtractImmediate32(9, 9, 1);
        assembler.CompareImmediate32(9, 0);
        assembler.BranchConditional(outerLabel, Arm64Condition.NotEqual);
        assembler.Label(doneLabel);
    }

    private static void EmitErrorPath(Arm64Assembler assembler, string label, string messageLabel)
    {
        assembler.Label(label);
        assembler.MovImmediate64(0, 2);
        assembler.AdrpAddLabel(1, messageLabel);
        assembler.MovImmediate64(2, LinuxElfRuntime.GetMessageLength(messageLabel));
        assembler.MovImmediate64(8, 64);
        assembler.Svc(0);
        assembler.MovImmediate64(0, 1);
        assembler.MovImmediate64(8, 93);
        assembler.Svc(0);
    }

    private static string GetLoopStartLabel(int instructionIndex) => $"loop_start_{instructionIndex}";

    private static string GetLoopEndLabel(int instructionIndex) => $"loop_end_{instructionIndex}";
}

internal static class LinuxElfRuntime
{
    public static SectionImage BuildDataSection(CompilerOptions options)
    {
        SectionBuilder builder = new();
        builder.Align(16);
        builder.DefineLabel("tape");
        builder.WriteZeros(options.CellCount);
        builder.DefineLabel("tape_end");
        builder.Align(8);
        builder.DefineLabel(EmitterRuntimeSupport.RngStateLabel);
        builder.WriteZeros(1);
        builder.DefineLabel(EmitterRuntimeSupport.ClearTerminalLabel);
        builder.WriteBytes(EmitterRuntimeSupport.ClearTerminalSequence);
        builder.DefineAsciiString("pointer_before_message", "Pointer moved before the beginning of the tape.\n");
        builder.DefineAsciiString("pointer_past_message", "Pointer moved past the end of the tape.\n");
        return builder.ToImage();
    }

    public static int GetMessageLength(string messageLabel) =>
        messageLabel switch
        {
            "pointer_before_message" => Encoding.ASCII.GetByteCount("Pointer moved before the beginning of the tape.\n"),
            "pointer_past_message" => Encoding.ASCII.GetByteCount("Pointer moved past the end of the tape.\n"),
            _ => throw new InvalidOperationException($"Unknown message label '{messageLabel}'.")
        };

    public static void PrepareFileForExecution(string outputPath)
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
}

internal static class LinuxArm64Runtime
{
    public static void AddToCell(Arm64Assembler assembler, int value)
    {
        if (value == 0)
        {
            return;
        }

        assembler.LoadByte(9, 19);
        assembler.AddImmediate32(9, 9, value);
        assembler.StoreByte(9, 19);
    }

    public static void SubtractFromCell(Arm64Assembler assembler, int value)
    {
        if (value == 0)
        {
            return;
        }

        assembler.LoadByte(9, 19);
        assembler.SubtractImmediate32(9, 9, value);
        assembler.StoreByte(9, 19);
    }

    public static void MovePointer(Arm64Assembler assembler, int count, bool moveRight)
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
}

internal static class ElfExecutableWriter
{
    private const uint PageAlignment = 0x1000;
    private const ulong Elf64BaseAddress = 0x00400000;
    private const uint Elf32BaseAddress = 0x08048000;

    public static byte[] WriteX64Executable(CodeImage codeImage, SectionImage dataSection)
    {
        const ushort machine = 0x3E;
        const ulong baseAddress = Elf64BaseAddress;
        const uint headerSize = 64;
        const uint programHeaderSize = 56;
        return WriteElf64Executable(
            machine,
            baseAddress,
            headerSize,
            programHeaderSize,
            PatchX64Code(codeImage, dataSection, baseAddress, headerSize, programHeaderSize),
            dataSection.Content);
    }

    public static byte[] WriteArm64Executable(Arm64CodeImage codeImage, SectionImage dataSection)
    {
        const ushort machine = 0xB7;
        const ulong baseAddress = Elf64BaseAddress;
        const uint headerSize = 64;
        const uint programHeaderSize = 56;
        return WriteElf64Executable(
            machine,
            baseAddress,
            headerSize,
            programHeaderSize,
            PatchArm64Code(codeImage, dataSection, baseAddress, headerSize, programHeaderSize),
            dataSection.Content);
    }

    public static byte[] WriteX86Executable(X86CodeImage codeImage, SectionImage dataSection)
    {
        const ushort machine = 0x03;
        const uint headerSize = 52;
        const uint programHeaderSize = 32;
        return WriteElf32Executable(
            machine,
            Elf32BaseAddress,
            headerSize,
            programHeaderSize,
            PatchX86Code(codeImage, dataSection, Elf32BaseAddress, headerSize, programHeaderSize),
            dataSection.Content);
    }

    private static byte[] WriteElf64Executable(ushort machine, ulong baseAddress, uint headerSize, uint programHeaderSize, byte[] codeBytes, byte[] dataBytes)
    {
        uint codeOffset = Align(headerSize + programHeaderSize, PageAlignment);
        uint dataOffset = Align(codeOffset + (uint)codeBytes.Length, 16);
        ulong entryPoint = baseAddress + codeOffset;
        uint fileSize = dataOffset + (uint)dataBytes.Length;

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        WriteElf64Header(writer, machine, entryPoint, headerSize, programHeaderSize);
        WriteElf64ProgramHeader(writer, baseAddress, fileSize);
        PadTo(writer, codeOffset);
        writer.Write(codeBytes);
        PadTo(writer, dataOffset);
        writer.Write(dataBytes);

        return stream.ToArray();
    }

    private static byte[] WriteElf32Executable(ushort machine, uint baseAddress, uint headerSize, uint programHeaderSize, byte[] codeBytes, byte[] dataBytes)
    {
        uint codeOffset = Align(headerSize + programHeaderSize, PageAlignment);
        uint dataOffset = Align(codeOffset + (uint)codeBytes.Length, 16);
        uint entryPoint = baseAddress + codeOffset;
        uint fileSize = dataOffset + (uint)dataBytes.Length;

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        WriteElf32Header(writer, machine, entryPoint, headerSize, programHeaderSize);
        WriteElf32ProgramHeader(writer, baseAddress, fileSize);
        PadTo(writer, codeOffset);
        writer.Write(codeBytes);
        PadTo(writer, dataOffset);
        writer.Write(dataBytes);

        return stream.ToArray();
    }

    private static byte[] PatchX64Code(CodeImage codeImage, SectionImage dataSection, ulong baseAddress, uint headerSize, uint programHeaderSize)
    {
        uint codeOffset = Align(headerSize + programHeaderSize, PageAlignment);
        uint dataOffset = Align(codeOffset + (uint)codeImage.Content.Length, 16);
        ulong codeAddress = baseAddress + codeOffset;
        ulong dataAddress = baseAddress + dataOffset;
        byte[] content = (byte[])codeImage.Content.Clone();

        foreach (TextPatch patch in codeImage.Patches)
        {
            ulong targetAddress = Resolve64Target(codeImage.Labels, dataSection.Labels, patch.LabelName, codeAddress, dataAddress);
            ulong sourceNextAddress = codeAddress + (uint)patch.NextInstructionOffset;
            int displacement = checked((int)((long)targetAddress - (long)sourceNextAddress));
            Array.Copy(BitConverter.GetBytes(displacement), 0, content, patch.PatchOffset, 4);
        }

        return content;
    }

    private static byte[] PatchArm64Code(Arm64CodeImage codeImage, SectionImage dataSection, ulong baseAddress, uint headerSize, uint programHeaderSize)
    {
        uint codeOffset = Align(headerSize + programHeaderSize, PageAlignment);
        uint dataOffset = Align(codeOffset + (uint)codeImage.Content.Length, 16);
        ulong codeAddress = baseAddress + codeOffset;
        ulong dataAddress = baseAddress + dataOffset;
        byte[] content = (byte[])codeImage.Content.Clone();

        foreach (Arm64Patch patch in codeImage.Patches)
        {
            ulong targetAddress = Resolve64Target(codeImage.Labels, dataSection.Labels, patch.LabelName, codeAddress, dataAddress);
            ulong instructionAddress = codeAddress + (uint)patch.InstructionOffset;
            uint encoded = patch.Kind switch
            {
                Arm64PatchKind.Branch26 => EncodeBranch26(patch.OpcodeBase, instructionAddress, targetAddress),
                Arm64PatchKind.ConditionalBranch19 => EncodeConditionalBranch19(patch.OpcodeBase, instructionAddress, targetAddress),
                Arm64PatchKind.Adrp => EncodeAdrp(patch.Register, instructionAddress, targetAddress),
                Arm64PatchKind.AddAbsoluteLow12 => EncodeAddLow12(patch.Register, targetAddress),
                _ => throw new InvalidOperationException($"Unsupported ELF arm64 patch kind '{patch.Kind}'.")
            };

            Array.Copy(BitConverter.GetBytes(encoded), 0, content, patch.InstructionOffset, 4);
        }

        return content;
    }

    private static byte[] PatchX86Code(X86CodeImage codeImage, SectionImage dataSection, uint baseAddress, uint headerSize, uint programHeaderSize)
    {
        uint codeOffset = Align(headerSize + programHeaderSize, PageAlignment);
        uint dataOffset = Align(codeOffset + (uint)codeImage.Content.Length, 16);
        uint codeAddress = baseAddress + codeOffset;
        uint dataAddress = baseAddress + dataOffset;
        byte[] content = (byte[])codeImage.Content.Clone();

        foreach (X86TextPatch patch in codeImage.Patches)
        {
            uint targetAddress = Resolve32Target(codeImage.Labels, dataSection.Labels, patch.LabelName, codeAddress, dataAddress);

            if (patch.Kind == X86PatchKind.Absolute32)
            {
                Array.Copy(BitConverter.GetBytes(targetAddress), 0, content, patch.PatchOffset, 4);
                continue;
            }

            uint sourceNextAddress = codeAddress + (uint)patch.NextInstructionOffset;
            int displacement = checked((int)((long)targetAddress - (long)sourceNextAddress));
            Array.Copy(BitConverter.GetBytes(displacement), 0, content, patch.PatchOffset, 4);
        }

        return content;
    }

    private static ulong Resolve64Target(
        Dictionary<string, int> codeLabels,
        Dictionary<string, int> dataLabels,
        string labelName,
        ulong codeAddress,
        ulong dataAddress)
    {
        if (codeLabels.TryGetValue(labelName, out int codeOffset))
        {
            return codeAddress + (uint)codeOffset;
        }

        if (dataLabels.TryGetValue(labelName, out int dataOffset))
        {
            return dataAddress + (uint)dataOffset;
        }

        throw new InvalidOperationException($"Unknown ELF patch target '{labelName}'.");
    }

    private static uint Resolve32Target(
        Dictionary<string, int> codeLabels,
        Dictionary<string, int> dataLabels,
        string labelName,
        uint codeAddress,
        uint dataAddress)
    {
        if (codeLabels.TryGetValue(labelName, out int codeOffset))
        {
            return codeAddress + (uint)codeOffset;
        }

        if (dataLabels.TryGetValue(labelName, out int dataOffset))
        {
            return dataAddress + (uint)dataOffset;
        }

        throw new InvalidOperationException($"Unknown ELF patch target '{labelName}'.");
    }

    private static void WriteElf64Header(BinaryWriter writer, ushort machine, ulong entryPoint, uint headerSize, uint programHeaderSize)
    {
        writer.Write(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        writer.Write((ushort)2);
        writer.Write(machine);
        writer.Write(1u);
        writer.Write(entryPoint);
        writer.Write((ulong)headerSize);
        writer.Write(0ul);
        writer.Write(0u);
        writer.Write((ushort)headerSize);
        writer.Write((ushort)programHeaderSize);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
    }

    private static void WriteElf32Header(BinaryWriter writer, ushort machine, uint entryPoint, uint headerSize, uint programHeaderSize)
    {
        writer.Write(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        writer.Write((ushort)2);
        writer.Write(machine);
        writer.Write(1u);
        writer.Write(entryPoint);
        writer.Write(headerSize);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)headerSize);
        writer.Write((ushort)programHeaderSize);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
    }

    private static void WriteElf64ProgramHeader(BinaryWriter writer, ulong baseAddress, uint fileSize)
    {
        writer.Write(1u);
        writer.Write(7u);
        writer.Write(0ul);
        writer.Write(baseAddress);
        writer.Write(baseAddress);
        writer.Write((ulong)fileSize);
        writer.Write((ulong)fileSize);
        writer.Write((ulong)PageAlignment);
    }

    private static void WriteElf32ProgramHeader(BinaryWriter writer, uint baseAddress, uint fileSize)
    {
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(baseAddress);
        writer.Write(baseAddress);
        writer.Write(fileSize);
        writer.Write(fileSize);
        writer.Write(7u);
        writer.Write(PageAlignment);
    }

    private static uint EncodeBranch26(uint opcodeBase, ulong instructionAddress, ulong targetAddress)
    {
        long displacement = (long)targetAddress - (long)instructionAddress;
        if ((displacement & 0x3) != 0)
        {
            throw new InvalidOperationException("ELF arm64 branch target is not 4-byte aligned.");
        }

        long immediate = displacement >> 2;
        if (immediate < -(1 << 25) || immediate >= (1 << 25))
        {
            throw new InvalidOperationException("ELF arm64 branch target is out of range.");
        }

        return opcodeBase | ((uint)immediate & 0x03FFFFFFu);
    }

    private static uint EncodeConditionalBranch19(uint opcodeBase, ulong instructionAddress, ulong targetAddress)
    {
        long displacement = (long)targetAddress - (long)instructionAddress;
        if ((displacement & 0x3) != 0)
        {
            throw new InvalidOperationException("ELF arm64 conditional branch target is not 4-byte aligned.");
        }

        long immediate = displacement >> 2;
        if (immediate < -(1 << 18) || immediate >= (1 << 18))
        {
            throw new InvalidOperationException("ELF arm64 conditional branch target is out of range.");
        }

        return opcodeBase | (((uint)immediate & 0x7FFFFu) << 5);
    }

    private static uint EncodeAdrp(int register, ulong instructionAddress, ulong targetAddress)
    {
        long instructionPage = (long)(instructionAddress & ~0xFFFul);
        long targetPage = (long)(targetAddress & ~0xFFFul);
        long pageDelta = (targetPage - instructionPage) >> 12;
        if (pageDelta < -(1 << 20) || pageDelta >= (1 << 20))
        {
            throw new InvalidOperationException("ELF arm64 ADRP target is out of range.");
        }

        uint imm = (uint)(pageDelta & 0x1FFFFF);
        uint immlo = imm & 0x3;
        uint immhi = (imm >> 2) & 0x7FFFF;
        return 0x90000000u | (immlo << 29) | (immhi << 5) | (uint)register;
    }

    private static uint EncodeAddLow12(int register, ulong targetAddress)
    {
        return 0x91000000u | (((uint)targetAddress & 0xFFFu) << 10) | ((uint)register << 5) | (uint)register;
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
