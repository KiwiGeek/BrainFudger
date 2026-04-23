using System.Runtime.InteropServices;
using BrainFudger.Emitters;
using BrainFudger.Models;
using BrainFudger.Services;

namespace BrainFudger.Mac;

internal sealed class MacGuiApplication
{
    private const string WindowTitle = "BrainFudger";
    private const nuint ActivationPolicyRegular = 0;
    private const nuint WindowStyleMaskTitled = 1;
    private const nuint WindowStyleMaskClosable = 2;
    private const nuint WindowStyleMaskMiniaturizable = 4;
    private const nuint WindowStyleMaskResizable = 8;
    private const nuint BackingStoreBuffered = 2;
    private const nint ModalResponseOk = 1;

    private static MacGuiApplication? s_current;

    private readonly IBinaryEmitter[] _emitters;

    private nint _app;
    private nint _window;
    private nint _inputField;
    private nint _outputField;
    private nint _targetPopup;
    private nint _cellsField;
    private nint _statusLabel;
    private nint _actionTarget;
    private bool _outputPathWasEdited;

    private MacGuiApplication()
    {
        _emitters = [.. BinaryEmitterRegistry.GetAll()];
    }

    public static int Run()
    {
        s_current = new MacGuiApplication();
        return s_current.RunApplication();
    }

    private int RunApplication()
    {
        nint pool = Cocoa.SendIntPtr(Cocoa.GetClass("NSAutoreleasePool"), "new");

        try
        {
            RegisterActionTargetClass();

            _app = Cocoa.SendIntPtr(Cocoa.GetClass("NSApplication"), "sharedApplication");
            Cocoa.SendVoid(_app, "setActivationPolicy:", ActivationPolicyRegular);

            _actionTarget = Cocoa.CreateObject(Cocoa.ActionTargetClassName);
            Cocoa.SendVoid(_app, "setDelegate:", _actionTarget);

            BuildWindow();

            Cocoa.SendVoid(_app, "activateIgnoringOtherApps:", true);
            Cocoa.SendVoid(_window, "makeKeyAndOrderFront:", nint.Zero);
            Cocoa.SendVoid(_app, "run");
            return 0;
        }
        catch (Exception ex)
        {
            ShowAlert("BrainFudger Mac GUI Failed", ex.Message, critical: true);
            return 1;
        }
        finally
        {
            if (pool != 0)
            {
                Cocoa.SendVoid(pool, "drain");
            }
        }
    }

    private void BuildWindow()
    {
        nuint styleMask = WindowStyleMaskTitled | WindowStyleMaskClosable | WindowStyleMaskMiniaturizable | WindowStyleMaskResizable;
        NSRect frame = new(0, 0, 760, 360);

        _window = Cocoa.SendIntPtr(
            Cocoa.SendIntPtr(Cocoa.GetClass("NSWindow"), "alloc"),
            "initWithContentRect:styleMask:backing:defer:",
            frame,
            styleMask,
            BackingStoreBuffered,
            false);

        Cocoa.SendVoid(_window, "setTitle:", Cocoa.ToNSString(WindowTitle));
        Cocoa.SendVoid(_window, "center");

        nint contentView = Cocoa.SendIntPtr(_window, "contentView");

        AddLabel(contentView, "Brainfuck source", new NSRect(20, 300, 140, 24));
        _inputField = AddTextField(contentView, new NSRect(20, 270, 560, 28), editable: true);
        AddButton(contentView, "Browse…", new NSRect(600, 268, 120, 32), "browseInput:");

        AddLabel(contentView, "Output", new NSRect(20, 228, 140, 24));
        _outputField = AddTextField(contentView, new NSRect(20, 198, 560, 28), editable: true);
        AddButton(contentView, "Choose…", new NSRect(600, 196, 120, 32), "browseOutput:");

        AddLabel(contentView, "Target", new NSRect(20, 156, 120, 24));
        _targetPopup = AddPopupButton(contentView, new NSRect(20, 126, 240, 30), "targetChanged:");
        foreach (IBinaryEmitter emitter in _emitters)
        {
            Cocoa.SendVoid(_targetPopup, "addItemWithTitle:", Cocoa.ToNSString(emitter.TargetId));
        }

        int defaultTargetIndex = Array.FindIndex(_emitters, static emitter => string.Equals(emitter.TargetId, "osx-arm64", StringComparison.OrdinalIgnoreCase));
        if (defaultTargetIndex < 0)
        {
            defaultTargetIndex = 0;
        }

        Cocoa.SendVoid(_targetPopup, "selectItemAtIndex:", (nint)defaultTargetIndex);

        AddLabel(contentView, "Cells", new NSRect(300, 156, 120, 24));
        _cellsField = AddTextField(contentView, new NSRect(300, 126, 120, 28), editable: true);
        SetControlText(_cellsField, "30000");

        AddButton(contentView, "Build", new NSRect(20, 70, 120, 34), "buildBinary:");
        AddButton(contentView, "Run", new NSRect(156, 70, 120, 34), "runBinary:");

        _statusLabel = AddTextField(contentView, new NSRect(20, 20, 700, 28), editable: false, bordered: false, drawBackground: false);
        SetControlText(_statusLabel, "Choose a source file, target, and output path.");

        RefreshDerivedOutputPath(force: true);
    }

    private void BrowseForInput()
    {
        nint panel = Cocoa.SendIntPtr(Cocoa.GetClass("NSOpenPanel"), "openPanel");
        Cocoa.SendVoid(panel, "setCanChooseFiles:", true);
        Cocoa.SendVoid(panel, "setCanChooseDirectories:", false);
        Cocoa.SendVoid(panel, "setAllowsMultipleSelection:", false);
        Cocoa.SendVoid(panel, "setPrompt:", Cocoa.ToNSString("Select"));

        if (Cocoa.SendInt(panel, "runModal") != ModalResponseOk)
        {
            return;
        }

        string? selectedPath = Cocoa.GetUrlPath(Cocoa.SendIntPtr(panel, "URL"));
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        SetControlText(_inputField, selectedPath);
        _outputPathWasEdited = false;
        RefreshDerivedOutputPath(force: true);
        SetStatus($"Ready: {selectedPath}");
    }

    private void BrowseForOutput()
    {
        nint panel = Cocoa.SendIntPtr(Cocoa.GetClass("NSSavePanel"), "savePanel");
        Cocoa.SendVoid(panel, "setPrompt:", Cocoa.ToNSString("Build"));

        string currentOutput = GetControlText(_outputField).Trim();
        if (!string.IsNullOrWhiteSpace(currentOutput))
        {
            Cocoa.SendVoid(panel, "setNameFieldStringValue:", Cocoa.ToNSString(Path.GetFileName(currentOutput)));
        }

        if (Cocoa.SendInt(panel, "runModal") != ModalResponseOk)
        {
            return;
        }

        string? selectedPath = Cocoa.GetUrlPath(Cocoa.SendIntPtr(panel, "URL"));
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        SetOutputPath(selectedPath, markAsEdited: true);
        SetStatus($"Output path set to {selectedPath}");
    }

    private void HandleTargetChanged()
    {
        RefreshDerivedOutputPath(force: false);
    }

    private void BuildBinary() => BuildOrRun(run: false);

    private void RunBinary() => BuildOrRun(run: true);

    private void BuildOrRun(bool run)
    {
        try
        {
            string inputPath = GetControlText(_inputField).Trim();
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                ShowAlert("No source file selected", "Choose a Brainfuck source file first.", critical: false);
                return;
            }

            if (!File.Exists(inputPath))
            {
                ShowAlert("Source file missing", $"The file '{inputPath}' does not exist.", critical: true);
                return;
            }

            if (!int.TryParse(GetControlText(_cellsField).Trim(), out int cells) || cells <= 0)
            {
                ShowAlert("Invalid cell count", "Cells must be a positive integer.", critical: true);
                return;
            }

            string outputPath = GetControlText(_outputField).Trim();
            if (!run && string.IsNullOrWhiteSpace(outputPath))
            {
                ShowAlert("No output path selected", "Choose where the built binary should be written.", critical: false);
                return;
            }

            string targetId = GetSelectedTargetId();
            SetStatus(run ? "Building and running..." : "Building...");

            CompilerOptions options = CompilationWorkflow.CreateCompilerOptions(
                new FileInfo(inputPath),
                string.IsNullOrWhiteSpace(outputPath) ? null : new FileInfo(outputPath),
                run,
                quietRun: false,
                cells,
                targetId);

            PreparedCompilation prepared = CompilationWorkflow.PrepareAsync(options).GetAwaiter().GetResult();
            CompilationExecutionResult result = CompilationWorkflow.PersistOrRunAsync(prepared).GetAwaiter().GetResult();

            if (!run)
            {
                SetOutputPath(result.OutputPath, markAsEdited: true);
            }

            SetStatus(run
                ? $"Run finished with exit code {result.ExitCode}."
                : $"Built {result.OutputPath}");

            ShowAlert(
                run ? "Run finished" : "Build succeeded",
                run
                    ? $"Emitter: {result.EmitterDisplayName}\nExit code: {result.ExitCode}"
                    : $"Emitter: {result.EmitterDisplayName}\nOutput: {result.OutputPath}",
                critical: false);
        }
        catch (Exception ex)
        {
            SetStatus("Build failed.");
            ShowAlert("Build failed", ex.Message, critical: true);
        }
    }

    private void RefreshDerivedOutputPath(bool force)
    {
        if (_outputPathWasEdited && !force)
        {
            return;
        }

        string inputPath = GetControlText(_inputField).Trim();
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        IBinaryEmitter emitter = GetSelectedEmitter();
        string derivedPath = Path.ChangeExtension(inputPath, emitter.DefaultFileExtension) ?? inputPath + emitter.DefaultFileExtension;
        SetOutputPath(derivedPath, markAsEdited: false);
    }

    private IBinaryEmitter GetSelectedEmitter()
    {
        string targetId = GetSelectedTargetId();
        return BinaryEmitterRegistry.Resolve(targetId);
    }

    private string GetSelectedTargetId()
    {
        string targetId = Cocoa.GetNSString(Cocoa.SendIntPtr(_targetPopup, "titleOfSelectedItem"));
        return string.IsNullOrWhiteSpace(targetId) ? _emitters[0].TargetId : targetId;
    }

    private void SetOutputPath(string value, bool markAsEdited)
    {
        SetControlText(_outputField, value);
        _outputPathWasEdited = markAsEdited;
    }

    private void SetStatus(string message)
    {
        SetControlText(_statusLabel, message);
    }

    private static void RegisterActionTargetClass()
    {
        if (Cocoa.ActionTargetClass != 0)
        {
            return;
        }

        nint nsObject = Cocoa.GetClass("NSObject");
        nint actionClass = Cocoa.objc_allocateClassPair(nsObject, Cocoa.ActionTargetClassName, 0);
        if (actionClass == 0)
        {
            nint existing = Cocoa.GetClass(Cocoa.ActionTargetClassName);
            if (existing != 0)
            {
                Cocoa.ActionTargetClass = existing;
                return;
            }

            throw new InvalidOperationException("Could not create the AppKit action target class.");
        }

        Cocoa.AddMethod(actionClass, "browseInput:", Cocoa.BrowseInputCallback, "v@:@");
        Cocoa.AddMethod(actionClass, "browseOutput:", Cocoa.BrowseOutputCallback, "v@:@");
        Cocoa.AddMethod(actionClass, "targetChanged:", Cocoa.TargetChangedCallback, "v@:@");
        Cocoa.AddMethod(actionClass, "buildBinary:", Cocoa.BuildBinaryCallback, "v@:@");
        Cocoa.AddMethod(actionClass, "runBinary:", Cocoa.RunBinaryCallback, "v@:@");
        Cocoa.AddMethod(actionClass, "applicationShouldTerminateAfterLastWindowClosed:", Cocoa.CloseAfterLastWindowCallback, "c@:@");

        Cocoa.objc_registerClassPair(actionClass);
        Cocoa.ActionTargetClass = actionClass;
    }

    private nint AddLabel(nint parent, string text, NSRect frame)
    {
        nint label = AddTextField(parent, frame, editable: false, bordered: false, drawBackground: false);
        SetControlText(label, text);
        return label;
    }

    private nint AddTextField(nint parent, NSRect frame, bool editable, bool bordered = true, bool drawBackground = true)
    {
        nint field = Cocoa.SendIntPtr(
            Cocoa.SendIntPtr(Cocoa.GetClass("NSTextField"), "alloc"),
            "initWithFrame:",
            frame);

        Cocoa.SendVoid(field, "setEditable:", editable);
        Cocoa.SendVoid(field, "setSelectable:", editable);
        Cocoa.SendVoid(field, "setBezeled:", bordered);
        Cocoa.SendVoid(field, "setBordered:", bordered);
        Cocoa.SendVoid(field, "setDrawsBackground:", drawBackground);
        Cocoa.SendVoid(parent, "addSubview:", field);
        return field;
    }

    private nint AddButton(nint parent, string title, NSRect frame, string actionSelector)
    {
        nint button = Cocoa.SendIntPtr(
            Cocoa.SendIntPtr(Cocoa.GetClass("NSButton"), "alloc"),
            "initWithFrame:",
            frame);

        Cocoa.SendVoid(button, "setTitle:", Cocoa.ToNSString(title));
        Cocoa.SendVoid(button, "setTarget:", _actionTarget);
        Cocoa.SendVoid(button, "setAction:", Cocoa.GetSelector(actionSelector));
        Cocoa.SendVoid(parent, "addSubview:", button);
        return button;
    }

    private nint AddPopupButton(nint parent, NSRect frame, string actionSelector)
    {
        nint popup = Cocoa.SendIntPtr(
            Cocoa.SendIntPtr(Cocoa.GetClass("NSPopUpButton"), "alloc"),
            "initWithFrame:pullsDown:",
            frame,
            false);

        Cocoa.SendVoid(popup, "setTarget:", _actionTarget);
        Cocoa.SendVoid(popup, "setAction:", Cocoa.GetSelector(actionSelector));
        Cocoa.SendVoid(parent, "addSubview:", popup);
        return popup;
    }

    private static void SetControlText(nint control, string text)
    {
        Cocoa.SendVoid(control, "setStringValue:", Cocoa.ToNSString(text));
    }

    private static string GetControlText(nint control)
    {
        return Cocoa.GetNSString(Cocoa.SendIntPtr(control, "stringValue"));
    }

    private static void ShowAlert(string title, string message, bool critical)
    {
        nint alert = Cocoa.SendIntPtr(Cocoa.SendIntPtr(Cocoa.GetClass("NSAlert"), "alloc"), "init");
        Cocoa.SendVoid(alert, "setMessageText:", Cocoa.ToNSString(title));
        Cocoa.SendVoid(alert, "setInformativeText:", Cocoa.ToNSString(message));
        Cocoa.SendVoid(alert, "setAlertStyle:", critical ? (nint)2 : (nint)1);
        Cocoa.SendInt(alert, "runModal");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSPoint(double x, double y)
    {
        public readonly double X = x;
        public readonly double Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSSize(double width, double height)
    {
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSRect(double x, double y, double width, double height)
    {
        public readonly NSPoint Origin = new(x, y);
        public readonly NSSize Size = new(width, height);
    }

    private static class Cocoa
    {
        public const string ActionTargetClassName = "BrainFudgerMacActionTarget";
        public static nint ActionTargetClass;

        public static readonly ObjcAction BrowseInputCallback = BrowseInput;
        public static readonly ObjcAction BrowseOutputCallback = BrowseOutput;
        public static readonly ObjcAction TargetChangedCallback = TargetChanged;
        public static readonly ObjcAction BuildBinaryCallback = BuildBinary;
        public static readonly ObjcAction RunBinaryCallback = RunBinary;
        public static readonly ObjcShouldTerminate CloseAfterLastWindowCallback = CloseAfterLastWindow;

        private static readonly Dictionary<string, nint> SelectorCache = [];
        private static readonly Dictionary<string, nint> ClassCache = [];

        public static nint GetSelector(string name)
        {
            if (SelectorCache.TryGetValue(name, out nint selector))
            {
                return selector;
            }

            selector = sel_registerName(name);
            SelectorCache[name] = selector;
            return selector;
        }

        public static nint GetClass(string name)
        {
            if (ClassCache.TryGetValue(name, out nint @class))
            {
                return @class;
            }

            @class = objc_getClass(name);
            ClassCache[name] = @class;
            return @class;
        }

        public static nint CreateObject(string className)
        {
            nint @class = ActionTargetClass != 0 && string.Equals(className, ActionTargetClassName, StringComparison.Ordinal)
                ? ActionTargetClass
                : GetClass(className);

            return SendIntPtr(SendIntPtr(@class, "alloc"), "init");
        }

        public static void AddMethod(nint @class, string selectorName, Delegate callback, string typeEncoding)
        {
            if (!class_addMethod(@class, GetSelector(selectorName), Marshal.GetFunctionPointerForDelegate(callback), typeEncoding))
            {
                throw new InvalidOperationException($"Could not register Objective-C method '{selectorName}'.");
            }
        }

        public static string GetNSString(nint nsString)
        {
            if (nsString == 0)
            {
                return string.Empty;
            }

            nint utf8 = IntPtr_objc_msgSend(nsString, GetSelector("UTF8String"));
            return utf8 == 0 ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
        }

        public static nint ToNSString(string value)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value + '\0');
            IntPtr buffer = Marshal.AllocHGlobal(utf8.Length);
            try
            {
                Marshal.Copy(utf8, 0, buffer, utf8.Length);
                return IntPtr_objc_msgSend_IntPtr(GetClass("NSString"), GetSelector("stringWithUTF8String:"), buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static string? GetUrlPath(nint url)
        {
            if (url == 0)
            {
                return null;
            }

            string path = GetNSString(IntPtr_objc_msgSend(url, GetSelector("path")));
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        public static nint SendIntPtr(nint receiver, string selectorName) =>
            IntPtr_objc_msgSend(receiver, GetSelector(selectorName));

        public static nint SendIntPtr(nint receiver, string selectorName, nint arg1) =>
            IntPtr_objc_msgSend_IntPtr(receiver, GetSelector(selectorName), arg1);

        public static nint SendIntPtr(nint receiver, string selectorName, NSRect rect) =>
            IntPtr_objc_msgSend_NSRect(receiver, GetSelector(selectorName), rect);

        public static nint SendIntPtr(nint receiver, string selectorName, NSRect rect, bool pullsDown) =>
            IntPtr_objc_msgSend_NSRect_bool(receiver, GetSelector(selectorName), rect, pullsDown);

        public static nint SendIntPtr(nint receiver, string selectorName, NSRect rect, nuint styleMask, nuint backing, bool defer) =>
            IntPtr_objc_msgSend_NSRect_nuint_nuint_bool(receiver, GetSelector(selectorName), rect, styleMask, backing, defer);

        public static void SendVoid(nint receiver, string selectorName) =>
            void_objc_msgSend(receiver, GetSelector(selectorName));

        public static void SendVoid(nint receiver, string selectorName, nint arg1) =>
            void_objc_msgSend_nint(receiver, GetSelector(selectorName), arg1);

        public static void SendVoid(nint receiver, string selectorName, nuint arg1) =>
            void_objc_msgSend_nuint(receiver, GetSelector(selectorName), arg1);

        public static void SendVoid(nint receiver, string selectorName, bool arg1) =>
            void_objc_msgSend_bool(receiver, GetSelector(selectorName), arg1);

        public static int SendInt(nint receiver, string selectorName) =>
            (int)nint_objc_msgSend(receiver, GetSelector(selectorName));

        private static void BrowseInput(nint self, nint cmd, nint sender) => s_current?.BrowseForInput();

        private static void BrowseOutput(nint self, nint cmd, nint sender) => s_current?.BrowseForOutput();

        private static void TargetChanged(nint self, nint cmd, nint sender) => s_current?.HandleTargetChanged();

        private static void BuildBinary(nint self, nint cmd, nint sender) => s_current?.BuildBinary();

        private static void RunBinary(nint self, nint cmd, nint sender) => s_current?.RunBinary();

        private static bool CloseAfterLastWindow(nint self, nint cmd, nint sender) => true;

        [DllImport("/usr/lib/libobjc.A.dylib")]
        public static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        public static extern void objc_registerClassPair(nint cls);

        [DllImport("/usr/lib/libobjc.A.dylib", CharSet = CharSet.Ansi)]
        public static extern nint objc_getClass(string name);

        [DllImport("/usr/lib/libobjc.A.dylib", CharSet = CharSet.Ansi)]
        public static extern nint sel_registerName(string selectorName);

        [DllImport("/usr/lib/libobjc.A.dylib", CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool class_addMethod(nint cls, nint name, nint imp, string types);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern nint IntPtr_objc_msgSend(nint receiver, nint selector);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern nint IntPtr_objc_msgSend_IntPtr(nint receiver, nint selector, nint arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern nint IntPtr_objc_msgSend_NSRect(nint receiver, nint selector, NSRect rect);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern nint IntPtr_objc_msgSend_NSRect_bool(nint receiver, nint selector, NSRect rect, [MarshalAs(UnmanagedType.I1)] bool arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern nint IntPtr_objc_msgSend_NSRect_nuint_nuint_bool(nint receiver, nint selector, NSRect rect, nuint arg1, nuint arg2, [MarshalAs(UnmanagedType.I1)] bool arg3);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void void_objc_msgSend(nint receiver, nint selector);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void void_objc_msgSend_nint(nint receiver, nint selector, nint arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void void_objc_msgSend_nuint(nint receiver, nint selector, nuint arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void void_objc_msgSend_bool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern nint nint_objc_msgSend(nint receiver, nint selector);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ObjcAction(nint self, nint cmd, nint sender);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool ObjcShouldTerminate(nint self, nint cmd, nint sender);
    }
}
