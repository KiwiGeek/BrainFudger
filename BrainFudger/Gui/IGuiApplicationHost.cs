namespace BrainFudger.Gui;

internal interface IGuiApplicationHost
{
    bool TryHandleHostArguments(string[] args, out int exitCode);
    bool ShouldLaunchDetached();
    int LaunchDetached();
    int Run();
}