using System.Diagnostics;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace BrainFucker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var suppressPrettyRunOutput = args.Any(static arg => arg == "--quiet-run");

        if (args.Length == 0 || args.All(string.IsNullOrWhiteSpace))
        {
            RenderNoArgumentsMessage();
            return 1;
        }

        var command = BuildCommand();
        var parseResult = command.Parse(args);

        if (args.Any(static arg => arg is "-h" or "--help"))
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

    private static RootCommand BuildCommand()
    {
        var inputArgument = new Argument<FileInfo>("input")
        {
            Description = "Path to the Brainfuck source file."
        };

        var outputOption = new Option<FileInfo?>("--output", ["-o"])
        {
            Description = "Write the generated executable to this path."
        };

        var runOption = new Option<bool>("--run", [])
        {
            Description = "Build to an OS temp directory, execute it, then clean it up."
        };

        var quietRunOption = new Option<bool>("--quiet-run", [])
        {
            Description = "When used with --run, suppress compile spinner and success panels so only the program output is shown."
        };

        var cellsOption = new Option<int>("--cells", [])
        {
            Description = "Number of tape cells to allocate in the generated program.",
            DefaultValueFactory = static _ => 30000
        };

        var targetOption = new Option<string>("--target", [])
        {
            Description = "Binary emitter target identifier. Available: win32-x64, win32-x86.",
            DefaultValueFactory = static _ => "win32-x64"
        };

        cellsOption.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int>() <= 0)
            {
                result.AddError("--cells must be a positive integer.");
            }
        });

        var command = new RootCommand("Compile Brainfuck source into a native executable.")
        {
            inputArgument,
            outputOption,
            runOption,
            quietRunOption,
            cellsOption,
            targetOption
        };

        command.TreatUnmatchedTokensAsErrors = true;
        command.Validators.Add(result =>
        {
            var run = result.GetValue(runOption);
            var quietRun = result.GetValue(quietRunOption);
            var output = result.GetValue(outputOption);
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
            var input = parseResult.GetValue(inputArgument);
            var output = parseResult.GetValue(outputOption);
            var run = parseResult.GetValue(runOption);
            var quietRun = parseResult.GetValue(quietRunOption);
            var cells = parseResult.GetValue(cellsOption);
            var target = parseResult.GetValue(targetOption);

            var options = CreateCompilerOptions(input, output, run, quietRun, cells, target);
            return await ExecuteAsync(options);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(CompilerOptions options)
    {
        try
        {
            var emitter = BinaryEmitterRegistry.Resolve(options.Target);
            if (options.Run && !emitter.CanExecuteOnCurrentPlatform(out var reason))
            {
                throw new InvalidOperationException(reason);
            }

            var source = await File.ReadAllTextAsync(options.InputPath);
            byte[] binary = [];

            if (options.QuietRun)
            {
                binary = BrainfuckCompiler.Compile(source, options, emitter);
            }
            else
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("deepskyblue2"))
                    .StartAsync("Compiling brainfuck source...", async _ =>
                    {
                        binary = BrainfuckCompiler.Compile(source, options, emitter);
                        await Task.CompletedTask;
                    });
            }

            return options.Run
                ? await BuildRunAndCleanUpAsync(options, emitter, binary)
                : await BuildBinaryAsync(options, emitter, binary);
        }
        catch (Exception ex)
        {
            RenderException(ex, options.QuietRun);
            return 1;
        }
    }

    private static CompilerOptions CreateCompilerOptions(FileInfo? input, FileInfo? output, bool run, bool quietRun, int cells, string? target)
    {
        var inputPath = input?.FullName ?? string.Empty;
        var outputPath = run
            ? CreateTemporaryOutputPath(inputPath)
            : (output?.FullName ?? Path.GetFullPath(Path.ChangeExtension(inputPath, ".exe")));

        return new CompilerOptions
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            OutputPathExplicit = output is not null,
            CellCount = cells,
            Target = string.IsNullOrWhiteSpace(target) ? "win32-x64" : target,
            Run = run,
            QuietRun = quietRun
        };
    }

    private static async Task<int> BuildBinaryAsync(CompilerOptions options, IBinaryEmitter emitter, byte[] binary)
    {
        var outputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(outputPath, binary);

        RenderSuccess("Built binary", emitter.DisplayName, outputPath);
        return 0;
    }

    private static async Task<int> BuildRunAndCleanUpAsync(CompilerOptions options, IBinaryEmitter emitter, byte[] binary)
    {
        var outputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(outputPath, binary);

        if (!options.QuietRun)
        {
            RenderSuccess("Built temporary binary", emitter.DisplayName, outputPath);
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = outputPath,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.InputPath)) ?? Directory.GetCurrentDirectory()
                }
            };

            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally
        {
            TryDeleteTemporaryDirectory(outputDirectory);
        }
    }

    private static string CreateTemporaryOutputPath(string inputPath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "BrainFucker");
        var tempDirectory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        var fileName = $"{Path.GetFileNameWithoutExtension(inputPath)}.exe";
        return Path.Combine(tempDirectory, fileName);
    }

    private static void TryDeleteTemporaryDirectory(string directoryPath)
    {
        try
        {
            var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BrainFucker"));
            var fullDirectoryPath = Path.GetFullPath(directoryPath);

            if (!fullDirectoryPath.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(fullDirectoryPath))
            {
                Directory.Delete(fullDirectoryPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup. The compiled program has already finished running.
        }
    }

    private static void RenderHelp()
    {
        AnsiConsole.Write(new FigletText("BrainFucker").Color(Color.DeepSkyBlue2));
        AnsiConsole.Write(new Markup("[grey]Compile Brainfuck source into a native executable.[/]\n\n"));

        var usage = new Table().Border(TableBorder.Rounded).AddColumn("[aqua]Usage[/]");
        usage.AddRow(
            $"[white]brainfucker[/] [yellow]{Markup.Escape("<input.bf>")}[/] " +
            $"[blue]{Markup.Escape("[-o output.exe]")}[/] " +
            $"[green]{Markup.Escape("[--run]")}[/] " +
            $"[grey]{Markup.Escape("[--quiet-run]")}[/] " +
            $"[blue]{Markup.Escape("[--cells 30000]")}[/] " +
            $"[blue]{Markup.Escape("[--target win32-x64]")}[/]");
        AnsiConsole.Write(usage);
        AnsiConsole.WriteLine();

        var options = new Table().RoundedBorder().AddColumns("[aqua]Option[/]", "[aqua]Description[/]");
        options.AddRow("[yellow]<input>[/]", "Path to the Brainfuck source file.");
        options.AddRow("[blue]-o[/], [blue]--output[/]", "Write the generated executable to this path.");
        options.AddRow("[green]--run[/]", "Build to an OS temp directory, execute it, then clean it up.");
        options.AddRow("[grey]--quiet-run[/]", "With --run, suppress CLI prettification so only the program output is shown.");
        options.AddRow("[grey] [/]", "Only allowed when the selected target can run on the current host OS.");
        options.AddRow("[blue]--cells[/]", "Number of tape cells to allocate. Default: [white]30000[/].");
        options.AddRow("[blue]--target[/]", "Binary emitter target identifier. Available: [white]win32-x64[/], [white]win32-x86[/]. Default: [white]win32-x64[/].");
        options.AddRow("[blue]-h[/], [blue]--help[/]", "Show this help screen.");
        AnsiConsole.Write(options);
    }

    private static void RenderParseErrors(ParseResult parseResult, bool plainText)
    {
        if (plainText)
        {
            foreach (var error in parseResult.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }

            return;
        }

        var panel = new Panel(string.Join(Environment.NewLine, parseResult.Errors.Select(static e => $"[red]-[/] {Markup.Escape(e.Message)}")))
            .Header("[red]Argument Error[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("red"));

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Run with [white]--help[/] to see usage and options.[/]");
    }

    private static void RenderNoArgumentsMessage()
    {
        AnsiConsole.Write(
            new Panel("[yellow]No input file was provided.[/]\n[grey]Run with [white]--help[/] to see usage and options.[/]")
                .Header("[yellow]Nothing To Do[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("yellow")));
    }

    private static void RenderSuccess(string title, string emitterDisplayName, string outputPath)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[green]Emitter[/]", Markup.Escape(emitterDisplayName));
        grid.AddRow("[green]Output[/]", Markup.Escape(outputPath));

        AnsiConsole.Write(
            new Panel(grid)
                .Header($"[green]{Markup.Escape(title)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("green")));
    }

    private static void RenderException(Exception ex, bool plainText)
    {
        if (plainText)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        AnsiConsole.Write(
            new Panel(Markup.Escape(ex.Message))
                .Header("[red]Build Failed[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("red")));
    }
}

internal sealed record CompilerOptions
{
    public string InputPath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public bool OutputPathExplicit { get; init; }
    public int CellCount { get; init; } = 30000;
    public string Target { get; init; } = "win32-x64";
    public bool Run { get; init; }
    public bool QuietRun { get; init; }
}
