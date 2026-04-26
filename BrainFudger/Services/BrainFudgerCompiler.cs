using BrainFudger.Emitters;
using BrainFudger.Models;

namespace BrainFudger.Services;

public static class BrainFudgerCompiler
{
    private static readonly HashSet<char> CoreTokens = ['>', '<', '+', '-', '.', ',', '[', ']'];

    public static byte[] Compile(string source, CompilerOptions options, IBinaryEmitter emitter)
    {
        IntermediateProgram program = CompileToIntermediateProgram(source, options);
        return emitter.EmitBinary(program, options);
    }

    public static IntermediateProgram CompileToIntermediateProgram(string source, CompilerOptions options)
    {
        List<IntermediateInstruction> instructions = [];
        Stack<(int InstructionIndex, int SourceIndex)> loopStack = [];

        for (int i = 0; i < source.Length; i++)
        {
            char token = source[i];
            if (!TryMapToken(token, options, out IntermediateOpcode opcode, out string? error))
            {
                if (error is not null)
                {
                    throw new InvalidOperationException(error);
                }

                continue;
            }

            if (opcode is IntermediateOpcode.MovePointer or IntermediateOpcode.AddToCell)
            {
                int operand = 0;
                char repeatedToken = token;
                while (i < source.Length && source[i] == repeatedToken)
                {
                    operand += repeatedToken is '>' or '+' ? 1 : -1;
                    i++;
                }

                i--;
                if (operand != 0)
                {
                    instructions.Add(new IntermediateInstruction(opcode, operand, SourceIndex: i));
                }

                continue;
            }

            switch (opcode)
            {
                case IntermediateOpcode.LoopStart:
                    loopStack.Push((instructions.Count, i));
                    instructions.Add(new IntermediateInstruction(opcode, SourceIndex: i));
                    break;

                case IntermediateOpcode.LoopEnd:
                    if (loopStack.Count == 0)
                    {
                        throw new InvalidOperationException($"Unmatched closing bracket at {Branding.LanguageDisplayName} instruction {i}.");
                    }

                    (int startInstructionIndex, int startSourceIndex) = loopStack.Pop();
                    int endInstructionIndex = instructions.Count;
                    instructions.Add(new IntermediateInstruction(opcode, MatchingInstructionIndex: startInstructionIndex, SourceIndex: i));
                    instructions[startInstructionIndex] = instructions[startInstructionIndex] with
                    {
                        MatchingInstructionIndex = endInstructionIndex,
                        SourceIndex = startSourceIndex
                    };
                    break;

                default:
                    instructions.Add(new IntermediateInstruction(opcode, SourceIndex: i));
                    break;
            }
        }

        if (loopStack.Count > 0)
        {
            (_, int sourceIndex) = loopStack.Peek();
            throw new InvalidOperationException($"Unmatched opening bracket at {Branding.LanguageDisplayName} instruction {sourceIndex}.");
        }

        return new IntermediateProgram(instructions);
    }

    private static bool TryMapToken(char token, CompilerOptions options, out IntermediateOpcode opcode, out string? error)
    {
        error = null;
        switch (token)
        {
            case '>':
            case '<':
                opcode = IntermediateOpcode.MovePointer;
                return true;

            case '+':
            case '-':
                opcode = IntermediateOpcode.AddToCell;
                return true;

            case '.':
                opcode = IntermediateOpcode.WriteByte;
                return true;

            case ',':
                opcode = IntermediateOpcode.ReadByte;
                return true;

            case '[':
                opcode = IntermediateOpcode.LoopStart;
                return true;

            case ']':
                opcode = IntermediateOpcode.LoopEnd;
                return true;

            case '?':
                opcode = IntermediateOpcode.RandomByte;
                if (options.EnableRandomCommand)
                {
                    return true;
                }

                error = "The '?' extension command is disabled. Enable it with --enable-random, --enable-all-extensions, or the GUI extension checkbox.";
                return false;

            case '!':
                opcode = IntermediateOpcode.ClearTerminal;
                if (options.EnableClearTerminalCommand)
                {
                    return true;
                }

                error = "The '!' extension command is disabled. Enable it with --enable-clear, --enable-all-extensions, or the GUI extension checkbox.";
                return false;

            case '~':
                opcode = IntermediateOpcode.Delay;
                if (options.EnableDelayCommand)
                {
                    return true;
                }

                error = "The '~' extension command is disabled. Enable it with --enable-delay, --enable-all-extensions, or the GUI extension checkbox.";
                return false;

            default:
                opcode = default;
                if (CoreTokens.Contains(token))
                {
                    return true;
                }

                return false;
        }
    }
}
