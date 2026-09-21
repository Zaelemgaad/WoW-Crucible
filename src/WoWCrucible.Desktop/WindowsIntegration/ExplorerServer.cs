using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.WindowsIntegration;

internal sealed record ExplorerRequest(ExplorerAction Action, string[] Paths, bool NoShowUi);

[SupportedOSPlatform("windows")]
internal sealed class ExplorerServer
{
    private readonly Queue<ExplorerRequest> _requests = new();
    private readonly List<object> _factories = [];
    private readonly List<uint> _cookies = [];
    private long _lastActivity = Environment.TickCount64;
    private int _locks;
    private Task? _active;
    private ExplorerRequest? _request;
    private IExplorerProgressDialog? _dialog;
    private CancellationTokenSource? _cancellation;
    private readonly LatestProgress _progress = new();
    private DbcBatchExportResult? _lastResult;

    internal void Touch() => _lastActivity = Environment.TickCount64;
    internal void Lock(bool locked) { _locks = Math.Max(0, _locks + (locked ? 1 : -1)); Touch(); }
    internal void Enqueue(ExplorerRequest request) { _requests.Enqueue(request); Touch(); }

    internal void Run()
    {
        Marshal.ThrowExceptionForHR(ExplorerNative.OleInitialize(0));
        try
        {
            foreach (var verb in ExplorerMenuRegistration.Verbs)
            {
                var factory = new ExplorerCommandFactory(this, verb.Action);
                _factories.Add(factory);
                var id = verb.ClassId;
                // Publish all action factories together; no managed code is loaded into Explorer.
                Marshal.ThrowExceptionForHR(ExplorerNative.CoRegisterClassObject(ref id, factory, 4, 5, out var cookie));
                _cookies.Add(cookie);
            }
            Marshal.ThrowExceptionForHR(ExplorerNative.CoResumeClassObjects());
            while (true)
            {
                while (ExplorerNative.PeekMessage(out var message, 0, 0, 0, 1))
                    ExplorerNative.DispatchMessage(ref message);
                Tick();
                if (_active is null && _requests.Count == 0 && _locks == 0 && Environment.TickCount64 - _lastActivity > 15000)
                {
                    Marshal.ThrowExceptionForHR(ExplorerNative.CoSuspendClassObjects());
                    break;
                }
                ExplorerNative.MsgWaitForMultipleObjectsEx(0, 0, 50, 0x04FF, 4);
            }
        }
        finally
        {
            // A failed native progress window must not terminate a writer halfway through its temporary file.
            _cancellation?.Cancel();
            try
            {
                if (_active is not null)
                {
                    try { _active.GetAwaiter().GetResult(); }
                    catch (Exception exception) { ExplorerReports.Error(exception, false); }
                }
                CloseProgress();
            }
            finally
            {
                foreach (var cookie in _cookies) ExplorerNative.CoRevokeClassObject(cookie);
                GC.KeepAlive(_factories);
                ExplorerNative.OleUninitialize();
            }
        }
    }

    private void Tick()
    {
        if (_active is null && _requests.TryDequeue(out var next))
        {
            _request = next;
            _cancellation = new();
            _progress.Value = null;
            try
            {
                if (!next.NoShowUi && next.Action != ExplorerAction.Open)
                {
                    _dialog = (IExplorerProgressDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new("F8383852-FCD3-11D1-A6B9-006097DF5BD4"), true)!)!;
                    _dialog.SetTitle($"Crucible - Convert to {next.Action.ToString().ToUpperInvariant()}");
                    _dialog.SetLine(1, $"{next.Paths.Length:N0} files", false, 0);
                    _dialog.SetCancelMsg("Cancelling after the current operation...", 0);
                    _dialog.StartProgressDialog(0, 0, 2, 0);
                }
                var token = _cancellation.Token;
                _active = Task.Run(() => RunRequest(next, token));
            }
            catch (Exception exception)
            {
                CloseProgress();
                ExplorerReports.Error(exception, !next.NoShowUi);
                _request = null;
                Touch();
            }
        }
        if (_active is null) return;
        if (_dialog is not null)
        {
            if (_dialog.HasUserCancelled()) _cancellation!.Cancel();
            if (Volatile.Read(ref _progress.Value) is { } value)
            {
                var fraction = value.TotalRows == 0 ? 0 : 1000UL * (ulong)value.CompletedRows / (ulong)value.TotalRows;
                _dialog.SetProgress64((ulong)value.CompletedFiles * 1000 + fraction, (ulong)value.TotalFiles * 1000);
                _dialog.SetLine(1, $"{value.CompletedFiles:N0} of {value.TotalFiles:N0} files", false, 0);
                _dialog.SetLine(2, value.SourcePath, true, 0);
            }
        }
        if (!_active.IsCompleted) return;
        var completed = _active;
        var noShowUi = _request!.NoShowUi;
        CloseProgress();
        _active = null;
        _request = null;
        try
        {
            completed.GetAwaiter().GetResult();
            if (_lastResult is { } result && !noShowUi)
                ExplorerNative.MessageBox(0, ExplorerReports.Summary(result), "Crucible - Export complete", result.Failed > 0 ? 0x30u : 0x40u);
        }
        catch (Exception exception) { ExplorerReports.Error(exception, !noShowUi); }
        _lastResult = null;
        Touch();
    }

    private void RunRequest(ExplorerRequest request, CancellationToken token)
    {
        if (request.Action == ExplorerAction.Open)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add("--explorer-open-stdin");
            using var process = Process.Start(start) ?? throw new IOException("Could not open Crucible.");
            // A pipe, not a command line, carries large selections with spaces/Unicode intact.
            JsonSerializer.Serialize(process.StandardInput.BaseStream, request.Paths);
            process.StandardInput.Close();
            return;
        }
        var schemas = new DesktopTableSchemaSource(DesktopSettings.Load());
        var format = request.Action == ExplorerAction.Csv ? DbcRowExportFormat.Csv : DbcRowExportFormat.Json;
        _lastResult = DbcBatchExportService.Export(request.Paths, format, file => schemas.Resolve(file).Schema, _progress, token);
        ExplorerReports.Write(new { CompletedUtc = DateTimeOffset.UtcNow, Format = format, Result = _lastResult });
    }

    private void CloseProgress()
    {
        if (_dialog is not null)
        {
            try { _dialog.StopProgressDialog(); }
            finally { Marshal.ReleaseComObject(_dialog); _dialog = null; }
        }
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private sealed class LatestProgress : IProgress<DbcBatchExportProgress>
    {
        internal DbcBatchExportProgress? Value;
        public void Report(DbcBatchExportProgress value) => Volatile.Write(ref Value, value);
    }
}

[SupportedOSPlatform("windows"), ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ExplorerCommandFactory : IExplorerClassFactory
{
    private readonly ExplorerServer _server;
    private readonly ExplorerAction _action;
    internal ExplorerCommandFactory(ExplorerServer server, ExplorerAction action) { _server = server; _action = action; }

    public int CreateInstance(nint outer, ref Guid iid, out nint instance)
    {
        instance = 0;
        if (outer != 0) return unchecked((int)0x80040110);
        _server.Touch();
        var unknown = Marshal.GetIUnknownForObject(new ExplorerCommand(_server, _action));
        try { return Marshal.QueryInterface(unknown, in iid, out instance); }
        finally { Marshal.Release(unknown); }
    }
    public int LockServer(bool locked) { _server.Lock(locked); return 0; }
}

[SupportedOSPlatform("windows"), ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ExplorerCommand : IExplorerExecuteCommand, IExplorerObjectWithSelection, IExplorerInitializeCommand
{
    private readonly ExplorerServer _server;
    private readonly ExplorerAction _action;
    private IShellItemArray? _selection;
    private bool _noShowUi;
    internal ExplorerCommand(ExplorerServer server, ExplorerAction action) { _server = server; _action = action; }
    public int SetKeyState(uint keyState) { _server.Touch(); return 0; }
    public int SetParameters(string? parameters) { _server.Touch(); return 0; }
    public int SetPosition(ExplorerPoint point) { _server.Touch(); return 0; }
    public int SetShowWindow(int show) { _server.Touch(); return 0; }
    public int SetNoShowUI(bool noShowUi) { _noShowUi = noShowUi; _server.Touch(); return 0; }
    public int SetDirectory(string? directory) { _server.Touch(); return 0; }
    public int Initialize(string name, nint propertyBag) { _server.Touch(); return 0; }
    public int SetSelection(IShellItemArray? selection) { _selection = selection; _server.Touch(); return 0; }
    public int GetSelection(ref Guid iid, out nint selection)
    {
        selection = 0;
        if (_selection is null) return unchecked((int)0x80004005);
        var unknown = Marshal.GetIUnknownForObject(_selection);
        try { return Marshal.QueryInterface(unknown, in iid, out selection); }
        finally { Marshal.Release(unknown); }
    }
    public int Execute()
    {
        try
        {
            if (_selection is null) throw new InvalidOperationException("Explorer did not provide a file selection.");
            _selection.GetCount(out var count);
            if (count == 0) throw new InvalidOperationException("Select at least one DBC or DB2 file.");
            var paths = new string[count];
            for (uint index = 0; index < count; index++)
            {
                _selection.GetItemAt(index, out var item);
                try
                {
                    item.GetDisplayName(0x80058000, out var name); // SIGDN_FILESYSPATH
                    try { paths[index] = Marshal.PtrToStringUni(name) ?? throw new IOException("A selected item has no file-system path."); }
                    finally { Marshal.FreeCoTaskMem(name); }
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            _server.Enqueue(new(_action, paths, _noShowUi));
            _selection = null;
            return 0;
        }
        catch (Exception exception)
        {
            ExplorerReports.Error(exception, !_noShowUi);
            return Marshal.GetHRForException(exception);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class ExplorerReports
{
    internal static string PathName => Path.Combine(CruciblePaths.LogDirectory, "Explorer", "last-export.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    internal static void Write<T>(T report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var temporary = PathName + $".{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, JsonOptions));
            File.Move(temporary, PathName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Summary(DbcBatchExportResult result)
    {
        var summary = $"{result.Exported:N0} exported, {result.Skipped:N0} skipped, {result.Failed:N0} failed, {result.Cancelled:N0} cancelled.\n\nOutputs are beside their source files. Originals were not changed.";
        foreach (var item in result.Items.Where(item => item.Status != DbcBatchExportStatus.Exported).Take(5))
            summary += $"\n\n{item.SourcePath}\n{item.Message}";
        return summary + $"\n\nFull results: {PathName}";
    }

    internal static void Error(Exception exception, bool showUi)
    {
        var message = exception.Message;
        try { Write(new { CompletedUtc = DateTimeOffset.UtcNow, Error = exception.ToString() }); }
        catch (Exception reportError) { message += $"\nCould not save the export report: {reportError.Message}"; }
        if (showUi) ExplorerNative.MessageBox(0, message, "Crucible - Explorer command failed", 0x10);
    }
}
