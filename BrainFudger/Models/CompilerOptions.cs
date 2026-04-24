namespace BrainFudger.Models;

public sealed record CompilerOptions
{
    public string InputPath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public bool OutputPathExplicit { get; init; }
    public int CellCount { get; init; } = 30000;
#if WINDOWS
    public string Target { get; init; } = "win-x64";
#elif APPLEOSX
    public string Target { get; init; } = "osx-arm64";
#endif
    public bool Run { get; init; }
    public bool QuietRun { get; init; }
    public bool UseShellExecuteForRun { get; init; }
    public bool PauseAfterRun { get; init; }
}
