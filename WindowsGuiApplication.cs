#if BRAINFUCKER_WINDOWS_GUI
using System.Runtime.InteropServices;
using System.Text;
using BrainFudger.Emitters;
using BrainFudger.Models;
using BrainFudger.Services;

namespace BrainFudger;

internal sealed class WindowsGuiApplication
{
    private const string WindowClassName = "BrainFudgerWindowsGui";
    private const string WindowTitle = "BrainFudger";
    private const uint WindowMessageBuildCompleted = NativeMethods.WM_APP + 1;

    private const int ControlIdInputEdit = 1001;
    private const int ControlIdInputBrowse = 1002;
    private const int ControlIdOutputEdit = 1003;
    private const int ControlIdOutputBrowse = 1004;
    private const int ControlIdTargetCombo = 1005;
    private const int ControlIdCellsEdit = 1006;
    private const int ControlIdBuildButton = 1007;
    private const int ControlIdRunButton = 1008;
    private const int ControlIdStatusLabel = 1009;

    private readonly WndProc _wndProc;
    private readonly IBinaryEmitter[] _emitters;

    private GCHandle _selfHandle;
    private nint _windowHandle;
    private nint _inputEditHandle;
    private nint _inputBrowseHandle;
    private nint _outputEditHandle;
    private nint _outputBrowseHandle;
    private nint _targetComboHandle;
    private nint _cellsEditHandle;
    private nint _buildButtonHandle;
    private nint _runButtonHandle;
    private nint _progressBarHandle;
    private nint _statusLabelHandle;
    private bool _outputPathWasEdited;
    private bool _suppressOutputEditTracking;
    private bool _isBusy;
    private bool _pendingRunOperation;
    private CompilationExecutionResult? _pendingExecutionResult;
    private Exception? _pendingException;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static int Run()
    {
        if (!IsSupported)
        {
            return 1;
        }

        nint consoleWindow = NativeMethods.GetConsoleWindow();
        if (consoleWindow != 0 && NativeMethods.IsOwnConsoleWindow())
        {
            NativeMethods.ShowWindow(consoleWindow, NativeMethods.SW_HIDE);
        }

        try
        {
            return new WindowsGuiApplication().RunMessageLoop();
        }
        catch (Exception ex)
        {
            NativeMethods.MessageBox(0, ex.Message, WindowTitle, NativeMethods.MB_ICONERROR | NativeMethods.MB_OK);
            return 1;
        }
    }

    public static bool ShouldLaunchDetached()
    {
        if (!IsSupported)
        {
            return false;
        }

        nint consoleWindow = NativeMethods.GetConsoleWindow();
        return consoleWindow != 0 && !NativeMethods.IsOwnConsoleWindow();
    }

    private WindowsGuiApplication()
    {
        _wndProc = StaticWindowProc;
        _emitters = [.. BinaryEmitterRegistry.GetAll()];
    }

    private int RunMessageLoop()
    {
        nint instanceHandle = NativeMethods.GetModuleHandle(null);
        _selfHandle = GCHandle.Alloc(this);
        NativeMethods.InitializeCommonControls();

        WndClassEx windowClass = new()
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = instanceHandle,
            hIcon = 0,
            hCursor = NativeMethods.LoadCursor(0, NativeMethods.IDC_ARROW),
            hbrBackground = (nint)(NativeMethods.COLOR_WINDOW + 1),
            lpszMenuName = null,
            lpszClassName = WindowClassName,
            hIconSm = 0
        };

        ushort atom = NativeMethods.RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            throw new InvalidOperationException("Failed to register the BrainFucker window class.");
        }

        _windowHandle = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            WindowTitle,
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX,
            NativeMethods.CW_USEDEFAULT,
            NativeMethods.CW_USEDEFAULT,
            700,
            360,
            0,
            0,
            instanceHandle,
            GCHandle.ToIntPtr(_selfHandle));

        if (_windowHandle == 0)
        {
            throw new InvalidOperationException("Failed to create the BrainFucker window.");
        }

        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_SHOW);
        NativeMethods.UpdateWindow(_windowHandle);

        while (true)
        {
            int messageResult = NativeMethods.GetMessage(out Msg message, 0, 0, 0);
            if (messageResult == -1)
            {
                throw new InvalidOperationException("The BrainFucker message loop terminated unexpectedly.");
            }

            if (messageResult == 0)
            {
                return unchecked((int)message.wParam);
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }
    }

    private static nint StaticWindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        if (message == NativeMethods.WM_NCCREATE)
        {
            CreateStruct createStruct = Marshal.PtrToStructure<CreateStruct>(lParam);
            NativeMethods.SetWindowLongPtr(windowHandle, NativeMethods.GWLP_USERDATA, createStruct.lpCreateParams);
        }

        nint userData = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GWLP_USERDATA);
        if (userData != 0)
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is WindowsGuiApplication application)
            {
                return application.WindowProc(windowHandle, message, wParam, lParam);
            }
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private nint WindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_CREATE:
                InitializeControls(windowHandle);
                return 0;

            case NativeMethods.WM_COMMAND:
                HandleCommand(wParam);
                return 0;

            case WindowMessageBuildCompleted:
                HandleBuildCompleted();
                return 0;

            case NativeMethods.WM_DROPFILES:
                HandleDroppedFiles(wParam);
                return 0;

            case NativeMethods.WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return 0;

            case NativeMethods.WM_NCDESTROY:
                NativeMethods.SetWindowLongPtr(windowHandle, NativeMethods.GWLP_USERDATA, 0);
                if (_selfHandle.IsAllocated)
                {
                    _selfHandle.Free();
                }

                return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private void InitializeControls(nint windowHandle)
    {
        NativeMethods.DragAcceptFiles(windowHandle, true);

        CreateLabel(windowHandle, "Drop a .bf file onto this window or browse for one.", 20, 18, 520, 20);
        CreateLabel(windowHandle, "Input .bf file", 20, 55, 120, 20);
        _inputEditHandle = CreateEdit(windowHandle, ControlIdInputEdit, 20, 76, 520, 24);
        _inputBrowseHandle = CreateButton(windowHandle, "Browse...", ControlIdInputBrowse, 550, 75, 110, 26);

        CreateLabel(windowHandle, "Export output", 20, 112, 120, 20);
        _outputEditHandle = CreateEdit(windowHandle, ControlIdOutputEdit, 20, 133, 520, 24);
        _outputBrowseHandle = CreateButton(windowHandle, "Browse...", ControlIdOutputBrowse, 550, 132, 110, 26);

        CreateLabel(windowHandle, "Target emitter", 20, 169, 120, 20);
        _targetComboHandle = CreateComboBox(windowHandle, ControlIdTargetCombo, 20, 190, 220, 180);

        CreateLabel(windowHandle, "Tape cells", 270, 169, 120, 20);
        _cellsEditHandle = CreateEdit(windowHandle, ControlIdCellsEdit, 270, 190, 120, 24);
        SetControlText(_cellsEditHandle, "30000");

        _buildButtonHandle = CreateButton(windowHandle, "Build", ControlIdBuildButton, 430, 188, 110, 30);
        _runButtonHandle = CreateButton(windowHandle, "Run", ControlIdRunButton, 550, 188, 110, 30);

        _progressBarHandle = CreateProgressBar(windowHandle, 20, 236, 640, 20);
        NativeMethods.ShowWindow(_progressBarHandle, NativeMethods.SW_HIDE);

        _statusLabelHandle = CreateLabel(windowHandle, "Choose a source file, target, and output path. Run uses a temporary output just like --run.", 20, 266, 640, 44);

        PopulateTargetChoices();
    }

    private void PopulateTargetChoices()
    {
        foreach (IBinaryEmitter emitter in _emitters)
        {
            NativeMethods.SendMessageText(_targetComboHandle, NativeMethods.CB_ADDSTRING, 0, emitter.TargetId);
        }

        int defaultIndex = Array.FindIndex(_emitters, static emitter => string.Equals(emitter.TargetId, "win-x64", StringComparison.OrdinalIgnoreCase));
        if (defaultIndex < 0)
        {
            defaultIndex = 0;
        }

        NativeMethods.SendMessage(_targetComboHandle, NativeMethods.CB_SETCURSEL, (nuint)defaultIndex, 0);
    }

    private void HandleCommand(nuint wParam)
    {
        if (_isBusy)
        {
            return;
        }

        int controlId = NativeMethods.LowWord(wParam);
        int notificationCode = NativeMethods.HighWord(wParam);

        switch (controlId)
        {
            case ControlIdInputBrowse when notificationCode == NativeMethods.BN_CLICKED:
                BrowseForInput();
                break;

            case ControlIdOutputBrowse when notificationCode == NativeMethods.BN_CLICKED:
                BrowseForOutput();
                break;

            case ControlIdBuildButton when notificationCode == NativeMethods.BN_CLICKED:
                BuildOrRun(run: false);
                break;

            case ControlIdRunButton when notificationCode == NativeMethods.BN_CLICKED:
                BuildOrRun(run: true);
                break;

            case ControlIdTargetCombo when notificationCode == NativeMethods.CBN_SELCHANGE:
                RefreshDerivedOutputPath();
                break;

            case ControlIdInputEdit when notificationCode == NativeMethods.EN_CHANGE:
                RefreshDerivedOutputPath();
                break;

            case ControlIdOutputEdit when notificationCode == NativeMethods.EN_CHANGE:
                if (!_suppressOutputEditTracking)
                {
                    _outputPathWasEdited = true;
                }

                break;
        }
    }

    private void HandleDroppedFiles(nuint wParam)
    {
        nint dropHandle = unchecked((nint)wParam);
        try
        {
            uint fileCount = NativeMethods.DragQueryFile(dropHandle, 0xFFFFFFFF, null, 0);
            if (fileCount == 0)
            {
                return;
            }

            StringBuilder pathBuilder = new(260);
            NativeMethods.DragQueryFile(dropHandle, 0, pathBuilder, (uint)pathBuilder.Capacity);
            string inputPath = pathBuilder.ToString();
            if (!IsBrainfuckSourcePath(inputPath))
            {
                ShowWarning("Only .bf files can be dropped into the BrainFucker window.");
                return;
            }

            SetControlText(_inputEditHandle, inputPath);
            RefreshDerivedOutputPath(force: true);
            UpdateStatus($"Ready: {inputPath}");
        }
        finally
        {
            NativeMethods.DragFinish(dropHandle);
        }
    }

    private void BrowseForInput()
    {
        string? selectedFile = ShowOpenFileDialog(
            title: "Select Brainfuck source",
            filter: "Brainfuck Source (*.bf)\0*.bf\0All Files (*.*)\0*.*\0\0",
            initialPath: GetControlText(_inputEditHandle));

        if (selectedFile is null)
        {
            return;
        }

        SetControlText(_inputEditHandle, selectedFile);
        RefreshDerivedOutputPath(force: true);
        UpdateStatus($"Ready: {selectedFile}");
    }

    private void BrowseForOutput()
    {
        IBinaryEmitter emitter = GetSelectedEmitter();
        string currentOutputPath = GetControlText(_outputEditHandle).Trim();
        string? selectedFile = ShowSaveFileDialog(
            title: "Choose output path",
            filter: BuildOutputFilter(emitter),
            defaultExtension: emitter.DefaultFileExtension.TrimStart('.'),
            initialPath: string.IsNullOrWhiteSpace(currentOutputPath) ? GetSuggestedOutputPath() : currentOutputPath);

        if (selectedFile is null)
        {
            return;
        }

        SetOutputPath(selectedFile, markAsEdited: true);
        UpdateStatus($"Export path set to {selectedFile}");
    }

    private void BuildOrRun(bool run)
    {
        try
        {
            if (_isBusy)
            {
                return;
            }

            string inputPath = GetControlText(_inputEditHandle).Trim();
            if (!IsBrainfuckSourcePath(inputPath))
            {
                ShowWarning("Select a valid .bf source file before continuing.");
                return;
            }

            if (!File.Exists(inputPath))
            {
                ShowWarning("The selected .bf file does not exist.");
                return;
            }

            if (!TryGetCellCount(out int cellCount))
            {
                ShowWarning("Tape cells must be a positive integer.");
                return;
            }

            IBinaryEmitter emitter = GetSelectedEmitter();
            string outputPath = run ? string.Empty : GetControlText(_outputEditHandle).Trim();
            if (!run && string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = GetSuggestedOutputPath();
                SetOutputPath(outputPath, markAsEdited: false);
            }

            FileInfo inputFile = new(inputPath);
            FileInfo? outputFile = run ? null : new FileInfo(outputPath);

            UpdateStatus(run ? "Compiling and running..." : "Compiling...");

            CompilerOptions options = CompilationWorkflow.CreateCompilerOptions(
                inputFile,
                outputFile,
                run,
                quietRun: false,
                cellCount,
                emitter.TargetId,
                useShellExecuteForRun: run,
                pauseAfterRun: run);

            BeginBusyState(run);
            _ = Task.Run(async () =>
            {
                try
                {
                    PreparedCompilation preparedCompilation = await CompilationWorkflow.PrepareAsync(options);
                    _pendingExecutionResult = await CompilationWorkflow.PersistOrRunAsync(preparedCompilation);
                    _pendingException = null;
                }
                catch (Exception ex)
                {
                    _pendingExecutionResult = null;
                    _pendingException = ex;
                }

                if (_windowHandle != 0)
                {
                    NativeMethods.PostMessage(_windowHandle, WindowMessageBuildCompleted, 0, 0);
                }
            });
        }
        catch (Exception ex)
        {
            UpdateStatus("Build failed.");
            NativeMethods.MessageBox(_windowHandle, ex.Message, "Build Failed", NativeMethods.MB_ICONERROR | NativeMethods.MB_OK);
        }
    }

    private void BeginBusyState(bool run)
    {
        _isBusy = true;
        _pendingRunOperation = run;
        _pendingExecutionResult = null;
        _pendingException = null;
        SetControlsEnabled(false);
        UpdateStatus(run ? "Compiling and running..." : "Compiling...");
        NativeMethods.ShowWindow(_progressBarHandle, NativeMethods.SW_SHOW);
        NativeMethods.SendMessage(_progressBarHandle, NativeMethods.PBM_SETMARQUEE, 1, 0);
    }

    private void HandleBuildCompleted()
    {
        _isBusy = false;
        NativeMethods.SendMessage(_progressBarHandle, NativeMethods.PBM_SETMARQUEE, 0, 0);
        NativeMethods.ShowWindow(_progressBarHandle, NativeMethods.SW_HIDE);
        SetControlsEnabled(true);

        if (_pendingException is not null)
        {
            Exception ex = _pendingException;
            _pendingException = null;
            _pendingExecutionResult = null;
            UpdateStatus("Build failed.");
            NativeMethods.MessageBox(_windowHandle, ex.Message, "Build Failed", NativeMethods.MB_ICONERROR | NativeMethods.MB_OK);
            return;
        }

        if (_pendingExecutionResult is null)
        {
            UpdateStatus("Build failed.");
            NativeMethods.MessageBox(_windowHandle, "The build completed without returning a result.", "Build Failed", NativeMethods.MB_ICONERROR | NativeMethods.MB_OK);
            return;
        }

        CompilationExecutionResult result = _pendingExecutionResult;
        _pendingExecutionResult = null;

        if (_pendingRunOperation)
        {
            UpdateStatus($"Program finished with exit code {result.ExitCode}.");
            NativeMethods.MessageBox(
                _windowHandle,
                $"Emitter: {result.EmitterDisplayName}\nExit code: {result.ExitCode}",
                "Run Finished",
                NativeMethods.MB_ICONINFORMATION | NativeMethods.MB_OK);
        }
        else
        {
            UpdateStatus($"Built {result.OutputPath}");
            NativeMethods.MessageBox(
                _windowHandle,
                $"Emitter: {result.EmitterDisplayName}\nOutput: {result.OutputPath}",
                "Build Succeeded",
                NativeMethods.MB_ICONINFORMATION | NativeMethods.MB_OK);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        NativeMethods.EnableWindow(_inputEditHandle, enabled);
        NativeMethods.EnableWindow(_inputBrowseHandle, enabled);
        NativeMethods.EnableWindow(_outputEditHandle, enabled);
        NativeMethods.EnableWindow(_outputBrowseHandle, enabled);
        NativeMethods.EnableWindow(_targetComboHandle, enabled);
        NativeMethods.EnableWindow(_cellsEditHandle, enabled);
        NativeMethods.EnableWindow(_buildButtonHandle, enabled);
        NativeMethods.EnableWindow(_runButtonHandle, enabled);
    }

    private bool TryGetCellCount(out int cellCount)
    {
        string value = GetControlText(_cellsEditHandle).Trim();
        return int.TryParse(value, out cellCount) && cellCount > 0;
    }

    private void RefreshDerivedOutputPath(bool force = false)
    {
        if (_outputPathWasEdited && !force)
        {
            return;
        }

        string suggestedPath = GetSuggestedOutputPath();
        if (string.IsNullOrWhiteSpace(suggestedPath))
        {
            return;
        }

        SetOutputPath(suggestedPath, markAsEdited: false);
    }

    private string GetSuggestedOutputPath()
    {
        string inputPath = GetControlText(_inputEditHandle).Trim();
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return string.Empty;
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            return string.Empty;
        }

        string directory = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        string extension = GetSelectedEmitter().DefaultFileExtension;
        return Path.Combine(directory, fileNameWithoutExtension + extension);
    }

    private IBinaryEmitter GetSelectedEmitter()
    {
        int selectedIndex = unchecked((int)NativeMethods.SendMessage(_targetComboHandle, NativeMethods.CB_GETCURSEL, 0, 0));
        if (selectedIndex < 0 || selectedIndex >= _emitters.Length)
        {
            return _emitters[0];
        }

        return _emitters[selectedIndex];
    }

    private void SetOutputPath(string outputPath, bool markAsEdited)
    {
        _suppressOutputEditTracking = true;
        try
        {
            SetControlText(_outputEditHandle, outputPath);
        }
        finally
        {
            _suppressOutputEditTracking = false;
        }

        _outputPathWasEdited = markAsEdited;
    }

    private void UpdateStatus(string text) => SetControlText(_statusLabelHandle, text);

    private void ShowWarning(string message)
    {
        UpdateStatus(message);
        NativeMethods.MessageBox(_windowHandle, message, WindowTitle, NativeMethods.MB_ICONWARNING | NativeMethods.MB_OK);
    }

    private static bool IsBrainfuckSourcePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetExtension(path), ".bf", StringComparison.OrdinalIgnoreCase);

    private static string BuildOutputFilter(IBinaryEmitter emitter)
    {
        string extension = emitter.DefaultFileExtension.TrimStart('.');
        string upperExtension = extension.ToUpperInvariant();
        return $"{upperExtension} Files (*.{extension})\0*.{extension}\0All Files (*.*)\0*.*\0\0";
    }

    private static nint CreateLabel(nint parent, string text, int x, int y, int width, int height)
    {
        nint handle = NativeMethods.CreateWindowEx(
            0,
            "STATIC",
            text,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
            x,
            y,
            width,
            height,
            parent,
            0,
            NativeMethods.GetModuleHandle(null),
            0);
        ApplyDefaultGuiFont(handle);
        return handle;
    }

    private nint CreateEdit(nint parent, int controlId, int x, int y, int width, int height)
    {
        nint handle = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_CLIENTEDGE,
            "EDIT",
            string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.ES_AUTOHSCROLL,
            x,
            y,
            width,
            height,
            parent,
            (nint)controlId,
            NativeMethods.GetModuleHandle(null),
            0);
        ApplyDefaultGuiFont(handle);
        return handle;
    }

    private nint CreateButton(nint parent, string text, int controlId, int x, int y, int width, int height)
    {
        nint handle = NativeMethods.CreateWindowEx(
            0,
            "BUTTON",
            text,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_PUSHBUTTON,
            x,
            y,
            width,
            height,
            parent,
            (nint)controlId,
            NativeMethods.GetModuleHandle(null),
            0);
        ApplyDefaultGuiFont(handle);
        return handle;
    }

    private nint CreateComboBox(nint parent, int controlId, int x, int y, int width, int height)
    {
        nint handle = NativeMethods.CreateWindowEx(
            0,
            "COMBOBOX",
            string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.WS_VSCROLL | NativeMethods.CBS_DROPDOWNLIST,
            x,
            y,
            width,
            height,
            parent,
            (nint)controlId,
            NativeMethods.GetModuleHandle(null),
            0);
        ApplyDefaultGuiFont(handle);
        return handle;
    }

    private nint CreateProgressBar(nint parent, int x, int y, int width, int height)
    {
        return NativeMethods.CreateWindowEx(
            0,
            NativeMethods.ProgressBarClassName,
            string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.PBS_MARQUEE,
            x,
            y,
            width,
            height,
            parent,
            0,
            NativeMethods.GetModuleHandle(null),
            0);
    }

    private static void ApplyDefaultGuiFont(nint controlHandle)
    {
        nint fontHandle = NativeMethods.GetStockObject(NativeMethods.DEFAULT_GUI_FONT);
        NativeMethods.SendMessage(controlHandle, NativeMethods.WM_SETFONT, unchecked((nuint)fontHandle), 1);
    }

    private static string GetControlText(nint controlHandle)
    {
        int length = NativeMethods.GetWindowTextLength(controlHandle);
        StringBuilder builder = new(length + 1);
        _ = NativeMethods.GetWindowText(controlHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static void SetControlText(nint controlHandle, string text)
    {
        if (controlHandle != 0)
        {
            NativeMethods.SetWindowText(controlHandle, text);
        }
    }

    private static string? ShowOpenFileDialog(string title, string filter, string initialPath)
    {
        return ShowFileDialog(
            saveDialog: false,
            title,
            filter,
            defaultExtension: null,
            initialPath,
            NativeMethods.OFN_FILEMUSTEXIST | NativeMethods.OFN_PATHMUSTEXIST);
    }

    private static string? ShowSaveFileDialog(string title, string filter, string defaultExtension, string initialPath)
    {
        return ShowFileDialog(
            saveDialog: true,
            title,
            filter,
            defaultExtension,
            initialPath,
            NativeMethods.OFN_OVERWRITEPROMPT | NativeMethods.OFN_PATHMUSTEXIST);
    }

    private static unsafe string? ShowFileDialog(bool saveDialog, string title, string filter, string? defaultExtension, string initialPath, int flags)
    {
        const int maxPathLength = 4096;
        char* fileBuffer = stackalloc char[maxPathLength];
        for (int i = 0; i < maxPathLength; i++)
        {
            fileBuffer[i] = '\0';
        }

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            int copyLength = Math.Min(initialPath.Length, maxPathLength - 1);
            initialPath.AsSpan(0, copyLength).CopyTo(new Span<char>(fileBuffer, maxPathLength));
            fileBuffer[copyLength] = '\0';
        }

        fixed (char* filterPtr = filter)
        fixed (char* titlePtr = title)
        fixed (char* defaultExtensionPtr = defaultExtension)
        {
            OpenFileName openFileName = new()
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = 0,
                lpstrFilter = filterPtr,
                lpstrFile = fileBuffer,
                nMaxFile = maxPathLength,
                lpstrTitle = titlePtr,
                lpstrDefExt = defaultExtensionPtr,
                Flags = flags
            };

            bool success = saveDialog
                ? NativeMethods.GetSaveFileName(ref openFileName)
                : NativeMethods.GetOpenFileName(ref openFileName);

            return success ? new string(fileBuffer) : null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStruct
    {
        public nint lpCreateParams;
        public nint hInstance;
        public nint hMenu;
        public nint hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public nint lpszName;
        public nint lpszClass;
        public uint dwExStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public unsafe char* lpstrFilter;
        public unsafe char* lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public unsafe char* lpstrFile;
        public int nMaxFile;
        public unsafe char* lpstrFileTitle;
        public int nMaxFileTitle;
        public unsafe char* lpstrInitialDir;
        public unsafe char* lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public unsafe char* lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public unsafe char* lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint windowHandle, uint message, nuint wParam, nint lParam);

    private static class NativeMethods
    {
        public const string ProgressBarClassName = "msctls_progress32";
        public const int BN_CLICKED = 0;
        public const int BS_PUSHBUTTON = 0x00000000;
        public const int CBN_SELCHANGE = 1;
        public const int CB_ADDSTRING = 0x0143;
        public const int CB_GETCURSEL = 0x0147;
        public const int CB_SETCURSEL = 0x014E;
        public const int CBS_DROPDOWNLIST = 0x0003;
        public const int COLOR_WINDOW = 5;
        public const int CW_USEDEFAULT = unchecked((int)0x80000000);
        public const int DEFAULT_GUI_FONT = 17;
        public const int EN_CHANGE = 0x0300;
        public const int ES_AUTOHSCROLL = 0x0080;
        public const int GWLP_USERDATA = -21;
        public const int IDC_ARROW = 32512;
        public const int MB_ICONERROR = 0x00000010;
        public const int MB_ICONINFORMATION = 0x00000040;
        public const int MB_ICONWARNING = 0x00000030;
        public const int MB_OK = 0x00000000;
        public const int OFN_FILEMUSTEXIST = 0x00001000;
        public const int OFN_OVERWRITEPROMPT = 0x00000002;
        public const int OFN_PATHMUSTEXIST = 0x00000800;
        public const int PBM_SETMARQUEE = 0x040A;
        public const int PBS_MARQUEE = 0x08;
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int WM_APP = 0x8000;
        public const int WM_COMMAND = 0x0111;
        public const int WM_CREATE = 0x0001;
        public const int WM_DESTROY = 0x0002;
        public const int WM_DROPFILES = 0x0233;
        public const int WM_NCCREATE = 0x0081;
        public const int WM_NCDESTROY = 0x0082;
        public const int WM_SETFONT = 0x0030;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_CHILD = 0x40000000;
        public const int WS_EX_CLIENTEDGE = 0x00000200;
        public const int WS_MINIMIZEBOX = 0x00020000;
        public const int WS_OVERLAPPED = 0x00000000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_TABSTOP = 0x00010000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_VSCROLL = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        public struct InitCommonControlsExData
        {
            public uint dwSize;
            public uint dwICC;
        }

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitCommonControlsEx(ref InitCommonControlsExData data);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOpenFileName(ref OpenFileName openFileName);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSaveFileName(ref OpenFileName openFileName);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern nint GetStockObject(int objectType);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint GetModuleHandle(string? moduleName);

        [DllImport("shell32.dll")]
        public static extern void DragAcceptFiles(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool accept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint DragQueryFile(nint dropHandle, uint fileIndex, StringBuilder? fileName, uint fileNameLength);

        [DllImport("shell32.dll")]
        public static extern void DragFinish(nint dropHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateWindowEx(
            int exStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            nint parentHandle,
            nint menuHandle,
            nint instanceHandle,
            nint param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern nint DefWindowProc(nint windowHandle, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint DispatchMessage(ref Msg message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnableWindow(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool enable);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GetConsoleWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out Msg message, nint windowHandle, uint minMessage, uint maxMessage);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(nint windowHandle, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(nint windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint GetWindowLongPtr(nint windowHandle, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint LoadCursor(nint instanceHandle, int cursorId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(nint windowHandle, string text, string caption, int type);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(nint windowHandle, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern nint SendMessage(nint windowHandle, int message, nuint wParam, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern nint SendMessage(nint windowHandle, int message, nuint wParam, int lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        public static extern nint SendMessageText(nint windowHandle, int message, nuint wParam, string text);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newLong);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowText(nint windowHandle, string text);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint windowHandle, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateWindow(nint windowHandle);

        public static int HighWord(nuint value) => unchecked((short)((value >> 16) & 0xFFFF));

        public static int LowWord(nuint value) => unchecked((short)(value & 0xFFFF));

        public static void InitializeCommonControls()
        {
            InitCommonControlsExData data = new()
            {
                dwSize = (uint)Marshal.SizeOf<InitCommonControlsExData>(),
                dwICC = 0x00000020
            };

            _ = InitCommonControlsEx(ref data);
        }

        public static bool IsOwnConsoleWindow()
        {
            Span<uint> processIds = stackalloc uint[8];
            uint processCount = GetConsoleProcessList(ref MemoryMarshal.GetReference(processIds), (uint)processIds.Length);
            return processCount == 1;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList(ref uint processList, uint processCount);
    }
}
#endif
