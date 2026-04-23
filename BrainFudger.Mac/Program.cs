namespace BrainFudger.Mac;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("BrainFudger.Mac can only run on macOS.");
            return 1;
        }

        return MacGuiApplication.Run();
    }
}
