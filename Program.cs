using System.Diagnostics;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Parsing;
using BrainFucker.Models;
using BrainFucker.Emitters;
using BrainFucker.Services;

namespace BrainFucker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string[] normalizedArgs = args
            .Where(static arg => !string.IsNullOrWhiteSpace(arg))
            .Select(static arg => arg.Trim())
            .ToArray();

        bool suppressPrettyRunOutput = normalizedArgs.Any(static arg => string.Equals(arg, "--quiet-run", StringComparison.OrdinalIgnoreCase));
        bool listTargets = normalizedArgs.Any(static arg => string.Equals(arg, "--list-targets", StringComparison.OrdinalIgnoreCase));

        if (listTargets)
        {
            RenderTargetList();
            return 0;
        }

        if (normalizedArgs.Length == 0)
        {
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

    private static RootCommand BuildCommand()
    {
        Argument<FileInfo> inputArgument = new("input")
        {
            Description = "Path to the Brainfuck source file."
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

        Option<string> targetOption = new("--target")
        {
            Description = "Binary emitter target identifier. Available: win32-x64, win32-x86, msdos-com, msdos-exe.",
            DefaultValueFactory = static _ => "win32-x64"
        };

        cellsOption.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int>() <= 0)
            {
                result.AddError("--cells must be a positive integer.");
            }
        });

        RootCommand command = new("Compile Brainfuck source into a native executable.")
        {
            inputArgument,
            outputOption,
            runOption,
            quietRunOption,
            listTargetsOption,
            cellsOption,
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

            CompilerOptions options = CreateCompilerOptions(input, output, run, quietRun, cells, target);
            return await ExecuteAsync(options);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(CompilerOptions options)
    {
        try
        {
            IBinaryEmitter emitter = BinaryEmitterRegistry.Resolve(options.Target);
            if (options.Run && !emitter.CanExecuteOnCurrentPlatform(out string reason))
            {
                throw new InvalidOperationException(reason);
            }

            string source = await File.ReadAllTextAsync(options.InputPath);
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
        string inputPath = input?.FullName ?? string.Empty;
        string resolvedTarget = string.IsNullOrWhiteSpace(target) ? "win32-x64" : target;
        IBinaryEmitter emitter = BinaryEmitterRegistry.Resolve(resolvedTarget);
        string outputPath = run
            ? CreateTemporaryOutputPath(inputPath, emitter.DefaultFileExtension)
            : (output?.FullName ?? Path.GetFullPath(Path.ChangeExtension(inputPath, emitter.DefaultFileExtension)));

        return new CompilerOptions
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            OutputPathExplicit = output is not null,
            CellCount = cells,
            Target = resolvedTarget,
            Run = run,
            QuietRun = quietRun
        };
    }

    private static async Task<int> BuildBinaryAsync(CompilerOptions options, IBinaryEmitter emitter, byte[] binary)
    {
        string outputPath = Path.GetFullPath(options.OutputPath);
        string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(outputPath, binary);

        RenderSuccess("Built binary", emitter.DisplayName, outputPath);
        return 0;
    }

    private static async Task<int> BuildRunAndCleanUpAsync(CompilerOptions options, IBinaryEmitter emitter, byte[] binary)
    {
        string outputPath = Path.GetFullPath(options.OutputPath);
        string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(outputPath, binary);

        if (!options.QuietRun)
        {
            RenderSuccess("Built temporary binary", emitter.DisplayName, outputPath);
        }

        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = outputPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.InputPath)) ?? Directory.GetCurrentDirectory()
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

    private static string CreateTemporaryOutputPath(string inputPath, string extension)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BrainFucker");
        string tempDirectory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        string fileName = $"{Path.GetFileNameWithoutExtension(inputPath)}{extension}";
        return Path.Combine(tempDirectory, fileName);
    }

    private static void TryDeleteTemporaryDirectory(string directoryPath)
    {
        try
        {
            string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BrainFucker"));
            string fullDirectoryPath = Path.GetFullPath(directoryPath);

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

        Table usage = new Table().Border(TableBorder.Rounded).AddColumn("[aqua]Usage[/]");
        usage.AddRow(
            $"[white]brainfucker[/] [yellow]{Markup.Escape("<input.bf>")}[/] " +
            $"[blue]{Markup.Escape("[-o output.exe|output.com]")}[/] " +
            $"[green]{Markup.Escape("[--run]")}[/] " +
            $"[grey]{Markup.Escape("[--quiet-run]")}[/] " +
            $"[aqua]{Markup.Escape("[--list-targets]")}[/] " +
            $"[blue]{Markup.Escape("[--cells 30000]")}[/] " +
            $"[blue]{Markup.Escape("[--target win32-x64|win32-x86|msdos-com|msdos-exe]")}[/]");
        AnsiConsole.Write(usage);
        AnsiConsole.WriteLine();

        Table options = new Table().RoundedBorder().AddColumns("[aqua]Option[/]", "[aqua]Description[/]");
        options.AddRow("[yellow]<input>[/]", "Path to the Brainfuck source file.");
        options.AddRow("[blue]-o[/], [blue]--output[/]", "Write the generated binary to this path.");
        options.AddRow("[green]--run[/]", "Build to an OS temp directory, execute it, then clean it up.");
        options.AddRow("[grey]--quiet-run[/]", "With --run, suppress CLI prettification so only the program output is shown.");
        options.AddRow("[aqua]--list-targets[/]", "List the available binary targets and exit.");
        options.AddRow("[grey] [/]", "Only allowed when the selected target can run on the current host OS.");
        options.AddRow("[blue]--cells[/]", "Number of tape cells to allocate. Default: [white]30000[/].");
        options.AddRow("[blue]--target[/]", "Binary emitter target identifier. Available: [white]win32-x64[/], [white]win32-x86[/], [white]msdos-com[/], [white]msdos-exe[/]. Default: [white]win32-x64[/].");
        options.AddRow("[blue]-h[/], [blue]--help[/]", "Show this help screen.");
        AnsiConsole.Write(options);
    }

    private static void RenderTargetList()
    {
        Table table = new Table().RoundedBorder().AddColumns("[aqua]Target[/]", "[aqua]Output[/]", "[aqua]Description[/]");
        table.AddRow("win32-x64", ".exe", "Win32 x64 PE executable");
        table.AddRow("win32-x86", ".exe", "Win32 x86 PE executable");
        table.AddRow("msdos-com", ".com", "MS-DOS 16-bit COM program");
        table.AddRow("msdos-exe", ".exe", "MS-DOS 16-bit MZ executable");
        AnsiConsole.Write(table);
    }

    private static void RenderParseErrors(ParseResult parseResult, bool plainText)
    {
        if (plainText)
        {
            foreach (ParseError error in parseResult.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }

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
        AnsiConsole.Write(
            new Panel("[yellow]No input file was provided.[/]\n[grey]Run with [white]--help[/] to see usage and options.[/]")
                .Header("[yellow]Nothing To Do[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("yellow")));
    }

    private static void RenderSuccess(string title, string emitterDisplayName, string outputPath)
    {
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