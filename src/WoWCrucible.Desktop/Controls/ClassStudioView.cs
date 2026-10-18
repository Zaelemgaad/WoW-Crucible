using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

/// <summary>The shared editor rendered within a desktop tab, with direct native messages and local assets.</summary>
internal sealed class ClassStudioView : UserControl, IDisposable
{
    private readonly ClassStudioSession _session;
    private readonly StudioSurface _surface;
    private readonly TextBlock _status = new() { Text = "Opening Class Studio…", Margin = new Thickness(20), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private bool _restored, _displayFailed, _disposed;
    private bool _toolbarsVisible = true;
    internal event EventHandler<KeyEventArgs>? ShellKeyRequested;
    internal Task Ready => _surface.Ready;
    internal CoreWebView2? Browser => _surface.Browser;

    public ClassStudioView(string assets, ClassStudioSession session, string? profile = null)
    {
        _session = session;
        _surface = new StudioSurface(assets, profile ?? Path.Combine(CruciblePaths.CacheDirectory, "ClassStudio", "WebView"));
        _surface.Message += HandleMessage;
        _surface.ShellKeyRequested += (_, e) => ShellKeyRequested?.Invoke(this, e);
        _surface.Failed += (_, error) => ShowFailure(error);
        _surface.LoadedPage += (_, _) => { _status.IsVisible = false; ApplyToolbarVisibility(); };
        Content = new Grid { Children = { _status, _surface } };
    }

    private async void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!StudioSurface.IsEditorPage(e.Source)) return;
        long? id = null;
        try
        {
            var json = e.WebMessageAsJson;
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement;
            id = message.GetProperty("id").GetInt64();
            var route = message.GetProperty("route").GetString() ?? "";
            var body = message.TryGetProperty("body", out var value) ? (JsonElement?)value : null;
            var result = await _session.InvokeAsync(route, body);
            if (route == "state" && body is null) _restored = true;
            var reply = await Task.Run(() => JsonSerializer.Serialize(new { id, result }, ClassStudioSession.Json));
            Browser?.PostWebMessageAsJson(reply);
        }
        catch (Exception error)
        {
            if (_disposed) return;
            DesktopCrashLogger.Log("Class Studio operation", error);
            if (_displayFailed) return;
            try { Browser?.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, error = error.Message }, ClassStudioSession.Json)); }
            catch (Exception deliveryError) { DesktopCrashLogger.Log("Class Studio response", deliveryError); ShowFailure(deliveryError); }
        }
    }

    // Capture the current storage before closing, including edits made inside the debounce interval.
    public async Task FlushAsync()
    {
        if (!_restored || _displayFailed || Browser is null) return;
        var json = await Browser.ExecuteScriptAsync("window.StudioBridge.prepareClose()");
        var state = JsonSerializer.Deserialize<string>(json);
        if (state is null) return;
        using var document = JsonDocument.Parse(state);
        await _session.InvokeAsync("state", document.RootElement);
    }

    public async Task ImportFileAsync(string path)
    {
        await Ready;
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (await Browser!.ExecuteScriptAsync("Boolean(window.TalentStudio && window.TalentStudio.initialized && window.StudioSources && window.StudioSources.initialized)") != "true")
        {
            if (_disposed || _displayFailed) throw new InvalidOperationException("The editor closed before the file could be opened.");
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Class Studio did not finish restoring its saved workspace. The file was not imported.");
            await Task.Delay(25);
        }
        var json = await File.ReadAllTextAsync(path);
        using var parsed = JsonDocument.Parse(json);
        var result = await Browser!.ExecuteScriptAsync("(() => { try { window.TalentStudio.importData(JSON.parse(" + JsonSerializer.Serialize(parsed.RootElement.GetRawText()) + ")); return null; } catch (e) { return String(e.message); } })()");
        var error = JsonSerializer.Deserialize<string>(result);
        if (error is not null) throw new InvalidDataException(error);
    }

    private void ShowFailure(Exception error)
    {
        _displayFailed = true; _surface.IsVisible = false;
        _status.Text = "Class Studio is unavailable: " + error.Message + " Saved work remains in Config/class-studio.json.";
        _status.IsVisible = true;
    }

    public void SetOccluded(bool occluded) => _surface.IsVisible = !occluded && !_displayFailed;

    public void SetToolbarsVisible(bool visible)
    {
        if (_toolbarsVisible == visible) return;
        _toolbarsVisible = visible;
        ApplyToolbarVisibility();
    }

    private async void ApplyToolbarVisibility()
    {
        if (Browser is null || !Ready.IsCompletedSuccessfully || _disposed || _displayFailed) return;
        try { await Browser.ExecuteScriptAsync("window.TalentStudio.setToolbarsVisible(" + (_toolbarsVisible ? "true" : "false") + ");"); }
        catch (Exception error) { if (!_disposed) ShowFailure(error); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _surface.Dispose(); _session.Dispose();
    }

    private sealed class StudioSurface : NativeControlHost, IDisposable
    {
        private const string Host = "class-studio.crucible.invalid";
        private const string Page = "https://" + Host + "/index.html";
        private readonly string _assets, _profile;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IPlatformHandle? _handle;
        private CoreWebView2Controller? _controller;
        private bool _disposed;
        private ulong _documentNavigation;
        internal Task Ready => _ready.Task;
        internal CoreWebView2? Browser => _controller?.CoreWebView2;
        internal event EventHandler<CoreWebView2WebMessageReceivedEventArgs>? Message;
        internal event EventHandler<Exception>? Failed;
        internal event EventHandler? LoadedPage;
        internal event EventHandler<KeyEventArgs>? ShellKeyRequested;

        internal StudioSurface(string assets, string profile)
        {
            _assets = Path.GetFullPath(assets); _profile = profile;
            if (!File.Exists(Path.Combine(_assets, "index.html"))) throw new FileNotFoundException("Class Studio files are missing from this build.");
            LayoutUpdated += (_, _) => UpdateBounds();
        }

        internal static bool IsEditorPage(string uri) => uri == Page;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The embedded Class Studio currently requires Windows WebView2.");
            if (_handle is null)
            {
                _handle = base.CreateNativeControlCore(parent);
                Dispatcher.UIThread.Post(InitializeAsync);
            }
            return _handle;
        }

        private async void InitializeAsync()
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
                if (_disposed) return;
                var controller = await environment.CreateCoreWebView2ControllerAsync(_handle!.Handle);
                if (_disposed) { controller.Close(); return; }
                _controller = controller;
                var browser = controller.CoreWebView2;
                browser.Settings.AreDevToolsEnabled = DesktopCrashLogger.IsDevbugEnabled;
                browser.Settings.AreDefaultContextMenusEnabled = false;
                browser.Settings.AreBrowserAcceleratorKeysEnabled = false;
                browser.Settings.IsStatusBarEnabled = false;
                browser.Settings.AreHostObjectsAllowed = false;
                browser.Settings.IsWebMessageEnabled = true;
                browser.SetVirtualHostNameToFolderMapping(Host, _assets, CoreWebView2HostResourceAccessKind.Deny);
                browser.NavigationStarting += (_, e) => { if (!IsEditorPage(e.Uri)) e.Cancel = true; else _documentNavigation = e.NavigationId; };
                browser.FrameNavigationStarting += (_, e) => e.Cancel = true;
                browser.NewWindowRequested += (_, e) => e.Handled = true;
                browser.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                browser.WebMessageReceived += (_, e) => Message?.Invoke(this, e);
                browser.NavigationCompleted += (_, e) =>
                {
                    if (e.NavigationId != _documentNavigation) return;
                    if (!e.IsSuccess) { Fail(new IOException("Editor assets could not load: " + e.WebErrorStatus)); return; }
                    _ready.TrySetResult(); LoadedPage?.Invoke(this, EventArgs.Empty);
                };
                browser.ProcessFailed += (_, e) => Fail(new IOException("The editor display stopped: " + e.ProcessFailedKind + ". Reopen Crucible to restore saved work."));
                controller.AcceleratorKeyPressed += (_, e) =>
                {
                    if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
                    var modifiers = (GetKeyState(0x11) < 0 ? KeyModifiers.Control : KeyModifiers.None)
                        | (GetKeyState(0x10) < 0 ? KeyModifiers.Shift : KeyModifiers.None)
                        | (GetKeyState(0x12) < 0 ? KeyModifiers.Alt : KeyModifiers.None);
                    var key = e.VirtualKey switch
                    {
                        0x75 => Key.F6,
                        0x4B when modifiers.HasFlag(KeyModifiers.Control) => Key.K,
                        0x4F when modifiers.HasFlag(KeyModifiers.Control) => Key.O,
                        0x57 when modifiers.HasFlag(KeyModifiers.Control) => Key.W,
                        0x09 when modifiers.HasFlag(KeyModifiers.Control) => Key.Tab,
                        _ => Key.None
                    };
                    if (key == Key.None) return;
                    e.Handled = true;
                    Dispatcher.UIThread.Post(() => ShellKeyRequested?.Invoke(this, new KeyEventArgs { Key = key, KeyModifiers = modifiers }));
                };
                controller.IsVisible = true;
                UpdateBounds();
                browser.Navigate(Page);
            }
            catch (Exception error) { if (!_disposed) Fail(error); }
        }

        private void Fail(Exception error)
        {
            DesktopCrashLogger.Log("Class Studio display", error);
            _ready.TrySetException(error); Failed?.Invoke(this, error);
        }

        private void UpdateBounds()
        {
            if (_controller is null || _handle is null || _disposed) return;
            TryUpdateNativeControlPosition();
            if (GetClientRect(_handle.Handle, out var rectangle))
            {
                var bounds = new System.Drawing.Rectangle(0, 0, rectangle.Right, rectangle.Bottom);
                if (_controller.Bounds != bounds) _controller.Bounds = bounds;
            }
            _controller.NotifyParentWindowPositionChanged();
        }

        // Avalonia parks detached HWNDs. Keep this one until application shutdown so changing
        // workspace tabs retains loaded data, unsaved input, history and the native page instance.
        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            // Explicitly owned by Dispose, including when Avalonia detaches during shutdown.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _controller?.Close(); _controller = null;
            if (_handle is not null) { base.DestroyNativeControlCore(_handle); _handle = null; }
            _ready.TrySetCanceled();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int key);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
    }
}
