using System.Diagnostics;
using BrainFudger.Emitters;
using BrainFudger.Models;

namespace BrainFudger.Services;

public sealed record PreparedCompilation(CompilerOptions Options, IBinaryEmitter Emitter, byte[] Binary);

public sealed record CompilationExecutionResult(int ExitCode, string OutputPath, string EmitterDisplayName, bool RanBinary);

public static class CompilationWorkflow
{
    public static CompilerOptions CreateCompilerOptions(
        FileInfo? input,
        FileInfo? output,
        bool run,
        bool quietRun,
        int cells,
        string? target,
        bool useShellExecuteForRun = false,
        bool pauseAfterRun = false)
    {
        string inputPath = input?.FullName ?? string.Empty;
        string resolvedTarget = string.IsNullOrWhiteSpace(target) ? "win-x64" : target;
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
            QuietRun = quietRun,
            UseShellExecuteForRun = useShellExecuteForRun,
            PauseAfterRun = pauseAfterRun
        };
    }

    public static async Task<PreparedCompilation> PrepareAsync(CompilerOptions options)
    {
        IBinaryEmitter emitter = BinaryEmitterRegistry.Resolve(options.Target);
        if (options.Run && !emitter.CanExecuteOnCurrentPlatform(out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        string source = await File.ReadAllTextAsync(options.InputPath);
        byte[] binary = BrainfuckCompiler.Compile(source, options, emitter);
        return new PreparedCompilation(options, emitter, binary);
    }

    public static Task<CompilationExecutionResult> PersistOrRunAsync(PreparedCompilation preparedCompilation)
    {
        return preparedCompilation.Options.Run
            ? BuildRunAndCleanUpAsync(preparedCompilation)
            : BuildBinaryAsync(preparedCompilation);
    }

    private static async Task<CompilationExecutionResult> BuildBinaryAsync(PreparedCompilation preparedCompilation)
    {
        string outputPath = Path.GetFullPath(preparedCompilation.Options.OutputPath);
        string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(outputPath, preparedCompilation.Binary);
        preparedCompilation.Emitter.PrepareFileForExecution(outputPath);

        return new CompilationExecutionResult(0, outputPath, preparedCompilation.Emitter.DisplayName, RanBinary: false);
    }

    private static async Task<CompilationExecutionResult> BuildRunAndCleanUpAsync(PreparedCompilation preparedCompilation)
    {
        string outputPath = Path.GetFullPath(preparedCompilation.Options.OutputPath);
        string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(outputPath, preparedCompilation.Binary);
        preparedCompilation.Emitter.PrepareFileForExecution(outputPath);

        try
        {
            using Process process = new();
            ProcessStartInfo startInfo = preparedCompilation.Options.PauseAfterRun
                ? CreatePausedRunStartInfo(preparedCompilation.Options, outputPath)
                : new ProcessStartInfo
                {
                    FileName = outputPath,
                    UseShellExecute = preparedCompilation.Options.UseShellExecuteForRun,
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(preparedCompilation.Options.InputPath)) ?? Directory.GetCurrentDirectory()
                };

            process.StartInfo = startInfo;

            process.Start();
            await process.WaitForExitAsync();
            return new CompilationExecutionResult(process.ExitCode, outputPath, preparedCompilation.Emitter.DisplayName, RanBinary: true);
        }
        finally
        {
            TryDeleteTemporaryDirectory(outputDirectory);
        }
    }

    private static string CreateTemporaryOutputPath(string inputPath, string extension)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BrainFudger");
        string tempDirectory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        string fileName = $"{Path.GetFileNameWithoutExtension(inputPath)}{extension}";
        return Path.Combine(tempDirectory, fileName);
    }

    private static ProcessStartInfo CreatePausedRunStartInfo(CompilerOptions options, string outputPath)
    {
        string outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        string launcherPath = Path.Combine(outputDirectory, "run-with-pause.cmd");
        string workingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.InputPath)) ?? Directory.GetCurrentDirectory();

        string scriptContents =
            "@echo off" + Environment.NewLine +
            $"cd /d \"{workingDirectory}\"" + Environment.NewLine +
            $"\"{outputPath}\"" + Environment.NewLine +
            "set BF_EXITCODE=%ERRORLEVEL%" + Environment.NewLine +
            "echo." + Environment.NewLine +
            "pause" + Environment.NewLine +
            "exit /b %BF_EXITCODE%" + Environment.NewLine;

        File.WriteAllText(launcherPath, scriptContents);

        return new ProcessStartInfo
        {
            FileName = launcherPath,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        };
    }

    private static void TryDeleteTemporaryDirectory(string directoryPath)
    {
        try
        {
            string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BrainFudger"));
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
}
