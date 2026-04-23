namespace BrainFudger.Gui;

internal static class GuiApplicationHostFactory
{
    public static IGuiApplicationHost? TryCreateForCurrentPlatform()
    {
#if WINDOWS
        return WindowsGuiApplication.Instance;
#elif APPLEOSX
        return MacGuiApplication.Instance;
#else
        return null;
#endif
    }
}
