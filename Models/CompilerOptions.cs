namespace BrainFucker.Models;

internal sealed record CompilerOptions
{
    public string InputPath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public bool OutputPathExplicit { get; init; }
    public int CellCount { get; init; } = 30000;
    public string Target { get; init; } = "win32-x64";
    public bool Run { get; init; }
    public bool QuietRun { get; init; }
    public bool UseShellExecuteForRun { get; init; }
    public bool PauseAfterRun { get; init; }
}
