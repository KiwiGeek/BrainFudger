namespace BrainFudger;

#if IAMGARYPENN && ZEROCOOL
#error IAMGARYPENN and ZEROCOOL are mutually exclusive branding directives.
#endif

internal static class Branding
{
#if IAMGARYPENN
    public const string AppDisplayName = "\u0042\u0072\u0061\u0069\u006E\u0046\u0075\u0063\u006B\u0065\u0072";
    public const string CommandName = "\u0062\u0072\u0061\u0069\u006E\u0066\u0075\u0063\u006B\u0065\u0072";
    public const string LanguageDisplayName = "\u0042\u0072\u0061\u0069\u006E\u0066\u0075\u0063\u006B";
    public const string LowercaseLanguageDisplayName = "\u0062\u0072\u0061\u0069\u006E\u0066\u0075\u0063\u006B";
#elif ZEROCOOL
    public const string AppDisplayName = "BrainFux0r";
    public const string CommandName = "brainfux0r";
    public const string LanguageDisplayName = "BrainFux";
    public const string LowercaseLanguageDisplayName = "brainfux";
#else
    public const string AppDisplayName = "BrainFudger";
    public const string CommandName = "brainfudger";
    public const string LanguageDisplayName = "Brainf$#k";
    public const string LowercaseLanguageDisplayName = "brainf$#k";
#endif

    public static string SourceFileDescription => $"Path to the {LanguageDisplayName} source file.";

    public static string SourceCompilationDescription => $"Compile {LanguageDisplayName} source into a native executable.";

    public static string SourceCompilationProgress => $"Compiling {LowercaseLanguageDisplayName} source...";

    public static string SourceFilePickerTitle => $"Select {LanguageDisplayName} source";

    public static string SourceFileDialogFilter => $"{LanguageDisplayName} Source (*.bf)\0*.bf\0All Files (*.*)\0*.*\0\0";
}
