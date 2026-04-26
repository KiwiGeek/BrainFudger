using System.Text;

namespace BrainFudger.Emitters;

internal static class EmitterRuntimeSupport
{
    public const string RngStateLabel = "rng_state";
    public const string ClearTerminalLabel = "clear_terminal_sequence";
    public const int ClearTerminalLength = 7;
    public const int DelayInnerLoopCount = 50_000;

    public static ReadOnlySpan<byte> ClearTerminalSequence => "\u001B[2J\u001B[H"u8;

    public static int GetDataStringLength(string labelName) =>
        labelName switch
        {
            EmitterRuntimeSupport.ClearTerminalLabel => ClearTerminalLength,
            _ => throw new InvalidOperationException($"Unknown runtime data label '{labelName}'.")
        };
}
