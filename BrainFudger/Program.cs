using Spectre.Console;
using System.Diagnostics;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using BrainFudger.Emitters;
using BrainFudger.Models;
using BrainFudger.Services;
using BrainFudger.Gui;

namespace BrainFudger;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        InitializeConsoleRendering();

        string[] normalizedArgs = args
            .Where(static arg => !string.IsNullOrWhiteSpace(arg))
            .Select(static arg => arg.Trim())
            .ToArray();

        bool suppressPrettyRunOutput = normalizedArgs.Any(static arg => string.Equals(arg, "--quiet-run", StringComparison.OrdinalIgnoreCase));
        bool listTargets = normalizedArgs.Any(static arg => string.Equals(arg, "--list-targets", StringComparison.OrdinalIgnoreCase));
        IGuiApplicationHost? guiHost = GuiApplicationHostFactory.TryCreateForCurrentPlatform();

        if (guiHost?.TryHandleHostArguments(normalizedArgs, out int hostExitCode) == true)
        {
            return hostExitCode;
        }

        if (listTargets)
        {
            RenderTargetList();
            return 0;
        }

        if (normalizedArgs.Length == 0)
        {
            if (guiHost is not null)
            {
                if (!Debugger.IsAttached && guiHost.ShouldLaunchDetached())
                {
                    return guiHost.LaunchDetached();
                }

                return guiHost.Run();
            }
            RenderNoArgumentsMessage();
            return 1;
        }

        RootCommand command = BuildCommand();
        ParseResult parseResult = command.Parse(normalizedArgs);

        if (normalizedArgs.Any(static arg => arg is "-h" or "--help"))
        {
            RenderHelp();
            return 0;
        }

        if (parseResult.Errors.Count > 0)
        {
            RenderParseErrors(parseResult, suppressPrettyRunOutput);
            return 1;
        }

        return await parseResult.InvokeAsync();
    }

    private static void InitializeConsoleRendering()
    {
        try
        {
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // If the host rejects encoding changes, we'll fall back to ASCII-safe rendering.
        }
    }

    private static bool SupportsUnicodeUi()
    {
        try
        {
            return Console.OutputEncoding.CodePage == 65001;
        }
        catch
        {
            return false;
        }
    }

    private static RootCommand BuildCommand()
    {
        Argument<FileInfo> inputArgument = new("input")
        {
            Description = Branding.SourceFileDescription
        };

        Option<FileInfo?> outputOption = new("--output", "-o")
        {
            Description = "Write the generated binary to this path."
        };

        Option<bool> runOption = new("--run")
        {
            Description = "Build to an OS temp directory, execute it, then clean it up."
        };

        Option<bool> quietRunOption = new("--quiet-run")
        {
            Description = "When used with --run, suppress compile spinner and success panels so only the program output is shown."
        };

        Option<bool> listTargetsOption = new("--list-targets")
        {
            Description = "List the available binary targets and exit."
        };

        Option<int> cellsOption = new("--cells")
        {
            Description = "Number of tape cells to allocate in the generated program.",
            DefaultValueFactory = static _ => 30000
        };

        Option<bool> enableRandomOption = new("--enable-random")
        {
            Description = "Enable the '?' extension command for pseudorandom byte generation."
        };

        Option<bool> enableClearOption = new("--enable-clear")
        {
            Description = "Enable the '!' extension command for best-effort terminal clearing."
        };

        Option<bool> enableDelayOption = new("--enable-delay")
        {
            Description = "Enable the '~' extension command for best-effort delay behavior."
        };

        Option<bool> enableAllExtensionsOption = new("--enable-all-extensions")
        {
            Description = "Enable all extension commands: ?, !, and ~."
        };

        Option<string> targetOption = new("--target")
        {
            Description = "Binary emitter target identifier. Available: linux-arm64, linux-x64, linux-x86, win-x64, win-x86, msdos-com, msdos-exe, osx-arm64."
        };

        cellsOption.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int>() <= 0)
            {
                result.AddError("--cells must be a positive integer.");
            }
        });

        RootCommand command = new(Branding.SourceCompilationDescription)
        {
            inputArgument,
            outputOption,
            runOption,
            quietRunOption,
            listTargetsOption,
            cellsOption,
            enableAllExtensionsOption,
            enableRandomOption,
            enableClearOption,
            enableDelayOption,
            targetOption
        };

        command.TreatUnmatchedTokensAsErrors = true;
        command.Validators.Add(result =>
        {
            bool run = result.GetValue(runOption);
            bool quietRun = result.GetValue(quietRunOption);
            FileInfo? output = result.GetValue(outputOption);
            if (run && output is not null)
            {
                result.AddError("The --run option cannot be combined with -o/--output.");
            }

            if (quietRun && !run)
            {
                result.AddError("The --quiet-run option can only be used together with --run.");
            }
        });

        command.SetAction(async parseResult =>
        {
            FileInfo? input = parseResult.GetValue(inputArgument);
            FileInfo? output = parseResult.GetValue(outputOption);
            bool run = parseResult.GetValue(runOption);
            bool quietRun = parseResult.GetValue(quietRunOption);
            int cells = parseResult.GetValue(cellsOption);
            string? target = parseResult.GetValue(targetOption);
            bool enableAllExtensions = parseResult.GetValue(enableAllExtensionsOption);
            bool enableRandom = parseResult.GetValue(enableRandomOption);
            bool enableClear = parseResult.GetValue(enableClearOption);
            bool enableDelay = parseResult.GetValue(enableDelayOption);

            CompilerOptions options = CompilationWorkflow.CreateCompilerOptions(
                input,
                output,
                run,
                quietRun,
                cells,
                target,
                enableRandom || enableAllExtensions,
                enableClear || enableAllExtensions,
                enableDelay || enableAllExtensions);
            return await ExecuteAsync(options);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(CompilerOptions options)
    {
        try
        {
            PreparedCompilation? preparedCompilation = null;

            if (options.QuietRun)
            {
                preparedCompilation = await CompilationWorkflow.PrepareAsync(options);
            }
            else
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("deepskyblue2"))
                    .StartAsync(Branding.SourceCompilationProgress, async _ =>
                    {
                        preparedCompilation = await CompilationWorkflow.PrepareAsync(options);
                    });
            }

            if (preparedCompilation is null)
            {
                throw new InvalidOperationException("Compilation did not produce an output payload.");
            }

            CompilationExecutionResult result = await CompilationWorkflow.PersistOrRunAsync(preparedCompilation);
            if (!options.QuietRun)
            {
                string title = options.Run ? "Built temporary binary" : "Built binary";
                RenderSuccess(title, result.EmitterDisplayName, result.OutputPath);
            }

            return result.ExitCode;
        }
        catch (Exception ex)
        {
            RenderException(ex, options.QuietRun);
            return 1;
        }
    }

    private static void RenderHelp()
    {
        if (!SupportsUnicodeUi())
        {
            RenderHelpPlainText();
            return;
        }

        AnsiConsole.Write(new FigletText(Branding.AppDisplayName).Color(Color.DeepSkyBlue2));
        AnsiConsole.Write(new Markup($"[grey]{Markup.Escape(Branding.SourceCompilationDescription)}[/]\n\n"));

        Table usage = new Table().Border(TableBorder.Rounded).AddColumn("[aqua]Usage[/]");
        usage.AddRow(
            $"[white]{Markup.Escape(Branding.CommandName)}[/] [yellow]{Markup.Escape("<input.bf>")}[/] " +
            $"[blue]{Markup.Escape("[-o output.exe|output.com|output]")}[/] " +
            $"[green]{Markup.Escape("[--run]")}[/] " +
            $"[grey]{Markup.Escape("[--quiet-run]")}[/] " +
            $"[aqua]{Markup.Escape("[--list-targets]")}[/] " +
            $"[blue]{Markup.Escape("[--cells 30000]")}[/] " +
            $"[magenta]{Markup.Escape("[--enable-all-extensions]")}[/] " +
            $"[magenta]{Markup.Escape("[--enable-random]")}[/] " +
            $"[magenta]{Markup.Escape("[--enable-clear]")}[/] " +
            $"[magenta]{Markup.Escape("[--enable-delay]")}[/] " +
            $"[blue]{Markup.Escape("[--target linux-arm64|linux-x64|linux-x86|win-x64|win-x86|msdos-com|msdos-exe|osx-arm64]")}[/]");
        AnsiConsole.Write(usage);
        AnsiConsole.WriteLine();

        Table options = new Table().RoundedBorder().AddColumns("[aqua]Option[/]", "[aqua]Description[/]");
        options.AddRow("[yellow]<input>[/]", Branding.SourceFileDescription);
#if WINDOWS
        options.AddRow("[grey](no arguments)[/]", "Launch the native GUI file picker instead of the CLI error panel.");
#elif APPLEOSX
        options.AddRow("[grey](no arguments)[/]", "Launch the native AppKit GUI instead of the CLI error panel.");
#elif LINUX
        options.AddRow("[grey](no arguments)[/]", "Launch the native Linux desktop GUI workflow instead of the CLI error panel.");
#endif
        options.AddRow("[blue]-o[/], [blue]--output[/]", "Write the generated binary to this path.");
        options.AddRow("[green]--run[/]", "Build to an OS temp directory, execute it, then clean it up.");
        options.AddRow("[grey]--quiet-run[/]", "With --run, suppress CLI prettification so only the program output is shown.");
        options.AddRow("[aqua]--list-targets[/]", "List the available binary targets and exit.");
        options.AddRow("[grey] [/]", "Only allowed when the selected target can run on the current host OS.");
        options.AddRow("[blue]--cells[/]", "Number of tape cells to allocate. Default: [white]30000[/].");
        options.AddRow("[magenta]--enable-all-extensions[/]", "Enable all three extension commands: [white]?[/], [white]![/], and [white]~[/].");
        options.AddRow("[magenta]--enable-random[/]", "Enable the [white]?[/] extension command for pseudorandom byte generation.");
        options.AddRow("[magenta]--enable-clear[/]", "Enable the [white]![/] extension command for best-effort terminal clearing.");
        options.AddRow("[magenta]--enable-delay[/]", "Enable the [white]~[/] extension command for best-effort delay behavior.");
        options.AddRow("[blue]--target[/]", "Binary emitter target identifier. Available: [white]linux-arm64[/], [white]linux-x64[/], [white]linux-x86[/], [white]win-x64[/], [white]win-x86[/], [white]msdos-com[/], [white]msdos-exe[/], [white]osx-arm64[/]. Default: [white]host-preferred target[/].");
        options.AddRow("[blue]-h[/], [blue]--help[/]", "Show this help screen.");
        AnsiConsole.Write(options);
    }

    private static void RenderTargetList()
    {
        if (!SupportsUnicodeUi())
        {
            RenderTargetListPlainText();
            return;
        }

        Table table = new Table().RoundedBorder().AddColumns("[aqua]Target[/]", "[aqua]Output[/]", "[aqua]Description[/]");
        table.AddRow("linux-arm64", "(none)", "Linux arm64 ELF executable");
        table.AddRow("linux-x64", "(none)", "Linux x64 ELF executable");
        table.AddRow("linux-x86", "(none)", "Linux x86 ELF executable");
        table.AddRow("win-x64", ".exe", "Win32 x64 PE executable");
        table.AddRow("win-x86", ".exe", "Win32 x86 PE executable");
        table.AddRow("msdos-com", ".com", "MS-DOS 16-bit COM program");
        table.AddRow("msdos-exe", ".exe", "MS-DOS 16-bit MZ executable");
        table.AddRow("osx-arm64", "(none)", "macOS Apple Silicon Mach-O executable");
        AnsiConsole.Write(table);
    }

    private static void RenderParseErrors(ParseResult parseResult, bool plainText)
    {
        if (plainText || !SupportsUnicodeUi())
        {
            foreach (ParseError error in parseResult.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }

            Console.Error.WriteLine("Run with --help to see usage and options.");
            return;
        }

        Panel panel = new Panel(string.Join(Environment.NewLine, parseResult.Errors.Select(static e => $"[red]-[/] {Markup.Escape(e.Message)}")))
            .Header("[red]Argument Error[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("red"));

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Run with [white]--help[/] to see usage and options.[/]");
    }

    private static void RenderNoArgumentsMessage()
    {
        if (!SupportsUnicodeUi())
        {
            Console.WriteLine("No input file was provided.");
            Console.WriteLine("Run with --help to see usage and options.");
            return;
        }

        AnsiConsole.Write(
            new Panel("[yellow]No input file was provided.[/]\n[grey]Run with [white]--help[/] to see usage and options.[/]")
                .Header("[yellow]Nothing To Do[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("yellow")));
        AnsiConsole.WriteLine();
    }

    private static void RenderSuccess(string title, string emitterDisplayName, string outputPath)
    {
        if (!SupportsUnicodeUi())
        {
            Console.WriteLine(title);
            Console.WriteLine($"Emitter: {emitterDisplayName}");
            Console.WriteLine($"Output: {outputPath}");
            return;
        }

        Grid grid = new();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[green]Emitter[/]", Markup.Escape(emitterDisplayName));
        grid.AddRow("[green]Output[/]", Markup.Escape(outputPath));

        AnsiConsole.Write(
            new Panel(grid)
                .Header($"[green]{Markup.Escape(title)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("green")));
        AnsiConsole.WriteLine();
    }

    private static void RenderException(Exception ex, bool plainText)
    {
        if (plainText || !SupportsUnicodeUi())
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        AnsiConsole.Write(
            new Panel(Markup.Escape(ex.Message))
                .Header("[red]Build Failed[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("red")));
        AnsiConsole.WriteLine();
    }

    private static void RenderHelpPlainText()
    {
        Console.WriteLine(Branding.AppDisplayName);
        Console.WriteLine(Branding.SourceCompilationDescription);
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine($"  {Branding.CommandName} <input.bf> [-o output.exe|output.com|output] [--run] [--quiet-run] [--list-targets] [--cells 30000] [--enable-all-extensions] [--enable-random] [--enable-clear] [--enable-delay] [--target linux-arm64|linux-x64|linux-x86|win-x64|win-x86|msdos-com|msdos-exe|osx-arm64]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  <input>           {Branding.SourceFileDescription}");
#if WINDOWS
        Console.WriteLine("  (no arguments)    Launch the native GUI file picker instead of the CLI error panel.");
#elif APPLEOSX
        Console.WriteLine("  (no arguments)    Launch the native AppKit GUI instead of the CLI error panel.");
#elif LINUX
        Console.WriteLine("  (no arguments)    Launch the native Linux desktop GUI workflow instead of the CLI error panel.");
#endif
        Console.WriteLine("  -o, --output      Write the generated binary to this path.");
        Console.WriteLine("  --run             Build to an OS temp directory, execute it, then clean it up.");
        Console.WriteLine("  --quiet-run       With --run, suppress CLI prettification so only the program output is shown.");
        Console.WriteLine("  --list-targets    List the available binary targets and exit.");
        Console.WriteLine("                    Only allowed when the selected target can run on the current host OS.");
        Console.WriteLine("  --cells           Number of tape cells to allocate. Default: 30000.");
        Console.WriteLine("  --enable-all-extensions");
        Console.WriteLine("                    Enable the ?, !, and ~ extension commands together.");
        Console.WriteLine("  --enable-random   Enable the ? extension command for pseudorandom byte generation.");
        Console.WriteLine("  --enable-clear    Enable the ! extension command for best-effort terminal clearing.");
        Console.WriteLine("  --enable-delay    Enable the ~ extension command for best-effort delay behavior.");
        Console.WriteLine("  --target          Available: linux-arm64, linux-x64, linux-x86, win-x64, win-x86, msdos-com, msdos-exe, osx-arm64.");
        Console.WriteLine("                    Default: host-preferred target.");
        Console.WriteLine("  -h, --help        Show this help screen.");
    }

    private static void RenderTargetListPlainText()
    {
        Console.WriteLine("Available targets:");
        Console.WriteLine("  linux-arm64 (none)  Linux arm64 ELF executable");
        Console.WriteLine("  linux-x64   (none)  Linux x64 ELF executable");
        Console.WriteLine("  linux-x86   (none)  Linux x86 ELF executable");
        Console.WriteLine("  win-x64    .exe    Win32 x64 PE executable");
        Console.WriteLine("  win-x86    .exe    Win32 x86 PE executable");
        Console.WriteLine("  msdos-com  .com    MS-DOS 16-bit COM program");
        Console.WriteLine("  msdos-exe  .exe    MS-DOS 16-bit MZ executable");
        Console.WriteLine("  osx-arm64  (none)  macOS Apple Silicon Mach-O executable");
    }
}
