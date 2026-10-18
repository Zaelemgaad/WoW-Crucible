using System.Text;
using System.Text.Json;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

/// <summary>Class Studio operations shared by its embedded view and desktop tests. No network transport.</summary>
internal sealed class ClassStudioSession : IDisposable
{
    internal const int MessageLimit = 32 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _dataGate = new(1);
    private readonly SemaphoreSlim _stateGate = new(1);
    private readonly Func<WdbcFile, DbcSchemaResolution> _schema;
    private readonly Func<string, Task<string?>> _pick;
    private readonly Func<string, string, Task<bool>> _export;
    private readonly DesktopWorkspaceSession _session;
    private readonly string _statePath;
    private ClassStudioDataService? _data;
    private ServerRealmCatalog? _realms;

    public ClassStudioSession(Func<WdbcFile, DbcSchemaResolution> schema, Func<string, Task<string?>> pick,
        Func<string, string, Task<bool>> export, DesktopWorkspaceSession session, string? statePath = null)
    {
        _schema = schema; _pick = pick; _export = export; _session = session;
        _statePath = statePath ?? Path.Combine(CruciblePaths.SettingsDirectory, "class-studio.json");
    }

    public async Task<object?> InvokeAsync(string route, JsonElement? body = null)
    {
        // Workspace requests retain their existing bound. Full-table CSV exports can be much larger.
        if (route != "export" && body is not null && Encoding.UTF8.GetByteCount(body.Value.GetRawText()) > MessageLimit)
            throw new InvalidDataException("Request exceeds the 32 MiB workspace limit.");
        var parts = route.Split('?', 2);
        var query = System.Web.HttpUtility.ParseQueryString(parts.Length > 1 ? parts[1] : "");
        object? result;
        switch (parts[0])
        {
            case "settings" when body is null:
                _realms = await _session.GetRealmsAsync(_stop.Token);
                result = new { serverPath = _session.Settings.CoreDbcPath, clientPath = _session.Settings.ClientDataPath, corePath = _session.Settings.CoreSourcePath, sql = _session.DatabaseTested,
                    realms = _realms.Realms.Select(r => new { r.Id, r.Name, r.Profile, serverPath = r.Server.DbcPath, database = r.Server.WorldDatabase.Database }),
                    notices = _realms.Notices }; break;
            case "realm" when body is not null:
                await _session.SelectRealmAsync(body.Value.GetProperty("id").GetUInt32(), _stop.Token);
                result = new { selected = _session.Settings.ActiveRealmId }; break;
            case "pick" when body is not null:
                result = new { path = await _pick(query["kind"] ?? "server") }; break;
            case "state" when body is null:
                await _stateGate.WaitAsync(_stop.Token);
                try { result = File.Exists(_statePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_statePath, _stop.Token), Json) : null; }
                finally { _stateGate.Release(); }
                break;
            case "state" when body is not null:
                var state = body.Value.Deserialize<Dictionary<string, string>>(Json) ?? throw new InvalidDataException("Missing state.");
                if (state.Any(pair => !pair.Key.StartsWith("twc.", StringComparison.Ordinal) || pair.Value is null)) throw new InvalidDataException("Unknown workspace key.");
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
            case "load" when body is not null:
                var sources = body.Value.Deserialize<SourceRequest>(Json) ?? throw new InvalidDataException("Missing sources.");
                await _dataGate.WaitAsync(_stop.Token);
                try
                {
                    ServerRealmSource? realm = null;
                    if (sources.RealmId is not null)
                    {
                        _realms ??= await _session.GetRealmsAsync(_stop.Token);
                        realm = _realms.Realms.SingleOrDefault(r => r.Id == sources.RealmId)
                            ?? throw new InvalidDataException("The selected realm is no longer configured. Reopen Class Studio to refresh realms.");
                    }
                    var database = sources.IncludeSql ? realm?.Server.WorldDatabase ?? _session.DatabaseProfile : null;
                    var capabilities = sources.IncludeSql ? _session.DatabaseCapabilities : null;
                    var notices = new List<string>();
                    if (database is not null && realm is not null)
                    {
                        try { capabilities = await new DatabaseCapabilityService().InspectAsync(database, _stop.Token); }
                        catch (MySqlConnector.MySqlException ex) { database = null; capabilities = null; notices.Add("Selected realm SQL could not be read: " + ex.Message); }
                    }
                    var candidate = new ClassStudioDataService(_schema);
                    var loaded = await Task.Run(() => candidate.LoadAsync(realm?.Server.DbcPath ?? sources.ServerPath, sources.ClientPath,
                        Path.Combine(CruciblePaths.CacheDirectory, "ClassStudio"), database, capabilities, _stop.Token, sources.CorePath,
                        Enum.Parse<ClientArchiveOrderPolicy>(sources.ClientOrder ?? "StandardWrath", ignoreCase: true)));
                    result = loaded with { Notices = loaded.Notices.Concat(notices).ToArray(), RealmProfile = realm?.Profile,
                        SourceIdentity = realm?.Server.ConfigLocation, RealmName = realm?.Name };
                    _data = candidate;
                }
                finally { _dataGate.Release(); }
                break;
            case "icons" when body is not null:
                await _dataGate.WaitAsync(_stop.Token);
                try
                {
                    if (_data is null) throw new InvalidOperationException("Load sources first.");
                    var names=body.Value.Deserialize<string[]>(Json) ?? [];
                    result=await Task.Run(()=>_data.ReadIcons(names,_stop.Token));
                }
                finally { _dataGate.Release(); }
                break;
            case "tooltip" when body is not null:
                var tooltip=body.Value;
                var description=tooltip.GetProperty("text").GetString() ?? "";
                if(description.Length>32768)throw new InvalidDataException("Description exceeds the preview size limit.");
                result=_data?.RenderTooltip(tooltip.GetProperty("layer").GetString() ?? "server",tooltip.GetProperty("spellId").GetUInt32(),description)
                    ?? SpellDescriptionRenderer.WithoutData.Render(description);
                break;
            case "row" when body is null:
                if (_data is null) throw new InvalidOperationException("Load sources first.");
                result = _data.ReadRow(query["layer"] ?? "", query["table"] ?? "", uint.Parse(query["id"] ?? "0")); break;
            case "export" when body is not null:
                var export = body.Value.Deserialize<ExportRequest>(Json) ?? throw new InvalidDataException("Missing export.");
                if (string.IsNullOrWhiteSpace(export.Name) || export.Name != Path.GetFileName(export.Name) || export.Text is null)
                    throw new InvalidDataException("Invalid export filename or content.");
                result = new { saved = await _export(export.Name, export.Text) }; break;
            default: throw new InvalidDataException("Unknown Class Studio request.");
        }
        return result;
    }

    private sealed record SourceRequest(string? ServerPath, string? ClientPath, bool IncludeSql, string? CorePath, string? ClientOrder, uint? RealmId);
    private sealed record ExportRequest(string Name, string Text);
    public void Dispose() => _stop.Cancel();
}
