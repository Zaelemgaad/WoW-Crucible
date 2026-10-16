using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

/// <summary>Loopback transport for the one shared frontend; never writes game files or exposes SQL credentials.</summary>
internal sealed class ClassStudioHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _dataGate = new(1);
    private readonly SemaphoreSlim _stateGate = new(1);
    private readonly Func<WdbcFile, DbcSchemaResolution> _schema;
    private readonly Func<string, Task<string?>> _pick;
    private readonly DesktopWorkspaceSession _session;
    private readonly string _assets;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private ClassStudioDataService? _data;
    private readonly string _statePath;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Url { get; }

    public ClassStudioHost(string assets, Func<WdbcFile, DbcSchemaResolution> schema, Func<string, Task<string?>> pick, DesktopWorkspaceSession session, string? statePath = null)
    {
        _assets = Path.GetFullPath(assets); _schema = schema; _pick = pick; _session = session;
        _statePath = statePath ?? Path.Combine(CruciblePaths.SettingsDirectory, "class-studio.json");
        if (!File.Exists(Path.Combine(_assets, "index.html"))) throw new FileNotFoundException("Class Studio frontend was not installed with this build.");
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var origin = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(origin); _listener.Start(); Url = origin + _token + "/index.html";
        _ = ListenAsync();
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                _ = HandleAsync(context);
            }
        }
        catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request; var path = request.Url!.AbsolutePath;
            if (!request.IsLocal || !path.StartsWith('/' + _token + "/", StringComparison.Ordinal) ||
                (request.Headers["Origin"] is { } origin && origin != request.Url.GetLeftPart(UriPartial.Authority)))
            { response.StatusCode = 403; return; }
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            var relative = Uri.UnescapeDataString(path[(_token.Length + 2)..]);
            if (relative.StartsWith("api/", StringComparison.Ordinal))
            {
                object? result;
                switch (relative)
                {
                    case "api/settings" when request.HttpMethod == "GET":
                        result = new { serverPath = _session.Settings.CoreDbcPath, clientPath = _session.Settings.ClientDataPath, corePath = _session.Settings.CoreSourcePath, sql = _session.DatabaseTested }; break;
                    case "api/pick" when request.HttpMethod == "POST":
                        result = new { path = await _pick(request.QueryString["kind"] ?? "server") }; break;
                    case "api/state" when request.HttpMethod == "GET":
                        await _stateGate.WaitAsync(_stop.Token);
                        try { result = File.Exists(_statePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_statePath, _stop.Token), Json) : null; }
                        finally { _stateGate.Release(); }
                        break;
                    case "api/state" when request.HttpMethod == "POST":
                        var state = await ReadAsync<Dictionary<string, string>>(request);
                        if (state.Any(pair => !pair.Key.StartsWith("twc.", StringComparison.Ordinal))) throw new InvalidDataException("Unknown workspace key.");
                        await _stateGate.WaitAsync(_stop.Token);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                            var temporary = _statePath + ".tmp";
                            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, Json), _stop.Token);
                            File.Move(temporary, _statePath, true);
                        }
                        finally { _stateGate.Release(); }
                        result = new { saved = true }; break;
                    case "api/load" when request.HttpMethod == "POST":
                        var sources = await ReadAsync<SourceRequest>(request);
                        await _dataGate.WaitAsync(_stop.Token);
                        try
                        {
                            var candidate = new ClassStudioDataService(_schema);
                            result = await Task.Run(() => candidate.LoadAsync(sources.ServerPath, sources.ClientPath,
                                Path.Combine(CruciblePaths.CacheDirectory, "ClassStudio"),
                                sources.IncludeSql ? _session.DatabaseProfile : null, sources.IncludeSql ? _session.DatabaseCapabilities : null, _stop.Token, sources.CorePath,
                                Enum.Parse<ClientArchiveOrderPolicy>(sources.ClientOrder ?? "StandardWrath", ignoreCase: true)));
                            _data = candidate;
                        }
                        finally { _dataGate.Release(); }
                        break;
                    case "api/row" when request.HttpMethod == "GET":
                        if (_data is null) throw new InvalidOperationException("Load sources first.");
                        result = _data.ReadRow(request.QueryString["layer"] ?? "", request.QueryString["table"] ?? "", uint.Parse(request.QueryString["id"] ?? "0")); break;
                    default: response.StatusCode = 404; result = new { error = "Unknown Class Studio request." }; break;
                }
                response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(response.OutputStream, result, Json, _stop.Token);
            }
            else
            {
                if (request.HttpMethod != "GET") { response.StatusCode = 405; return; }
                var file = Path.GetFullPath(Path.Combine(_assets, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(_assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) { response.StatusCode = 404; return; }
                response.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
                { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".png" => "image/png", ".jpg" => "image/jpeg", _ => "application/octet-stream" };
                using var input = File.OpenRead(file); await input.CopyToAsync(response.OutputStream, _stop.Token);
            }
        }
        catch (Exception e)
        {
            if (!_stop.IsCancellationRequested)
            {
                DesktopCrashLogger.Log("Class Studio request", e);
                try { response.StatusCode = 400; response.ContentType = "application/json"; await JsonSerializer.SerializeAsync(response.OutputStream, new { error = e.Message }, Json); }
                catch (Exception closed) when (closed is HttpListenerException or ObjectDisposedException or IOException) { }
            }
        }
        finally { response.Close(); }
    }

    private async Task<T> ReadAsync<T>(HttpListenerRequest request)
    {
        const int limit = 32 * 1024 * 1024;
        if (request.ContentLength64 > limit) throw new InvalidDataException("Request exceeds the 32 MiB workspace limit.");
        using var body = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await request.InputStream.ReadAsync(buffer, _stop.Token)) > 0)
        {
            if (body.Length + count > limit) throw new InvalidDataException("Request exceeds the 32 MiB workspace limit.");
            body.Write(buffer, 0, count);
        }
        body.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(body, Json, _stop.Token) ?? throw new InvalidDataException("Request body is missing.");
    }
    private sealed record SourceRequest(string? ServerPath, string? ClientPath, bool IncludeSql, string? CorePath, string? ClientOrder);
    public void Dispose() { _stop.Cancel(); _listener.Close(); }
}
