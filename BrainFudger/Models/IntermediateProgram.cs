namespace BrainFudger.Models;

public enum IntermediateOpcode : byte
{
    MovePointer = 1,
    AddToCell = 2,
    WriteByte = 3,
    ReadByte = 4,
    LoopStart = 5,
    LoopEnd = 6,
    RandomByte = 7,
    ClearTerminal = 8,
    Delay = 9
}

public readonly record struct IntermediateInstruction(
    IntermediateOpcode Opcode,
    int Operand = 0,
    int MatchingInstructionIndex = -1,
    int SourceIndex = -1);

public sealed record IntermediateProgram(IReadOnlyList<IntermediateInstruction> Instructions);
