#if LINUX
using BrainFudger.Emitters;
using BrainFudger.Models;
using BrainFudger.Services;
using Gtk;

namespace BrainFudger.Gui;

internal sealed class LinuxGuiApplication : IGuiApplicationHost
{
    private readonly IBinaryEmitter[] _emitters;

    private Gtk.Window? _window;
    private Entry? _inputEntry;
    private Entry? _outputEntry;
    private ComboBoxText? _targetCombo;
    private SpinButton? _cellsSpinButton;
    private Button? _buildButton;
    private Button? _runButton;
    private ProgressBar? _progressBar;
    private Label? _statusLabel;
    private bool _outputPathWasEdited;
    private bool _suppressOutputTracking;
    private bool _isBusy;
    private CancellationTokenSource? _progressCancellationSource;

    private LinuxGuiApplication()
    {
        _emitters = [.. BinaryEmitterRegistry.GetAll()];
    }

    public static LinuxGuiApplication Instance { get; } = new();

    public bool TryHandleHostArguments(string[] args, out int exitCode)
    {
        exitCode = 0;
        return false;
    }

    public bool ShouldLaunchDetached() => false;

    public int LaunchDetached() => Run();

    public int Run()
    {
        try
        {
            EnsureDesktopSession();
            Application.Init();
            BuildWindow();
            _window!.ShowAll();
            _progressBar!.Hide();
            Application.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private void BuildWindow()
    {
        _window = new Gtk.Window(Branding.AppDisplayName)
        {
            DefaultWidth = 700,
            DefaultHeight = 360,
            Resizable = false
        };

        _window.DeleteEvent += (_, _) => Application.Quit();
        _window.BorderWidth = 16;

        Box root = new(Orientation.Vertical, 10);
        _window.Add(root);

        root.PackStart(CreateLeftAlignedLabel("Drop a .bf file onto this window or browse for one."), expand: false, fill: false, padding: 0);

        root.PackStart(CreateFilePickerSection("Input .bf file", out _inputEntry, "Browse...", HandleBrowseForInput), expand: false, fill: false, padding: 0);
        root.PackStart(CreateFilePickerSection("Export output", out _outputEntry, "Browse...", HandleBrowseForOutput), expand: false, fill: false, padding: 0);

        Box optionsRow = new(Orientation.Horizontal, 16);
        root.PackStart(optionsRow, expand: false, fill: false, padding: 0);

        Box targetColumn = new(Orientation.Vertical, 4);
        optionsRow.PackStart(targetColumn, expand: true, fill: true, padding: 0);
        targetColumn.PackStart(CreateLeftAlignedLabel("Target emitter"), expand: false, fill: false, padding: 0);
        _targetCombo = new ComboBoxText();
        foreach (IBinaryEmitter emitter in _emitters)
        {
            _targetCombo.AppendText(emitter.TargetId);
        }

        string defaultTargetId = BinaryEmitterRegistry.GetDefaultTargetForCurrentPlatform();
        int defaultIndex = Array.FindIndex(_emitters, emitter => string.Equals(emitter.TargetId, defaultTargetId, StringComparison.OrdinalIgnoreCase));
        _targetCombo.Active = defaultIndex >= 0 ? defaultIndex : 0;
        _targetCombo.Changed += (_, _) => RefreshDerivedOutputPath();
        targetColumn.PackStart(_targetCombo, expand: false, fill: false, padding: 0);

        Box cellsColumn = new(Orientation.Vertical, 4);
        optionsRow.PackStart(cellsColumn, expand: false, fill: false, padding: 0);
        cellsColumn.PackStart(CreateLeftAlignedLabel("Tape cells"), expand: false, fill: false, padding: 0);
        _cellsSpinButton = new SpinButton(min: 1, max: 1_000_000, step: 1)
        {
            Value = 30000,
            Numeric = true
        };
        cellsColumn.PackStart(_cellsSpinButton, expand: false, fill: false, padding: 0);

        Box buttonsRow = new(Orientation.Horizontal, 10);
        root.PackStart(buttonsRow, expand: false, fill: false, padding: 0);

        _buildButton = new Button("Build");
        _buildButton.Clicked += (_, _) => BuildOrRun(run: false);
        buttonsRow.PackStart(_buildButton, expand: false, fill: false, padding: 0);

        _runButton = new Button("Run");
        _runButton.Clicked += (_, _) => BuildOrRun(run: true);
        buttonsRow.PackStart(_runButton, expand: false, fill: false, padding: 0);

        _progressBar = new ProgressBar();
        root.PackStart(_progressBar, expand: false, fill: true, padding: 0);

        _statusLabel = CreateLeftAlignedLabel("Choose a source file, target, and output path. Run uses a temporary output just like --run.");
        _statusLabel.LineWrap = true;
        root.PackStart(_statusLabel, expand: true, fill: true, padding: 0);

        _inputEntry!.Changed += (_, _) =>
        {
            _outputPathWasEdited = false;
            RefreshDerivedOutputPath(force: true);
        };

        _outputEntry!.Changed += (_, _) =>
        {
            if (!_suppressOutputTracking)
            {
                _outputPathWasEdited = true;
            }
        };

        ConfigureDragAndDrop(_window);
    }

    private Box CreateFilePickerSection(string labelText, out Entry entry, string buttonText, EventHandler onBrowse)
    {
        Box section = new(Orientation.Vertical, 4);
        section.PackStart(CreateLeftAlignedLabel(labelText), expand: false, fill: false, padding: 0);

        Box row = new(Orientation.Horizontal, 8);
        section.PackStart(row, expand: false, fill: false, padding: 0);

        entry = new Entry();
        row.PackStart(entry, expand: true, fill: true, padding: 0);

        Button browseButton = new(buttonText);
        browseButton.Clicked += onBrowse;
        row.PackStart(browseButton, expand: false, fill: false, padding: 0);

        return section;
    }

    private static Label CreateLeftAlignedLabel(string text)
    {
        return new Label(text)
        {
            Xalign = 0
        };
    }

    private void ConfigureDragAndDrop(Widget widget)
    {
        Gtk.TargetEntry[] targets =
        [
            new("text/uri-list", 0, 0)
        ];

        Gtk.Drag.DestSet(widget, DestDefaults.All, targets, Gdk.DragAction.Copy);
        widget.DragDataReceived += (_, args) =>
        {
            try
            {
                string? droppedPath = ExtractDroppedPath(args.SelectionData);
                if (string.IsNullOrWhiteSpace(droppedPath))
                {
                    return;
                }

                if (!IsSourceFilePath(droppedPath))
                {
                    ShowMessage(MessageType.Warning, "Invalid file", $"Only .bf files can be dropped into the {Branding.AppDisplayName} window.");
                    return;
                }

                _inputEntry!.Text = droppedPath;
                RefreshDerivedOutputPath(force: true);
                UpdateStatus($"Ready: {droppedPath}");
            }
            finally
            {
                Gtk.Drag.Finish(args.Context, success: true, del: false, args.Time);
            }
        };
    }

    private static string? ExtractDroppedPath(SelectionData selectionData)
    {
        string raw = selectionData.Text ?? string.Empty;
        string? uri = raw
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsedUri) && parsedUri.IsFile
            ? parsedUri.LocalPath
            : null;
    }

    private void HandleBrowseForInput(object? sender, EventArgs args)
    {
        using FileChooserDialog dialog = new(
            Branding.SourceFilePickerTitle,
            _window,
            FileChooserAction.Open,
            "Cancel",
            ResponseType.Cancel,
            "Open",
            ResponseType.Accept);

        FileFilter filter = new()
        {
            Name = $"{Branding.LanguageDisplayName} Source"
        };
        filter.AddPattern("*.bf");
        dialog.AddFilter(filter);

        if (!string.IsNullOrWhiteSpace(_inputEntry!.Text))
        {
            dialog.SetFilename(_inputEntry.Text);
        }

        if ((ResponseType)dialog.Run() != ResponseType.Accept)
        {
            return;
        }

        _inputEntry.Text = dialog.Filename;
        RefreshDerivedOutputPath(force: true);
        UpdateStatus($"Ready: {dialog.Filename}");
    }

    private void HandleBrowseForOutput(object? sender, EventArgs args)
    {
        using FileChooserDialog dialog = new(
            "Choose output path",
            _window,
            FileChooserAction.Save,
            "Cancel",
            ResponseType.Cancel,
            "Save",
            ResponseType.Accept);

        dialog.DoOverwriteConfirmation = true;

        string currentOutputPath = _outputEntry!.Text.Trim();
        if (!string.IsNullOrWhiteSpace(currentOutputPath))
        {
            dialog.SetFilename(currentOutputPath);
        }
        else
        {
            dialog.SetFilename(GetSuggestedOutputPath());
        }

        if ((ResponseType)dialog.Run() != ResponseType.Accept)
        {
            return;
        }

        SetOutputPath(dialog.Filename, markAsEdited: true);
        UpdateStatus($"Export path set to {dialog.Filename}");
    }

    private void RefreshDerivedOutputPath(bool force = false)
    {
        if (_outputPathWasEdited && !force)
        {
            return;
        }

        string inputPath = _inputEntry!.Text.Trim();
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        SetOutputPath(CreateSuggestedOutputPath(inputPath, GetSelectedEmitter().DefaultFileExtension), markAsEdited: false);
    }

    private string GetSuggestedOutputPath()
    {
        string inputPath = _inputEntry!.Text.Trim();
        return string.IsNullOrWhiteSpace(inputPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "output" + GetSelectedEmitter().DefaultFileExtension)
            : CreateSuggestedOutputPath(inputPath, GetSelectedEmitter().DefaultFileExtension);
    }

    private static string CreateSuggestedOutputPath(string inputPath, string extension)
    {
        string directory = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, fileNameWithoutExtension + extension);
    }

    private IBinaryEmitter GetSelectedEmitter()
    {
        string? targetId = _targetCombo!.ActiveText;
        return BinaryEmitterRegistry.Resolve(string.IsNullOrWhiteSpace(targetId) ? _emitters[0].TargetId : targetId);
    }

    private void BuildOrRun(bool run)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            string inputPath = _inputEntry!.Text.Trim();
            if (!IsSourceFilePath(inputPath))
            {
                ShowMessage(MessageType.Warning, "Missing source file", "Select a valid .bf source file before continuing.");
                return;
            }

            if (!File.Exists(inputPath))
            {
                ShowMessage(MessageType.Warning, "Missing source file", "The selected .bf file does not exist.");
                return;
            }

            int cellCount = _cellsSpinButton!.ValueAsInt;
            if (cellCount <= 0)
            {
                ShowMessage(MessageType.Warning, "Invalid cell count", "Tape cells must be a positive integer.");
                return;
            }

            IBinaryEmitter emitter = GetSelectedEmitter();
            string outputPath = run ? string.Empty : _outputEntry!.Text.Trim();
            if (!run && string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = GetSuggestedOutputPath();
                SetOutputPath(outputPath, markAsEdited: false);
            }

            CompilerOptions options = CompilationWorkflow.CreateCompilerOptions(
                new FileInfo(inputPath),
                run ? null : new FileInfo(outputPath),
                run,
                quietRun: false,
                cellCount,
                emitter.TargetId,
                useShellExecuteForRun: false,
                pauseAfterRun: false);

            BeginBusyState(run);
            _ = Task.Run(async () =>
            {
                try
                {
                    PreparedCompilation preparedCompilation = await CompilationWorkflow.PrepareAsync(options);
                    CompilationExecutionResult result = await CompilationWorkflow.PersistOrRunAsync(preparedCompilation);
                    Application.Invoke((_, _) => HandleBuildCompleted(run, result, null));
                }
                catch (Exception ex)
                {
                    Application.Invoke((_, _) => HandleBuildCompleted(run, null, ex));
                }
            });
        }
        catch (Exception ex)
        {
            HandleBuildCompleted(run, null, ex);
        }
    }

    private void BeginBusyState(bool run)
    {
        _isBusy = true;
        SetControlsEnabled(false);
        _progressBar!.Show();
        _progressCancellationSource = new CancellationTokenSource();
        _ = PulseProgressBarAsync(_progressCancellationSource.Token);
        UpdateStatus(run ? "Compiling and running..." : "Compiling...");
    }

    private async Task PulseProgressBarAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(80, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Application.Invoke((_, _) =>
            {
                if (_isBusy)
                {
                    _progressBar!.Pulse();
                }
            });
        }
    }

    private void HandleBuildCompleted(bool run, CompilationExecutionResult? result, Exception? exception)
    {
        _progressCancellationSource?.Cancel();
        _progressCancellationSource?.Dispose();
        _progressCancellationSource = null;

        _isBusy = false;
        _progressBar!.Hide();
        SetControlsEnabled(true);

        if (exception is not null)
        {
            UpdateStatus("Build failed.");
            ShowMessage(MessageType.Error, "Build failed", exception.Message);
            return;
        }

        if (result is null)
        {
            UpdateStatus("Build failed.");
            ShowMessage(MessageType.Error, "Build failed", "The build completed without returning a result.");
            return;
        }

        if (!run)
        {
            SetOutputPath(result.OutputPath, markAsEdited: true);
        }

        if (run)
        {
            UpdateStatus($"Program finished with exit code {result.ExitCode}.");
            ShowMessage(MessageType.Info, "Run finished", $"Emitter: {result.EmitterDisplayName}\nExit code: {result.ExitCode}");
            return;
        }

        UpdateStatus($"Built {result.OutputPath}");
        ShowMessage(MessageType.Info, "Build succeeded", $"Emitter: {result.EmitterDisplayName}\nOutput: {result.OutputPath}");
    }

    private void SetControlsEnabled(bool enabled)
    {
        _inputEntry!.Sensitive = enabled;
        _outputEntry!.Sensitive = enabled;
        _targetCombo!.Sensitive = enabled;
        _cellsSpinButton!.Sensitive = enabled;
        _buildButton!.Sensitive = enabled;
        _runButton!.Sensitive = enabled;
    }

    private void SetOutputPath(string path, bool markAsEdited)
    {
        _suppressOutputTracking = true;
        _outputEntry!.Text = path;
        _suppressOutputTracking = false;
        _outputPathWasEdited = markAsEdited;
    }

    private void UpdateStatus(string message)
    {
        _statusLabel!.Text = message;
    }

    private void ShowMessage(MessageType messageType, string title, string message)
    {
        using MessageDialog dialog = new(
            _window,
            DialogFlags.Modal,
            messageType,
            ButtonsType.Ok,
            message)
        {
            Title = title
        };

        dialog.Run();
        dialog.Hide();
    }

    private static bool IsSourceFilePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               string.Equals(Path.GetExtension(path), ".bf", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureDesktopSession()
    {
        string? display = Environment.GetEnvironmentVariable("DISPLAY");
        string? waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");

        if (string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(waylandDisplay))
        {
            throw new InvalidOperationException(
                "No graphical desktop session was detected. Start BrainFudger from an X11 or Wayland desktop session.");
        }
    }
}
#endif
