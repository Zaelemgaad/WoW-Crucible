using System.Diagnostics;
using WoWCrucible.Core;

var devbugRequested = args.Any(argument => argument.Equals("--devbug", StringComparison.OrdinalIgnoreCase));
var commandArguments = args.Where(argument => !argument.Equals("--devbug", StringComparison.OrdinalIgnoreCase)).ToArray();
using var devbug = CliDevbugSession.TryStart(devbugRequested, args);
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
var exitCode = 0;

try
{
    exitCode = commandArguments.Length == 0 || commandArguments[0] is "help" or "--help" or "-h" ? Help() : commandArguments[0].ToLowerInvariant() switch
    {
        "dbc" => Dbc(commandArguments[1..]),
        "db" => Database(commandArguments[1..], cancellation.Token).GetAwaiter().GetResult(),
        "server" => Server(commandArguments[1..]).GetAwaiter().GetResult(),
        "client" => Client(commandArguments[1..]),
        "asset" => Asset(commandArguments[1..], cancellation.Token),
        "project" => Project(commandArguments[1..], cancellation.Token).GetAwaiter().GetResult(),
        "tools" => Tooling(commandArguments[1..]),
        "knowledge" => Knowledge(commandArguments[1..]),
        "cache" => Cache(commandArguments[1..], cancellation.Token).GetAwaiter().GetResult(),
        "mpq" => Mpq(commandArguments[1..]),
        "casc" => Casc(commandArguments[1..], cancellation.Token),
        "manifest" => Manifest(commandArguments[1..]),
        _ => Fail($"Unknown command: {commandArguments[0]}")
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    exitCode = 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    devbug?.RecordException(ex);
    exitCode = 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

devbug?.Complete(exitCode);
return exitCode;

static async Task<int> Cache(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return CacheHelp();
    var operation = args[0].ToLowerInvariant(); var options = args[1..];
    if (operation == "server-plan") return await CacheServerPlan(options, cancellationToken);
    if (operation == "server-apply") return await CacheServerApply(options, cancellationToken);
    if (operation == "server-rollback") return await CacheServerRollback(options, cancellationToken);
    var operands = options.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray();
    var definitionPaths = options.Where(option => option.StartsWith("--definitions=", StringComparison.OrdinalIgnoreCase)).Select(option => option[(option.IndexOf('=') + 1)..]).ToArray();
    var definitionName = Option(options, "--definition="); var json = options.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
    var allowed = new[] { "--definitions=", "--definition=", "--format=", "--limit=", "--search=", "--overwrite" };
    var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !allowed.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray();
    if (unknown.Length > 0) return Fail($"Unknown cache option: {unknown[0]}");
    if (operands.Length < 1) return Fail($"cache {operation} requires a WDB or ADB file path.");
    var cachePath = Path.GetFullPath(operands[0]);
    if (Path.GetExtension(cachePath).Equals(".adb", StringComparison.OrdinalIgnoreCase)) return CacheAdb(operation, options, operands, definitionPaths, definitionName, json, cachePath);
    WowCacheTableDefinition? definition = null;
    if (definitionPaths.Length > 0)
    {
        var catalog = WowCacheDefinitionCatalog.Load(definitionPaths); definition = catalog.Resolve(cachePath, WowCacheDefinitionKind.Wdb, definitionName);
        if (definition is null) return Fail($"No WDB definition named '{definitionName ?? Path.GetFileNameWithoutExtension(cachePath)}' exists in the selected definition file(s).");
    }
    else
    {
        var discoveredPaths = WowCacheDefinitionCatalog.Discover(cachePath);
        var discovered = discoveredPaths.FirstOrDefault(path => Path.GetFileName(path).Equals("WDB.xml", StringComparison.OrdinalIgnoreCase))
                         ?? discoveredPaths.FirstOrDefault(path => path.Contains("Adb_Wdb_Parser 1.0.0", StringComparison.OrdinalIgnoreCase) && !path.Contains("4.3", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).Equals("wdb-definitions.xml", StringComparison.OrdinalIgnoreCase));
        if (discovered is not null) definition = WowCacheDefinitionCatalog.Load(discovered).Resolve(cachePath, WowCacheDefinitionKind.Wdb, definitionName);
    }
    var table = WowCacheTableService.LoadWdb(cachePath, definition);
    if (operation == "info")
    {
        if (operands.Length != 1) return Fail("cache info accepts exactly one WDB file path.");
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { table.SourcePath, table.Sha256, table.Header, Definition = table.Definition?.Name, DefinitionSource = table.Definition?.SourcePath, Records = table.Records.Count, Decoded = table.Records.Count(record => record.Decoded), DecodeFailures = table.Records.Count(record => record.DecodeError is not null), UnconsumedRecords = table.Records.Count(record => record.UnconsumedBytes != 0), table.HasTerminator, table.TrailingBytes }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"Path\t{table.SourcePath}\nSHA256\t{table.Sha256}\nMagic\t{table.Header.Magic} ({table.Header.RawMagic} on disk)\nBuild\t{table.Header.Build:N0}\nLocale\t{(table.Header.Locale.Length == 0 ? "-" : table.Header.Locale)}\nHeaderSize\t{table.Header.HeaderSize:N0}\nRecordVersion\t{table.Header.RecordVersion}\nCacheVersion\t{table.Header.CacheVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}\nMaximumRecordSize\t{table.Header.MaximumRecordSize:N0}\nDefinition\t{table.Definition?.Name ?? "RAW (no matching schema)"}\nDefinitionSource\t{table.Definition?.SourcePath ?? "-"}\nRecords\t{table.Records.Count:N0}\nDecoded\t{table.Records.Count(record => record.Decoded):N0}\nDecodeFailures\t{table.Records.Count(record => record.DecodeError is not null):N0}\nUnconsumedRecords\t{table.Records.Count(record => record.UnconsumedBytes != 0):N0}\nTerminator\t{table.HasTerminator}\nTrailingBytes\t{table.TrailingBytes:N0}");
        return table.Records.Any(record => record.DecodeError is not null) ? 3 : 0;
    }
    if (operation == "rows")
    {
        if (operands.Length != 1) return Fail("cache rows accepts exactly one WDB file path.");
        var limitText = Option(options, "--limit="); var limit = limitText is null ? 100 : int.TryParse(limitText, out var parsed) && parsed is > 0 and <= 100_000 ? parsed : throw new FormatException("--limit must be 1–100000.");
        var search = Option(options, "--search=")?.Trim();
        var rows = table.Records.Where(record => string.IsNullOrEmpty(search) || record.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) || record.Values.Any(value => value.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || value.DisplayValue.Contains(search, StringComparison.OrdinalIgnoreCase))).Take(limit).ToArray();
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rows.Select(record => new { record.Id, record.PayloadSize, record.FileOffset, Values = record.Values.ToDictionary(value => value.Name, value => value.Value), record.UnconsumedBytes, record.DecodeError }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else foreach (var record in rows) Console.WriteLine($"{record.Id}\t{record.PayloadSize}\t{(record.DecodeError is null ? string.Join(" · ", record.Values.Take(8).Select(value => $"{value.Name}={value.DisplayValue}")) : "DECODE ERROR: " + record.DecodeError)}\tremaining={record.UnconsumedBytes}");
        return rows.Length > 0 ? 0 : 3;
    }
    if (operation == "export")
    {
        if (operands.Length != 2) return Fail("cache export requires <file.wdb> <output.csv|jsonl>.");
        var format = Option(options, "--format=") ?? Path.GetExtension(operands[1]).TrimStart('.'); if (format.Equals("json", StringComparison.OrdinalIgnoreCase)) return Fail("Cache row export supports CSV or streaming JSON Lines (jsonl), not a monolithic JSON array.");
        WowCacheTableService.Export(table, operands[1], format, options.Contains("--overwrite", StringComparer.OrdinalIgnoreCase));
        Console.Error.WriteLine($"Exported {table.Records.Count:N0} cache record(s) to {Path.GetFullPath(operands[1])}"); return 0;
    }
    return Fail($"Unknown cache operation: {operation}");
}

static async Task<int> CacheServerPlan(string[] args, CancellationToken cancellationToken)
{
    var operands = args.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray();
    if (operands.Length != 5) return Fail("cache server-plan requires <file.wdb> <host> <port> <user> <database>.");
    if (!Path.GetExtension(operands[0]).Equals(".wdb", StringComparison.OrdinalIgnoreCase)) return Fail("cache server-plan currently requires a decoded WDB cache. WCH2 ADB rows remain inspect/export-only until their server semantics are proven.");
    if (!uint.TryParse(operands[2], out var port) || port is 0 or > 65535) return Fail("Database port must be from 1 to 65535.");
    var allowed = new[] { "--definitions=", "--definition=", "--ids=", "--output=", "--sql=", "--password-env=", "--ssl=", "--format=", "--overwrite" };
    var unknown = args.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !allowed.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray();
    if (unknown.Length > 0) return Fail($"Unknown cache server-plan option: {unknown[0]}");
    var passwordEnvironment = Option(args, "--password-env=") ?? "WOW_CRUCIBLE_DB_PASSWORD";
    var password = Environment.GetEnvironmentVariable(passwordEnvironment); if (password is null) return Fail($"Set the {passwordEnvironment} environment variable for this process. Passwords are not accepted on the command line.");
    var sslText = Option(args, "--ssl=") ?? "Preferred"; if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(sslText, true, out var ssl)) return Fail($"Unknown SSL mode: {sslText}");
    var cachePath = Path.GetFullPath(operands[0]); var definitionName = Option(args, "--definition=");
    var definitionPaths = args.Where(option => option.StartsWith("--definitions=", StringComparison.OrdinalIgnoreCase)).Select(option => option[(option.IndexOf('=') + 1)..]).ToArray();
    WowCacheTableDefinition? definition = null;
    if (definitionPaths.Length > 0) definition = WowCacheDefinitionCatalog.Load(definitionPaths).Resolve(cachePath, WowCacheDefinitionKind.Wdb, definitionName);
    else
    {
        var discovered = WowCacheDefinitionCatalog.Discover(cachePath).FirstOrDefault(path => Path.GetFileName(path).Equals("WDB.xml", StringComparison.OrdinalIgnoreCase));
        if (discovered is not null) definition = WowCacheDefinitionCatalog.Load(discovered).Resolve(cachePath, WowCacheDefinitionKind.Wdb, definitionName);
    }
    if (definition is null) return Fail($"No WDB definition named '{definitionName ?? Path.GetFileNameWithoutExtension(cachePath)}' was resolved. Supply --definitions=WDB.xml explicitly.");
    IReadOnlyCollection<uint>? ids = null; var idsText = Option(args, "--ids=");
    if (!string.IsNullOrWhiteSpace(idsText)) { ids = ItemIdQueryParser.Parse(idsText); if (ids.Count == 0) return Fail("--ids must contain one or more unsigned record IDs."); }
    var table = WowCacheTableService.LoadWdb(cachePath, definition);
    var profile = new DatabaseConnectionProfile(operands[1], port, operands[3], password, operands[4], ssl);
    var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
    var service = new CacheServerExecutionService(); var plan = await service.BuildAsync(table, profile, capabilities, ids, cancellationToken); var overwrite = args.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
    var output = Option(args, "--output="); if (output is not null) await service.SavePlanAsync(plan, output, overwrite, cancellationToken);
    var sqlPath = Option(args, "--sql="); if (sqlPath is not null)
    {
        sqlPath = Path.GetFullPath(sqlPath); if (File.Exists(sqlPath) && !overwrite) return Fail($"SQL preview already exists: {sqlPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(sqlPath)!); var temporary = sqlPath + $".{Guid.NewGuid():N}.tmp";
        try { await File.WriteAllTextAsync(temporary, plan.PreviewSql(), new System.Text.UTF8Encoding(false), cancellationToken); File.Move(temporary, sqlPath, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    if (args.Contains("--format=json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    else
    {
        Console.WriteLine(plan.PreviewSql());
        Console.Error.WriteLine($"Live cache server plan: {plan.Ready:N0} ready · {plan.AlreadyApplied:N0} already equal · {plan.Missing:N0} missing target · {plan.Blocked:N0} blocked · {plan.Records.Sum(record => record.Fields.Count):N0} changed field(s). No SQL was executed.");
        if (output is not null) Console.Error.WriteLine($"Plan: {Path.GetFullPath(output)}"); if (sqlPath is not null) Console.Error.WriteLine($"SQL preview: {sqlPath}");
    }
    return plan.Missing != 0 || plan.Blocked != 0 ? 3 : 0;
}

static async Task<int> CacheServerApply(string[] args, CancellationToken cancellationToken)
{
    var operands = args.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray(); if (operands.Length != 6) return Fail("cache server-apply requires <plan.json> <host> <port> <user> <database> <receipt.json>.");
    var allowed = new[] { "--password-env=", "--ssl=", "--apply", "--overwrite" }; var unknown = args.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !allowed.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray(); if (unknown.Length > 0) return Fail($"Unknown cache server-apply option: {unknown[0]}");
    var service = new CacheServerExecutionService(); var plan = await service.LoadPlanAsync(operands[0], cancellationToken); Console.WriteLine(plan.PreviewSql());
    if (!args.Contains("--apply", StringComparer.OrdinalIgnoreCase)) { Console.Error.WriteLine($"Dry-run only: {plan.Ready:N0} ready · {plan.AlreadyApplied:N0} already equal · {plan.Missing:N0} missing · {plan.Blocked:N0} blocked. Re-run with --apply after reviewing the exact preimages."); return plan.Missing != 0 || plan.Blocked != 0 ? 3 : 0; }
    var profile = CacheProfile(operands[1], operands[2], operands[3], operands[4], args); var result = await service.ApplyAsync(operands[0], profile, operands[5], args.Contains("--overwrite", StringComparer.OrdinalIgnoreCase), cancellationToken);
    Console.Error.WriteLine($"Applied {result.UpdatedFields:N0} cache-derived field(s) across {result.UpdatedRecords:N0} existing row(s); {result.AlreadyAppliedRecords:N0} row(s) were already equal. Receipt: {result.ReceiptPath}"); return 0;
}

static async Task<int> CacheServerRollback(string[] args, CancellationToken cancellationToken)
{
    var operands = args.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray(); if (operands.Length != 5) return Fail("cache server-rollback requires <receipt.json> <host> <port> <user> <database>.");
    var allowed = new[] { "--password-env=", "--ssl=", "--apply" }; var unknown = args.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !allowed.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray(); if (unknown.Length > 0) return Fail($"Unknown cache server-rollback option: {unknown[0]}");
    var service = new CacheServerExecutionService(); var receipt = await service.LoadReceiptAsync(operands[0], cancellationToken);
    Console.WriteLine($"Receipt\t{Path.GetFullPath(operands[0])}\nTarget\t{receipt.Target.User}@{receipt.Target.Host}:{receipt.Target.Port}/{receipt.Target.Database}\nAppliedUtc\t{receipt.AppliedUtc:O}\nRecords\t{receipt.AppliedRecords.Count:N0}\nFields\t{receipt.AppliedRecords.Sum(record => record.Fields.Count):N0}");
    if (!args.Contains("--apply", StringComparer.OrdinalIgnoreCase)) { Console.Error.WriteLine("Dry-run only. Rollback will require every applied field to still equal its receipt after-value. Re-run with --apply to restore exact preimages transactionally."); return 0; }
    var profile = CacheProfile(operands[1], operands[2], operands[3], operands[4], args); var result = await service.RollbackAsync(operands[0], profile, cancellationToken);
    Console.Error.WriteLine($"Restored {result.RestoredFields:N0} field(s) across {result.RestoredRecords:N0} row(s). Receipt marked rolled back: {result.ReceiptPath}"); return 0;
}

static DatabaseConnectionProfile CacheProfile(string host, string portText, string user, string database, string[] options)
{
    if (!uint.TryParse(portText, out var port) || port is 0 or > 65535) throw new FormatException("Database port must be from 1 to 65535.");
    var environment = Option(options, "--password-env=") ?? "WOW_CRUCIBLE_DB_PASSWORD"; var password = Environment.GetEnvironmentVariable(environment) ?? throw new InvalidOperationException($"Set the {environment} environment variable for this process. Passwords are not accepted on the command line.");
    var sslText = Option(options, "--ssl=") ?? "Preferred"; if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(sslText, true, out var ssl)) throw new FormatException($"Unknown SSL mode: {sslText}");
    return new(host, port, user, password, database, ssl);
}

static int CacheAdb(string operation, string[] options, string[] operands, string[] definitionPaths, string? definitionName, bool json, string cachePath)
{
    WowCacheTableDefinition? definition = null;
    if (definitionPaths.Length > 0)
    {
        var catalog = WowCacheDefinitionCatalog.Load(definitionPaths); definition = catalog.Resolve(cachePath, WowCacheDefinitionKind.Adb, definitionName);
        if (definition is null) return Fail($"No ADB definition named '{definitionName ?? Path.GetFileNameWithoutExtension(cachePath)}' exists in the selected definition file(s).");
    }
    else
    {
        var discovered = WowCacheDefinitionCatalog.Discover(cachePath).FirstOrDefault(path => Path.GetFileName(path).Equals("adb-definitions.xml", StringComparison.OrdinalIgnoreCase) && path.Contains("4.3", StringComparison.OrdinalIgnoreCase));
        if (discovered is not null) definition = WowCacheDefinitionCatalog.Load(discovered).Resolve(cachePath, WowCacheDefinitionKind.Adb, definitionName);
    }
    var table = WowAdbTableService.LoadWch2(cachePath, definition);
    if (operation == "info")
    {
        if (operands.Length != 1) return Fail("cache info accepts exactly one cache file path.");
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { table.SourcePath, table.Sha256, table.Header, Definition = table.Definition?.Name, DefinitionSource = table.Definition?.SourcePath, Records = table.Records.Count, Decoded = table.Records.Count(record => record.Decoded), DecodeFailures = table.Records.Count(record => record.DecodeError is not null), UnconsumedRecords = table.Records.Count(record => record.UnconsumedBytes != 0), table.StringBlockOffset, table.CopyTableOffset, table.TrailingBytes }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"Path\t{table.SourcePath}\nSHA256\t{table.Sha256}\nSignature\t{table.Header.Signature}\nBuild\t{table.Header.Build:N0}\nRecords\t{table.Header.RecordCount:N0}\nFields\t{table.Header.FieldCount:N0}\nRecordSize\t{table.Header.RecordSize:N0}\nStrings\t{table.Header.StringBlockSize:N0}\nTableHash\t0x{table.Header.TableHash:X8}\nIDRange\t{table.Header.MinId:N0}..{table.Header.MaxId:N0}\nIndexBytes\t{table.Header.IndexBytes:N0}\nCopyTableBytes\t{table.Header.CopyTableSize:N0}\nDefinition\t{table.Definition?.Name ?? "RAW (no matching schema)"}\nDefinitionSource\t{table.Definition?.SourcePath ?? "-"}\nDecoded\t{table.Records.Count(record => record.Decoded):N0}\nDecodeFailures\t{table.Records.Count(record => record.DecodeError is not null):N0}\nUnconsumedRecords\t{table.Records.Count(record => record.UnconsumedBytes != 0):N0}\nTrailingBytes\t{table.TrailingBytes:N0}");
        return table.Records.Any(record => record.DecodeError is not null) ? 3 : 0;
    }
    if (operation == "rows")
    {
        if (operands.Length != 1) return Fail("cache rows accepts exactly one cache file path.");
        var limitText = Option(options, "--limit="); var limit = limitText is null ? 100 : int.TryParse(limitText, out var parsed) && parsed is > 0 and <= 100_000 ? parsed : throw new FormatException("--limit must be 1–100000."); var search = Option(options, "--search=")?.Trim();
        var rows = table.Records.Where(record => string.IsNullOrEmpty(search) || record.RowIndex.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) || (record.Id?.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) || record.Values.Any(value => value.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || value.DisplayValue.Contains(search, StringComparison.OrdinalIgnoreCase))).Take(limit).ToArray();
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rows.Select(record => new { record.RowIndex, record.Id, Values = record.Values.ToDictionary(value => value.Name, value => value.Value), record.UnconsumedBytes, record.DecodeError }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else foreach (var record in rows) Console.WriteLine($"{record.RowIndex}\t{record.Id?.ToString() ?? "-"}\t{(record.DecodeError is null ? string.Join(" · ", record.Values.Take(8).Select(value => $"{value.Name}={value.DisplayValue}")) : "DECODE ERROR: " + record.DecodeError)}\tremaining={record.UnconsumedBytes}");
        return rows.Length > 0 ? 0 : 3;
    }
    if (operation == "export")
    {
        if (operands.Length != 2) return Fail("cache export requires <file.adb> <output.csv|jsonl>."); var format = Option(options, "--format=") ?? Path.GetExtension(operands[1]).TrimStart('.'); WowAdbTableService.Export(table, operands[1], format, options.Contains("--overwrite", StringComparer.OrdinalIgnoreCase)); Console.Error.WriteLine($"Exported {table.Records.Count:N0} ADB record(s) to {Path.GetFullPath(operands[1])}"); return 0;
    }
    return Fail($"Unknown cache operation: {operation}");
}

static int Tooling(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return ToolingHelp();
    if (args[0].Equals("commands", StringComparison.OrdinalIgnoreCase))
    {
        var commandOptions = args[1..]; var commandJson = commandOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var commandUnknown = commandOptions.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (commandUnknown.Length > 0) return Fail($"Unknown tools commands option: {commandUnknown[0]}");
        var query = string.Join(' ', commandOptions.Where(option => !option.StartsWith("--", StringComparison.Ordinal))); var matches = CrucibleCommandCatalog.Search(query, 100);
        if (commandJson) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Query = query, TotalCommands = CrucibleCommandCatalog.All.Count, Matches = matches.Select(match => new { match.Command.Id, match.Command.Title, match.Command.Category, match.Command.Description, match.Command.Aliases, match.Command.Shortcut, match.Score }) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else foreach (var match in matches) Console.WriteLine($"{match.Command.Id}\t{match.Command.Category}\t{match.Command.Title}\t{match.Command.Shortcut ?? "-"}\t{match.Command.Description}");
        return matches.Count > 0 ? 0 : 3;
    }
    if (!args[0].Equals("inventory", StringComparison.OrdinalIgnoreCase)) return Fail($"Unknown tools operation: {args[0]}");
    var options = args[1..]; var rootArgument = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var unassignedOnly = options.Any(option => option.Equals("--unassigned-only", StringComparison.OrdinalIgnoreCase)); var includeMissing = !options.Any(option => option.Equals("--no-missing", StringComparison.OrdinalIgnoreCase));
    var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--unassigned-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--no-missing", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown tools inventory option: {unknown[0]}");
    if (options.Count(option => !option.StartsWith("--", StringComparison.Ordinal)) > 1) return Fail("tools inventory accepts at most one workspace-root path.");
    var root = rootArgument is null ? ToolConsolidationInventoryService.FindWorkspaceRoot(CruciblePaths.ApplicationDirectory) : rootArgument; var report = ToolConsolidationInventoryService.Scan(root, includeMissing); var entries = unassignedOnly ? report.Entries.Where(entry => entry.Status == ToolInventoryStatus.Unassigned).ToArray() : report.Entries;
    if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { report.WorkspaceRoot, report.ScannedUtc, report.Tracked, report.Missing, report.Unassigned, Entries = entries }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
    else
    {
        foreach (var entry in entries) Console.WriteLine($"{entry.Status}\t{entry.Scope}\t{entry.RelativePath}\t{entry.Capability}\t{entry.CrucibleDestination}");
        Console.Error.WriteLine($"Tool inventory: {report.Tracked:N0} tracked · {report.Unassigned:N0} NEW UNASSIGNED · {report.Missing:N0} expected root(s) absent.\nWorkspace: {report.WorkspaceRoot}");
    }
    return report.Unassigned == 0 ? 0 : 3;
}

static int Knowledge(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return KnowledgeHelp();
    var operation = args[0]; var options = args[1..];
    var root = Option(options, "--root=") ?? KnowledgeReferenceService.FindWikiRoot(CruciblePaths.ApplicationDirectory)
        ?? throw new DirectoryNotFoundException("Could not discover the local wiki. Supply --root=<wiki-folder>.");
    var service = new KnowledgeReferenceService(); var index = service.Build(root);
    if (operation.Equals("search", StringComparison.OrdinalIgnoreCase))
    {
        var json = options.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var locale = Option(options, "--locale=");
        var limitText = Option(options, "--limit="); var limit = limitText is null ? 100 : int.TryParse(limitText, out var parsed) ? parsed : throw new FormatException("--limit must be an integer.");
        var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.StartsWith("--root=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--locale=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown knowledge search option: {unknown[0]}");
        var query = string.Join(' ', options.Where(option => !option.StartsWith("--", StringComparison.Ordinal))); if (string.IsNullOrWhiteSpace(query)) return Fail("knowledge search requires one or more search terms.");
        var hits = service.Search(query, locale, limit);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Query = query, index.RootPath, Documents = index.Articles.Count, Sections = index.SectionCount, Hits = hits }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            foreach (var hit in hits) Console.WriteLine($"{hit.Score}\t{hit.Locale}\t{hit.Title}\t{hit.Heading}\t{hit.RelativePath}\t{hit.Excerpt.ReplaceLineEndings(" ")}");
            Console.Error.WriteLine($"Knowledge search: {hits.Count:N0} result(s) across {index.Articles.Count:N0} document(s) and {index.SectionCount:N0} section(s). Root: {index.RootPath}");
        }
        return hits.Count > 0 ? 0 : 3;
    }
    if (operation.Equals("show", StringComparison.OrdinalIgnoreCase))
    {
        var operands = options.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray(); if (operands.Length != 1) return Fail("knowledge show requires one exact relative Markdown path.");
        var sectionText = Option(options, "--section="); var section = sectionText is null ? (int?)null : int.TryParse(sectionText, out var parsed) ? parsed : throw new FormatException("--section must be an integer.");
        var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.StartsWith("--root=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--section=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown knowledge show option: {unknown[0]}");
        var article = index.Articles.FirstOrDefault(candidate => candidate.RelativePath.Equals(operands[0].Replace('\\','/'), StringComparison.OrdinalIgnoreCase));
        if (article is null) { Console.Error.WriteLine($"Knowledge article not found: {operands[0]}"); return 3; }
        var sections = section is null ? article.Sections : article.Sections.Where(candidate => candidate.Index == section).ToArray();
        if (sections.Count == 0) { Console.Error.WriteLine($"Section {section} does not exist in {article.RelativePath}."); return 3; }
        Console.WriteLine($"{article.Title}\n{article.Locale} · {article.RelativePath}"); foreach (var value in sections) Console.WriteLine($"\n{new string('#', Math.Clamp(value.Level, 1, 6))} {value.Heading}\n\n{value.PlainText}"); return 0;
    }
    return Fail($"Unknown knowledge operation: {operation}");
}

static int Asset(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return AssetHelp();
    if (args is ["texture-consumers-build", var consumerLibrary, .. var consumerBuildOptions])
    {
        var json = consumerBuildOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = consumerBuildOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset texture-consumers-build option: {unknown[0]}");
        var progress = new WoWCrucible.Cli.SynchronousProgress<TextureConsumerIndexProgress>(value =>
        {
            if (value.CurrentPath == "Complete" || value.CurrentPath.StartsWith("Batch", StringComparison.Ordinal) || value.EligibleAssets % 1000 == 0)
                Console.Error.WriteLine($"Texture consumers\t{value.EligibleAssets:N0} eligible\t{value.UpdatedAssets:N0} updated\t{value.CatalogRows:N0} catalog rows\t{value.CurrentPath}");
        });
        var result = new TextureConsumerIndexService().Build(consumerLibrary, progress, cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else PrintTextureConsumerBuild(result);
        return result.Summary.CoverageComplete ? 0 : 3;
    }
    if (args is ["texture-consumers", var queryLibrary, var textureQueryInput, .. var consumerQueryOptions])
    {
        var json = consumerQueryOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var refresh = consumerQueryOptions.Contains("--refresh", StringComparer.OrdinalIgnoreCase);
        var dbcRoot = Option(consumerQueryOptions, "--dbc="); var schemaPath = Option(consumerQueryOptions, "--schema="); var serverRoot = Option(consumerQueryOptions, "--server=");
        var unknown = consumerQueryOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--refresh", StringComparison.OrdinalIgnoreCase) &&
            !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--schema=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--server=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset texture-consumers option: {unknown[0]}");
        var service = new TextureConsumerIndexService(); TextureConsumerIndexBuildResult? build = null;
        if (refresh || !File.Exists(TextureConsumerIndexService.GetIndexPath(queryLibrary)))
        {
            var progress = new WoWCrucible.Cli.SynchronousProgress<TextureConsumerIndexProgress>(value =>
            {
                if (value.CurrentPath == "Complete" || value.CurrentPath.StartsWith("Batch", StringComparison.Ordinal) || value.EligibleAssets % 1000 == 0)
                    Console.Error.WriteLine($"Texture consumers\t{value.EligibleAssets:N0} eligible\t{value.UpdatedAssets:N0} updated\t{value.CatalogRows:N0} catalog rows\t{value.CurrentPath}");
            });
            build = service.Build(queryLibrary, progress, cancellationToken);
        }
        var query = service.Query(queryLibrary, textureQueryInput, cancellationToken);
        ServerWorkspace? workspace = null;
        if (!string.IsNullOrWhiteSpace(serverRoot)) { workspace = ServerWorkspaceDetector.DetectAsync(serverRoot, cancellationToken).GetAwaiter().GetResult(); dbcRoot ??= workspace.DbcPath; }
        TextureAppearanceQueryResult? appearance = null;
        if (!string.IsNullOrWhiteSpace(dbcRoot))
            appearance = new TextureAppearanceReferenceService().QueryAsync(queryLibrary, dbcRoot, schemaPath, query.TextureClientPath, query.TextureProvenance, workspace?.WorldDatabase, cancellationToken: cancellationToken).GetAwaiter().GetResult();
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Build = build, Query = query, Appearance = appearance }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            if (build is not null) PrintTextureConsumerBuild(build);
            Console.WriteLine($"TEXTURE\t{query.TextureClientPath}\nTEXTURE_SOURCE\t{query.TextureSourcePath ?? "client path only"}\nTEXTURE_PROVENANCE\t{query.TextureProvenance ?? "not selected"}\nCONSUMERS\t{query.Consumers.Count:N0}\nCOVERAGE_COMPLETE\t{query.Summary.CoverageComplete}");
            foreach (var consumer in query.Consumers) Console.WriteLine($"CONSUMER\t{(consumer.SameProvenance ? "SAME_PROVENANCE" : "OTHER_OR_UNSELECTED_PROVENANCE")}\t{consumer.ReferenceKind}\t{consumer.ConsumerProvenance}\t{consumer.ConsumerClientPath}\t{consumer.ConsumerSourcePath}");
            if (!query.Summary.CoverageComplete) Console.Error.WriteLine($"INCOMPLETE COVERAGE: {query.Summary.UnsupportedAssets:N0} unsupported-format, {query.Summary.InvalidAssets:N0} invalid consumer file(s), {query.Summary.MissingAssets:N0} missing catalog file(s), {query.Summary.CatalogIssues:N0} catalog issue(s). Zero matches cannot be treated as proof of no use.");
            if (appearance is not null)
            {
                Console.WriteLine($"APPEARANCE_BINDINGS\t{appearance.Bindings.Count:N0}\nCHARSECTION_RECORDS\t{appearance.CharacterSectionRecords:N0}\nCREATURE_DISPLAY_RECORDS\t{appearance.CreatureDisplayRecords:N0}\nSQL_ROWS\t{appearance.SqlRows:N0}\nAPPEARANCE_COVERAGE_COMPLETE\t{appearance.CoverageComplete}");
                foreach (var binding in appearance.Bindings)
                {
                    Console.WriteLine($"APPEARANCE\t{binding.Kind}\t{binding.Table}\t{binding.RecordId}\t{binding.Field}\tslot={binding.ReplaceableType}\t{binding.ModelClientPath ?? "no-model-binding"}\t{binding.Description}");
                    foreach (var source in binding.ModelSources) Console.WriteLine($"  MODEL\t{(source.SameProvenance ? "SAME_PROVENANCE" : "OTHER_OR_UNSELECTED_PROVENANCE")}\t{source.Provenance}\t{source.ClientPath}\t{source.SourcePath}");
                    foreach (var sql in binding.SqlConsumers) Console.WriteLine($"  SQL\t{sql.Table}\t{string.Join(';', sql.Key.Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)}"))}\t{sql.Field}\t{sql.Description}");
                }
                foreach (var finding in appearance.Findings) Console.Error.WriteLine($"APPEARANCE FINDING: {finding}");
                if (appearance.SqlTruncated) Console.Error.WriteLine("APPEARANCE FINDING: live SQL uses exceeded the 10,000-row safety cap; results are explicitly truncated.");
            }
        }
        var found = query.Consumers.Count > 0 || appearance?.Bindings.Count > 0; var complete = query.Summary.CoverageComplete && (appearance?.CoverageComplete ?? true);
        return found && complete ? 0 : 3;
    }
    if (args is ["m2-material-audit", var materialRoot, .. var materialOptions])
    {
        var json = materialOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var workersText = Option(materialOptions, "--workers=") ?? "0"; var examplesText = Option(materialOptions, "--examples=") ?? "5";
        var unknown = materialOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--examples=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset m2-material-audit option: {unknown[0]}");
        if (!int.TryParse(workersText, out var workers) || workers is < 0 or > M2MaterialAuditService.MaximumWorkers) return Fail($"--workers must be 1 through {M2MaterialAuditService.MaximumWorkers}, or zero for automatic.");
        if (!int.TryParse(examplesText, out var examples) || examples is < 1 or > 100) return Fail("--examples must be 1 through 100.");
        var audit = M2MaterialAuditService.Audit(materialRoot, workers, examples);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(audit, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"ROOT\t{audit.Root}\nWORKERS\t{audit.Workers:N0}\nDISCOVERED_SKINS\t{audit.DiscoveredSkinFiles:N0}\nSCANNED_WOTLK_SKINS\t{audit.ScannedSkinFiles:N0}\nWOTLK_MODELS\t{audit.WotlkModelFiles:N0}\nMISSING_MODELS\t{audit.MissingCompanionModels:N0}\nNON_WOTLK_MODELS\t{audit.NonWotlkModels:N0}\nINVALID_PAIRS\t{audit.InvalidPairs:N0}\nMATERIAL_UNITS\t{audit.MaterialUnits:N0}\nUNSUPPORTED_COMBINER_UNITS\t{audit.UnsupportedCombinerMaterialUnits:N0}\nUNSUPPORTED_EXPLICIT_COMBINER_UNITS\t{audit.UnsupportedExplicitCombinerMaterialUnits:N0}\nDURATION_MS\t{audit.DurationMilliseconds:0.###}");
            foreach (var entry in audit.Entries)
            {
                var shader = entry.Encoding == M2MaterialEncoding.Explicit ? $"0x{entry.ShaderId:X4}/{entry.ShaderId & 0x7FFF}" : entry.ShaderId.ToString();
                Console.WriteLine($"MATERIAL\t{entry.Encoding}\tshader={shader}\tstages={entry.TextureStages}\tunits={entry.MaterialUnits:N0}\tskins={entry.SkinFiles:N0}\tcombiner-supported={entry.CombinerSupported}\tcombiner-exact={entry.CombinerExact}\t{entry.Combiner}");
                foreach (var example in entry.Examples) Console.WriteLine($"  EXAMPLE\t{example}");
            }
            foreach (var finding in audit.Findings) Console.WriteLine($"FINDING\t{finding}");
        }
        return audit.ScannedSkinFiles > 0 && audit.InvalidPairs == 0 ? 0 : 3;
    }
    if (args[0].Equals("gameobject-index-plan", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[1..]; var operands = options.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (operands.Length < 5) return Fail("asset gameobject-index-plan requires <client-index> <GameObjectDisplayInfo.dbc> <schema.xml> <new-workspace> <virtual-model-path>... .");
        var allowed = new[] { "--display-start=", "--template-start=", "--occupied=", "--archive-choice=", "--format=" };
        var unknown = options.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !allowed.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset gameobject-index-plan option: {unknown[0]}");
        if (!uint.TryParse(Option(options, "--display-start=") ?? "100000", out var displayStart) || displayStart == 0) return Fail("--display-start must be a positive unsigned ID.");
        if (!uint.TryParse(Option(options, "--template-start=") ?? "100000", out var templateStart) || templateStart == 0) return Fail("--template-start must be a positive unsigned ID.");
        var occupiedPath = Option(options, "--occupied="); var occupied = occupiedPath is null ? null : CrucibleContentProjectService.ReadOccupiedIds(occupiedPath);
        var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options.Where(value => value.StartsWith("--archive-choice=", StringComparison.OrdinalIgnoreCase)))
        {
            var value = option["--archive-choice=".Length..]; var separator = value.IndexOf('|');
            if (separator <= 0 || separator == value.Length - 1) return Fail("--archive-choice must use virtual-client-path|Data\\archive.MPQ.");
            choices[value[..separator]] = value[(separator + 1)..];
        }
        var result = ClientIndexedAssetSnapshotService.CreateGameObjectPlan(operands[0], operands[3], operands[4..], operands[1], operands[2], displayStart, templateStart,
            occupiedTemplateIds: occupied, archiveOverrides: choices);
        if (options.Contains("--format=json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"Workspace\t{Path.GetFullPath(operands[3])}\nSnapshot\t{result.SnapshotPath}\nIndexFingerprint\t{result.Snapshot.IndexFingerprint}\nReady\t{result.Plan.Ready}\nRoots\t{result.Snapshot.RootClientPaths.Count:N0}\nResolvedAssets\t{result.Snapshot.Files.Count(file => file.SourceRelativePath is not null):N0}\nExternalBindings\t{result.Snapshot.Files.Count(file => file.State == ClientIndexedAssetSnapshotState.ExternalBinding):N0}\nPlan\t{result.PlanPath}\nModels\t{result.Plan.Rows.Count:N0}\nPatchAssets\t{result.Plan.Assets.Count:N0}");
            foreach (var row in result.Plan.Rows) Console.WriteLine($"ROW\t{row.TemplateId}\tdisplay={row.DisplayId}\t{(row.ReusesDisplay ? "REUSE" : "ADD")}\t{row.ClientPath}\t{row.Name}");
        }
        return result.Plan.Ready ? 0 : 3;
    }
    if (args[0].Equals("indexed-snapshot-verify", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[1..]; var operands = options.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray(); if (operands.Length != 1) return Fail("asset indexed-snapshot-verify requires <indexed-assets.snapshot.json>.");
        var unknown = options.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.Equals("--archives", StringComparison.OrdinalIgnoreCase) && !value.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !value.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset indexed-snapshot-verify option: {unknown[0]}");
        var snapshot = ClientIndexedAssetSnapshotService.Load(operands[0], true, options.Contains("--archives", StringComparer.OrdinalIgnoreCase));
        if (options.Contains("--format=json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else Console.WriteLine($"Snapshot\t{Path.GetFullPath(operands[0])}\nValid\tTrue\nArchiveIdentityChecked\t{options.Contains("--archives", StringComparer.OrdinalIgnoreCase)}\nIndexFingerprint\t{snapshot.IndexFingerprint}\nRoots\t{snapshot.RootClientPaths.Count:N0}\nResolvedAssets\t{snapshot.Files.Count(file => file.SourceRelativePath is not null):N0}\nBlockers\t{snapshot.Blocking.Count:N0}");
        return snapshot.Ready ? 0 : 3;
    }
    if (args[0].Equals("gameobject-bulk-plan", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[1..]; var operands = options.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (operands.Length < 4) return Fail("asset gameobject-bulk-plan requires <GameObjectDisplayInfo.dbc> <schema.xml> <plan.json> <model-or-folder>... .");
        var allowed = new[] { "--library=", "--client-root=", "--display-start=", "--template-start=", "--occupied=", "--format=", "--overwrite" };
        var unknown = options.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !allowed.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset gameobject-bulk-plan option: {unknown[0]}");
        if (!uint.TryParse(Option(options, "--display-start=") ?? "100000", out var displayStart) || displayStart == 0) return Fail("--display-start must be a positive unsigned ID.");
        if (!uint.TryParse(Option(options, "--template-start=") ?? "100000", out var templateStart) || templateStart == 0) return Fail("--template-start must be a positive unsigned ID.");
        var occupiedPath = Option(options, "--occupied="); var occupied = occupiedPath is null ? null : CrucibleContentProjectService.ReadOccupiedIds(occupiedPath);
        var plan = GameObjectBulkGeneratorService.CreatePlan(operands[0], operands[1], operands[3..], displayStart, templateStart, Option(options, "--library="), Option(options, "--client-root="), occupiedTemplateIds: occupied);
        GameObjectBulkGeneratorService.SavePlan(operands[2], plan, options.Contains("--overwrite", StringComparer.OrdinalIgnoreCase));
        if (options.Contains("--format=json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"Plan\t{Path.GetFullPath(operands[2])}\nReady\t{plan.Ready}\nModels\t{plan.Rows.Count:N0}\nAddedDisplays\t{plan.AddedDisplays:N0}\nReusedDisplays\t{plan.Rows.Count - plan.AddedDisplays:N0}\nPatchAssets\t{plan.Assets.Count:N0}\nBlockers\t{plan.Blockers.Count:N0}");
            foreach (var row in plan.Rows) Console.WriteLine($"ROW\t{row.TemplateId}\tdisplay={row.DisplayId}\t{(row.ReusesDisplay ? "REUSE" : "ADD")}\t{row.ClientPath}\t{row.Name}");
            foreach (var blocker in plan.Blockers) Console.WriteLine($"BLOCKER\t{blocker}"); foreach (var finding in plan.Findings) Console.WriteLine($"FINDING\t{finding}");
        }
        return plan.Ready ? 0 : 3;
    }
    if (args[0].Equals("gameobject-bulk-apply", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[1..]; var operands = options.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray(); if (operands.Length != 2) return Fail("asset gameobject-bulk-apply requires <plan.json> <new-or-empty-output-folder>.");
        var unknown = options.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !value.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset gameobject-bulk-apply option: {unknown[0]}");
        var result = GameObjectBulkGeneratorService.Apply(GameObjectBulkGeneratorService.LoadPlan(operands[0]), operands[1]);
        if (options.Contains("--format=json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"Output\t{result.OutputRoot}\nDBC\t{result.DbcPath}\nDBC_SHA256\t{result.DbcSha256}\nSQL\t{result.SqlPath}\nManifest\t{result.ManifestPath}\nPatch\t{result.PatchPath}\nReceipt\t{result.ReceiptPath}\nAddedDisplays\t{result.AddedDisplays:N0}\nTemplates\t{result.Templates:N0}\nPatchEntries\t{result.PatchEntries:N0}");
        return 0;
    }
    if (args is ["adt-texture-add-plan", var addTextureAdtPath, var newTexturePath, var addTextureCellText, var addTexturePlanPath, .. var addTextureOptions])
    {
        var overwrite = addTextureOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var encodingText = (Option(addTextureOptions, "--encoding=") ?? "auto").Replace("-", string.Empty, StringComparison.Ordinal); var initialText = Option(addTextureOptions, "--initial-alpha=") ?? "0"; var unknown = addTextureOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--encoding=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--initial-alpha=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-add-plan option: {unknown[0]}");
        if (!Enum.TryParse<AdtNewLayerEncoding>(encodingText, true, out var encoding) || !Enum.IsDefined(encoding)) return Fail("--encoding must be auto, packed-4-bit, big-8-bit, or rle-8-bit."); if (!byte.TryParse(initialText, out var initialAlpha)) return Fail("--initial-alpha must be 0–255."); var cells = new List<(int, int)>();
        foreach (var token in addTextureCellText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var parts = token.Split(':'); if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return Fail($"Invalid ADT cell '{token}'; use x:y,x:y."); cells.Add((x, y)); }
        var plan = AdtTextureStructureService.Plan(addTextureAdtPath, newTexturePath, cells, encoding, initialAlpha); AdtTextureStructureService.SavePlan(plan, addTexturePlanPath, overwrite); Console.Error.WriteLine($"Planned MTEX {plan.TextureId} ({plan.TexturePath}) plus one {plan.Encoding} layer in {plan.Cells.Count:N0} cell(s): {Path.GetFullPath(addTexturePlanPath)}"); return 0;
    }
    if (args is ["adt-texture-add-apply", var applyTextureStructurePlan, var textureStructureOutput, .. var textureStructureOptions])
    {
        var overwrite = textureStructureOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = textureStructureOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-add-apply option: {unknown[0]}"); var result = AdtTextureStructureService.Apply(AdtTextureStructureService.LoadPlan(applyTextureStructurePlan), textureStructureOutput, overwrite); Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nTextureId\t{result.TextureId}\nEditedCells\t{result.EditedCells:N0}"); return 0;
    }
    if (args is ["adt-alpha-info", var alphaAdtPath, .. var alphaInfoOptions])
    {
        var json = alphaInfoOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var includeCells = alphaInfoOptions.Contains("--cells", StringComparer.OrdinalIgnoreCase); var unknown = alphaInfoOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--cells", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-alpha-info option: {unknown[0]}"); var inspection = AdtAlphaMapService.Inspect(alphaAdtPath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspection, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"Path\t{inspection.Path}\nSHA256\t{inspection.Sha256}\nAlphaMaps\t{inspection.Maps.Count:N0}\nCellsWithAlpha\t{inspection.Maps.Select(map => (map.CellX, map.CellY)).Distinct().Count():N0}");
            foreach (var group in inspection.Maps.GroupBy(map => map.Encoding).OrderBy(group => group.Key)) Console.WriteLine($"ENCODING\t{group.Key}\t{group.Count():N0}");
            foreach (var finding in inspection.Findings) Console.WriteLine($"FINDING\t{finding}");
            if (includeCells) foreach (var map in inspection.Maps) Console.WriteLine($"ALPHA\t{map.CellX},{map.CellY}\tslot={map.Slot}\ttexture={map.TextureId}\tpath={map.TexturePath ?? "MISSING"}\tencoding={map.Encoding}\tcapacity={map.Capacity}\tused={map.EncodedBytesUsed}\trange={map.Minimum}..{map.Maximum}\taverage={map.Average:0.###}");
        }
        return inspection.Findings.Count == 0 ? 0 : 3;
    }
    if (args is ["adt-alpha-plan", var planAlphaAdtPath, var alphaLayerText, var alphaCenterText, var alphaRadiusText, var targetAlphaText, var opacityText, var alphaCellText, var alphaPlanPath, .. var alphaPlanOptions])
    {
        var overwrite = alphaPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var falloffText = Option(alphaPlanOptions, "--falloff=") ?? "smooth"; var unknown = alphaPlanOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--falloff=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-alpha-plan option: {unknown[0]}");
        if (!int.TryParse(alphaLayerText, out var layerSlot) || layerSlot <= 0) return Fail("Alpha layer slot must be greater than zero; slot 0 is the opaque base layer."); var center = alphaCenterText.Split(':');
        if (center.Length != 2 || !float.TryParse(center[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var centerX) || !float.TryParse(center[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var centerY)) return Fail("Alpha-brush center must use tile-local center-x:center-y numbers.");
        if (!float.TryParse(alphaRadiusText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radius) || !byte.TryParse(targetAlphaText, out var targetAlpha) || !float.TryParse(opacityText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var opacity)) return Fail("Radius and opacity must be numbers; target alpha must be 0–255.");
        if (!Enum.TryParse<AdtTerrainBrushFalloff>(falloffText, true, out var falloff) || !Enum.IsDefined(falloff)) return Fail("--falloff must be linear, smooth, or constant."); IReadOnlyList<(int X, int Y)>? cells = null;
        if (!alphaCellText.Equals("all", StringComparison.OrdinalIgnoreCase)) { var parsed = new List<(int, int)>(); foreach (var token in alphaCellText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var parts = token.Split(':'); if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return Fail($"Invalid ADT cell '{token}'; use x:y,x:y or all."); parsed.Add((x, y)); } cells = parsed; }
        var plan = AdtAlphaMapService.Plan(planAlphaAdtPath, layerSlot, centerX, centerY, radius, targetAlpha, opacity, falloff, cells); AdtAlphaMapService.SavePlan(plan, alphaPlanPath, overwrite); Console.Error.WriteLine($"Planned {plan.Edits.Sum(edit => edit.ChangedPixels):N0} stored alpha-pixel edit(s) across {plan.Edits.Count:N0} map(s): {Path.GetFullPath(alphaPlanPath)}"); return 0;
    }
    if (args is ["adt-alpha-apply", var applyAlphaPlanPath, var alphaOutputPath, .. var alphaApplyOptions])
    {
        var overwrite = alphaApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = alphaApplyOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-alpha-apply option: {unknown[0]}"); var result = AdtAlphaMapService.Apply(AdtAlphaMapService.LoadPlan(applyAlphaPlanPath), alphaOutputPath, overwrite); Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nEditedMaps\t{result.EditedMaps:N0}\nEditedCells\t{result.EditedCells:N0}\nEditedPixels\t{result.EditedPixels:N0}"); return 0;
    }
    if (args is ["adt-texture-info", var textureAdtPath, .. var textureInfoOptions])
    {
        var json = textureInfoOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var includeCells = textureInfoOptions.Contains("--cells", StringComparer.OrdinalIgnoreCase); var unknown = textureInfoOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--cells", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-info option: {unknown[0]}"); var inspection = AdtTextureLayerService.Inspect(textureAdtPath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspection, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else { Console.WriteLine($"Path\t{inspection.Path}\nSHA256\t{inspection.Sha256}\nTextures\t{inspection.Textures.Count:N0}\nLayers\t{inspection.Layers.Count:N0}\nCellsWithLayers\t{inspection.Layers.Select(layer => (layer.CellX, layer.CellY)).Distinct().Count():N0}"); foreach (var texture in inspection.Textures) Console.WriteLine($"MTEX\t{texture.Id}\t{texture.Path}"); foreach (var finding in inspection.Findings) Console.WriteLine($"FINDING\t{finding}"); if (includeCells) foreach (var layer in inspection.Layers) Console.WriteLine($"LAYER\t{layer.CellX},{layer.CellY}\tslot={layer.Slot}\ttexture={layer.TextureId}\tpath={layer.TexturePath ?? "MISSING"}\tflags=0x{layer.Flags:X}\talpha={layer.AlphaOffset}\teffect={layer.EffectId}"); } return inspection.Findings.Count == 0 ? 0 : 3;
    }
    if (args is ["adt-texture-plan", var planTextureAdtPath, var layerSlotText, var textureIdText, var textureCellText, var texturePlanPath, .. var texturePlanOptions])
    {
        var overwrite = texturePlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = texturePlanOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-plan option: {unknown[0]}"); if (!int.TryParse(layerSlotText, out var layerSlot) || layerSlot < 0 || !uint.TryParse(textureIdText, out var textureId)) return Fail("Layer slot must be non-negative and texture ID must be an unsigned MTEX index."); IReadOnlyList<(int X, int Y)> cells;
        if (textureCellText.Equals("all", StringComparison.OrdinalIgnoreCase)) cells = AdtTextureLayerService.Inspect(planTextureAdtPath).Layers.Where(layer => layer.Slot == layerSlot).Select(layer => (layer.CellX, layer.CellY)).Distinct().ToArray(); else { var parsed = new List<(int, int)>(); foreach (var token in textureCellText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var parts = token.Split(':'); if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return Fail($"Invalid ADT cell '{token}'; use x:y,x:y or all."); parsed.Add((x, y)); } cells = parsed; }
        var plan = AdtTextureLayerService.Plan(planTextureAdtPath, cells, layerSlot, textureId); AdtTextureLayerService.SavePlan(plan, texturePlanPath, overwrite); Console.Error.WriteLine($"Planned {plan.Edits.Count:N0} MCLY layer edit(s) to MTEX {plan.TextureId} ({plan.TexturePath}): {Path.GetFullPath(texturePlanPath)}"); return 0;
    }
    if (args is ["adt-texture-apply", var applyTexturePlanPath, var textureOutputPath, .. var textureApplyOptions])
    {
        var overwrite = textureApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = textureApplyOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-texture-apply option: {unknown[0]}"); var result = AdtTextureLayerService.Apply(AdtTextureLayerService.LoadPlan(applyTexturePlanPath), textureOutputPath, overwrite); Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nEditedLayers\t{result.EditedLayers:N0}\nEditedCells\t{result.EditedCells:N0}"); return 0;
    }
    if (args is ["adt-brush-plan", var brushAdtPath, var centerText, var radiusText, var strengthText, var brushPlanPath, .. var brushPlanOptions])
    {
        var overwrite = brushPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var falloffText = Option(brushPlanOptions, "--falloff=") ?? "smooth"; var modeText = (Option(brushPlanOptions, "--mode=") ?? "raise-lower").Replace("-", string.Empty, StringComparison.Ordinal); var targetText = Option(brushPlanOptions, "--target-height="); var seedText = Option(brushPlanOptions, "--seed=") ?? "0";
        var unknown = brushPlanOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--falloff=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--target-height=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--seed=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-brush-plan option: {unknown[0]}");
        var center = centerText.Split(':'); if (center.Length != 2 || !float.TryParse(center[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var centerX) || !float.TryParse(center[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var centerY)) return Fail("Brush center must use tile-local center-x:center-y numbers.");
        if (!float.TryParse(radiusText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radius) || !float.TryParse(strengthText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var strength)) return Fail("Brush radius and signed strength must be numbers.");
        if (!Enum.TryParse<AdtTerrainBrushFalloff>(falloffText, true, out var falloff) || !Enum.IsDefined(falloff)) return Fail("--falloff must be linear, smooth, or constant.");
        if (!Enum.TryParse<AdtTerrainBrushMode>(modeText, true, out var mode) || !Enum.IsDefined(mode)) return Fail("--mode must be raise-lower, flatten, smooth, or noise.");
        float? targetHeight = null; if (targetText is not null) { if (!float.TryParse(targetText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedTarget) || !float.IsFinite(parsedTarget)) return Fail("--target-height must be finite."); targetHeight = parsedTarget; }
        if (!int.TryParse(seedText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seed)) return Fail("--seed must be a signed 32-bit integer.");
        var plan = AdtTerrainBrushService.Plan(brushAdtPath, centerX, centerY, radius, strength, falloff, mode, targetHeight, seed); AdtTerrainBrushService.SavePlan(plan, brushPlanPath, overwrite);
        Console.Error.WriteLine($"Planned {plan.Mode} with {plan.Vertices.Count:N0} MCVT vertex edit(s) across {plan.Vertices.Select(vertex => (vertex.CellX, vertex.CellY)).Distinct().Count():N0} cell(s): {Path.GetFullPath(brushPlanPath)}"); return 0;
    }
    if (args is ["adt-brush-apply", var applyBrushPlanPath, var brushOutputPath, .. var brushApplyOptions])
    {
        var overwrite = brushApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = brushApplyOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-brush-apply option: {unknown[0]}");
        var result = AdtTerrainBrushService.Apply(AdtTerrainBrushService.LoadPlan(applyBrushPlanPath), brushOutputPath, overwrite);
        Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nEditedVertices\t{result.EditedVertices:N0}\nEditedCells\t{result.EditedCells:N0}"); return 0;
    }
    if (args is ["adt-height-plan", var adtPath, var deltaText, var cellText, var heightPlanPath, .. var heightPlanOptions])
    {
        var overwrite = heightPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = heightPlanOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-height-plan option: {unknown[0]}");
        if (!float.TryParse(deltaText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delta) || !float.IsFinite(delta)) return Fail("Height delta must be finite.");
        IReadOnlyList<(int X, int Y)> cells;
        if (cellText.Equals("all", StringComparison.OrdinalIgnoreCase)) cells = MapAssetInspectionService.Inspect(adtPath).Cells.Where(cell => cell.Present).Select(cell => (cell.X, cell.Y)).ToArray();
        else
        {
            var parsed = new List<(int, int)>(); foreach (var token in cellText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var parts = token.Split(':'); if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return Fail($"Invalid ADT cell '{token}'; use x:y,x:y or all."); parsed.Add((x, y)); } cells = parsed;
        }
        var plan = AdtHeightEditService.Plan(adtPath, cells, delta); AdtHeightEditService.SavePlan(plan, heightPlanPath, overwrite);
        Console.Error.WriteLine($"Planned {plan.Cells.Count:N0} ADT terrain-cell edit(s) at delta {plan.Delta:R}: {Path.GetFullPath(heightPlanPath)}"); return 0;
    }
    if (args is ["adt-height-apply", var applyHeightPlanPath, var heightOutputPath, .. var heightApplyOptions])
    {
        var overwrite = heightApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = heightApplyOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset adt-height-apply option: {unknown[0]}");
        var result = AdtHeightEditService.Apply(AdtHeightEditService.LoadPlan(applyHeightPlanPath), heightOutputPath, overwrite);
        Console.WriteLine($"Output\t{result.OutputPath}\nSHA256\t{result.OutputSha256}\nReceipt\t{result.ReceiptPath}\nEditedCells\t{result.EditedCells:N0}\nDelta\t{result.Delta:R}"); return 0;
    }
    if (args is ["map-info", var mapPath, .. var mapOptions])
    {
        var json = mapOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var includeCells = mapOptions.Contains("--cells", StringComparer.OrdinalIgnoreCase); var includePlacements = mapOptions.Contains("--placements", StringComparer.OrdinalIgnoreCase);
        var unknown = mapOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--cells", StringComparison.OrdinalIgnoreCase) && !option.Equals("--placements", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset map-info option: {unknown[0]}");
        var inspection = MapAssetInspectionService.Inspect(mapPath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspection, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"Path\t{inspection.Path}\nKind\t{inspection.Kind}\nVersion\t{inspection.Version}\nGrid\t{inspection.GridWidth}x{inspection.GridHeight}\nPresent\t{inspection.PresentCells:N0}/{inspection.Cells.Count:N0}\nWorldTile\t{inspection.TileX?.ToString() ?? "-"},{inspection.TileY?.ToString() ?? "-"}\nHeight\t{inspection.MinimumHeight?.ToString("R") ?? "-"}..{inspection.MaximumHeight?.ToString("R") ?? "-"}\nTextures\t{inspection.TexturePaths.Count:N0}\nModels\t{inspection.ModelPaths.Count:N0}\nM2Placements\t{inspection.M2Placements.Count:N0}\nWMOs\t{inspection.WmoPaths.Count:N0}\nWMOPlacements\t{inspection.WmoPlacements.Count:N0}\nHeaderFlags\t0x{inspection.HeaderFlags:X}");
            foreach (var chunk in inspection.Chunks) Console.WriteLine($"CHUNK\t{chunk.Id}\tcount={chunk.Occurrences:N0}\tbytes={chunk.PayloadBytes:N0}");
            foreach (var finding in inspection.Findings) Console.WriteLine($"FINDING\t{finding}");
            if (includeCells) foreach (var cell in inspection.Cells.Where(cell => cell.Present)) Console.WriteLine($"CELL\t{cell.X},{cell.Y}\tflags=0x{cell.Flags:X}\tarea={cell.AreaId?.ToString() ?? "-"}\tholes=0x{cell.Holes?.ToString("X") ?? "-"}\theight={cell.MinimumHeight?.ToString("R") ?? "-"}..{cell.MaximumHeight?.ToString("R") ?? "-"}");
            if (includePlacements) foreach (var placement in inspection.M2Placements) Console.WriteLine($"M2_PLACEMENT\tindex={placement.Index:N0}\tuid={placement.UniqueId:N0}\tname={placement.NameId:N0}\tpath={placement.ClientPath ?? "<unresolved>"}\tposition={placement.Position.X:R},{placement.Position.Y:R},{placement.Position.Z:R}\torientation={placement.Orientation.X:R},{placement.Orientation.Y:R},{placement.Orientation.Z:R}\tflags=0x{placement.Flags:X}\tscaleRaw={placement.ScaleRaw:N0}\tscale={placement.Scale:R}");
            if (includePlacements) foreach (var placement in inspection.WmoPlacements) Console.WriteLine($"WMO_PLACEMENT\tindex={placement.Index:N0}\tuid={placement.UniqueId:N0}\tname={placement.NameId:N0}\tpath={placement.ClientPath ?? "<unresolved>"}\tposition={placement.Position.X:R},{placement.Position.Y:R},{placement.Position.Z:R}\torientation={placement.Orientation.X:R},{placement.Orientation.Y:R},{placement.Orientation.Z:R}\textents={placement.MinimumExtent.X:R},{placement.MinimumExtent.Y:R},{placement.MinimumExtent.Z:R}..{placement.MaximumExtent.X:R},{placement.MaximumExtent.Y:R},{placement.MaximumExtent.Z:R}\tflags=0x{placement.Flags:X}\tdoodadSet={placement.DoodadSet:N0}\tnameSet={placement.NameSet:N0}\tscaleRaw={placement.ScaleRaw:N0}\tscale={placement.Scale:R}");
        }
        return inspection.Findings.Any(finding => finding.StartsWith("MVER", StringComparison.Ordinal)) ? 3 : 0;
    }
    if (args is ["texture-info", var texturePath])
    {
        var info = BlpTextureService.Inspect(texturePath);
        Console.WriteLine($"Path\t{info.Path}\nVersion\t{info.Version}\nDimensions\t{info.Width}x{info.Height}\nEncoding\t{info.Encoding}\nAlphaDepth\t{info.AlphaDepth}\nAlphaEncoding\t{info.AlphaEncoding}\nMipmaps\t{info.MipLevels.Count} (declared={info.DeclaresMipmaps})");
        foreach (var mip in info.MipLevels) Console.WriteLine($"MIP\t{mip.Index}\t{mip.Width}x{mip.Height}\t{mip.Offset}\t{mip.Size}");
        foreach (var warning in info.Warnings) Console.WriteLine($"WARN\t{warning}");
        return 0;
    }
    if (args is ["texture-decode", var decodeSource, var decodeOutput, .. var decodeOptions])
    {
        var mipText = Option(decodeOptions, "--mip=") ?? "0";
        var overwrite = decodeOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var unknown = decodeOptions.Where(option => !option.StartsWith("--mip=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset texture-decode option: {unknown[0]}");
        if (!int.TryParse(mipText, out var mip) || mip < 0) return Fail("--mip must be a non-negative integer.");
        BlpTextureService.DecodeToPng(decodeSource, decodeOutput, mip, overwrite);
        Console.Error.WriteLine($"Decoded native BLP mip {mip} to PNG: {Path.GetFullPath(decodeOutput)}");
        return 0;
    }
    if (args is ["texture-proof", var proofSource, .. var proofOptions])
    {
        var mipText = Option(proofOptions, "--mip=") ?? "0"; if (!int.TryParse(mipText, out var mip) || mip < 0) return Fail("--mip must be a non-negative integer.");
        var codecText = (Option(proofOptions, "--codec=") ?? "auto").Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var codec = codecText switch { "auto" => BlpOutputFormat.Auto, "dxt1" => BlpOutputFormat.Dxt1, "dxt1a" or "dxt1alpha" => BlpOutputFormat.Dxt1Alpha, "dxt3" => BlpOutputFormat.Dxt3, "dxt5" => BlpOutputFormat.Dxt5, _ => (BlpOutputFormat)(-1) }; if ((int)codec < 0) return Fail("--codec must be auto, dxt1, dxt1a, dxt3, or dxt5.");
        var qualityText = (Option(proofOptions, "--quality=") ?? "best").ToLowerInvariant(); var quality = qualityText switch { "fast" => BlpOutputQuality.Fast, "balanced" => BlpOutputQuality.Balanced, "best" => BlpOutputQuality.Best, _ => (BlpOutputQuality)(-1) }; if ((int)quality < 0) return Fail("--quality must be fast, balanced, or best.");
        var amplificationText = Option(proofOptions, "--amplify=") ?? "4"; if (!double.TryParse(amplificationText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amplification) || !double.IsFinite(amplification) || amplification <= 0 || amplification > 255) return Fail("--amplify must be a finite number from 0 exclusive through 255.");
        var reportFormat = (Option(proofOptions, "--report=") ?? "text").ToLowerInvariant(); if (reportFormat is not "text" and not "json") return Fail("--report must be text or json.");
        var maximumRgbMae = OptionalFinite(proofOptions, "--max-rgb-mae="); var maximumAlphaMae = OptionalFinite(proofOptions, "--max-alpha-mae=");
        var maximumAlphaCrossingsText = Option(proofOptions, "--max-alpha-crossings="); long? maximumAlphaCrossings = maximumAlphaCrossingsText is null ? null : long.TryParse(maximumAlphaCrossingsText, out var crossings) && crossings >= 0 ? crossings : throw new ArgumentException("--max-alpha-crossings must be a non-negative integer.");
        var differenceOutput = Option(proofOptions, "--difference="); var previewOutput = Option(proofOptions, "--preview="); var overwrite = proofOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var generateMipmaps = !proofOptions.Contains("--no-mips", StringComparer.OrdinalIgnoreCase);
        var unknown = proofOptions.Where(option => !option.StartsWith("--mip=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--codec=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--amplify=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--report=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--difference=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--preview=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--max-rgb-mae=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--max-alpha-mae=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--max-alpha-crossings=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--no-mips", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset texture-proof option: {unknown[0]}");
        proofSource = Path.GetFullPath(proofSource); differenceOutput = NormalizeProofPng(differenceOutput, "--difference"); previewOutput = NormalizeProofPng(previewOutput, "--preview");
        if (differenceOutput is not null && differenceOutput.Equals(proofSource, StringComparison.OrdinalIgnoreCase) || previewOutput is not null && previewOutput.Equals(proofSource, StringComparison.OrdinalIgnoreCase)) return Fail("Texture proof images require separate output paths; the source remains immutable.");
        if (differenceOutput is not null && previewOutput is not null && differenceOutput.Equals(previewOutput, StringComparison.OrdinalIgnoreCase)) return Fail("--difference and --preview must use separate PNG output paths.");
        var source = Path.GetExtension(proofSource).Equals(".blp", StringComparison.OrdinalIgnoreCase) ? BlpTextureService.Decode(proofSource, mip) : BlpTextureService.DecodeImage(proofSource);
        var proof = TextureComparisonService.AnalyzeEncoding(source, new(codec, generateMipmaps, quality), amplification);
        if (differenceOutput is not null) BlpTextureService.WritePng(differenceOutput, proof.DifferenceMap, overwrite); if (previewOutput is not null) BlpTextureService.WritePng(previewOutput, proof.DecodedPreview, overwrite);
        if (reportFormat == "json") Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(proof, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })); else WriteTextureProof(proof, proofSource, amplification, differenceOutput, previewOutput);
        var exceeds = maximumRgbMae is { } rgbLimit && proof.Comparison.RgbCombined.MeanAbsoluteError > rgbLimit || maximumAlphaMae is { } alphaLimit && proof.Comparison.Alpha.MeanAbsoluteError > alphaLimit || maximumAlphaCrossings is { } crossingLimit && proof.Comparison.AlphaThresholdCrossings > crossingLimit;
        if (exceeds) Console.Error.WriteLine("Texture proof exceeded at least one explicit loss threshold."); return exceeds ? 3 : 0;
    }
    if (args is ["texture-compose", var composeOutput, .. var textureComposeOptions])
    {
        var positional = textureComposeOptions.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).Select(path => new TextureLayerArgument(path, TextureBlendMode.Normal, 1, 0, 0, true));
        var described = textureComposeOptions.Where(option => option.StartsWith("--layer=", StringComparison.OrdinalIgnoreCase)).Select(option => ParseTextureLayerArgument(option[8..])); var requestedLayers = positional.Concat(described).ToArray();
        if (requestedLayers.Length == 0) return Fail("texture-compose requires at least one positional source or --layer=path|blend|opacity|x|y|visible layer. Layers are ordered bottom-to-top.");
        var widthText = Option(textureComposeOptions, "--width="); var heightText = Option(textureComposeOptions, "--height=");
        if ((widthText is not null && (!int.TryParse(widthText, out var requestedWidth) || requestedWidth <= 0)) ||
            (heightText is not null && (!int.TryParse(heightText, out var requestedHeight) || requestedHeight <= 0)))
            return Fail("--width and --height must be positive whole pixels.");
        var backgroundParts = (Option(textureComposeOptions, "--background=") ?? "0:0:0:0").Split(':'); if (backgroundParts.Length != 4 || backgroundParts.Any(value => !byte.TryParse(value, out _))) return Fail("--background must be R:G:B:A with four byte values."); var background = backgroundParts.Select(byte.Parse).ToArray();
        var codecText = (Option(textureComposeOptions, "--codec=") ?? "auto").Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant(); var codec = codecText switch { "auto" => BlpOutputFormat.Auto, "dxt1" => BlpOutputFormat.Dxt1, "dxt1a" or "dxt1alpha" => BlpOutputFormat.Dxt1Alpha, "dxt3" => BlpOutputFormat.Dxt3, "dxt5" => BlpOutputFormat.Dxt5, _ => (BlpOutputFormat)(-1) }; if ((int)codec < 0) return Fail("--codec must be auto, dxt1, dxt1a, dxt3, or dxt5.");
        var qualityText = (Option(textureComposeOptions, "--quality=") ?? "best").ToLowerInvariant(); var quality = qualityText switch { "fast" => BlpOutputQuality.Fast, "balanced" => BlpOutputQuality.Balanced, "best" => BlpOutputQuality.Best, _ => (BlpOutputQuality)(-1) }; if ((int)quality < 0) return Fail("--quality must be fast, balanced, or best.");
        var reportFormat = (Option(textureComposeOptions, "--report=") ?? "text").ToLowerInvariant(); if (reportFormat is not "text" and not "json") return Fail("--report must be text or json.");
        var overwrite = textureComposeOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var generateMipmaps = !textureComposeOptions.Contains("--no-mips", StringComparer.OrdinalIgnoreCase);
        var unknown = textureComposeOptions.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.StartsWith("--layer=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--width=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--height=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--background=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--codec=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--report=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--no-mips", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset texture-compose option: {unknown[0]}");
        composeOutput = Path.GetFullPath(composeOutput); var decodedLayers = requestedLayers.Select(layer =>
        {
            var path = Path.GetFullPath(layer.Path); if (path.Equals(composeOutput, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Composition output must not overwrite a layer source."); var texture = DecodeTextureInput(path); return new TextureCompositionLayer(Path.GetFileName(path), texture, layer.Visible, layer.Opacity, layer.OffsetX, layer.OffsetY, layer.BlendMode);
        }).ToArray();
        var width = widthText is null ? decodedLayers[0].Texture.Width : int.Parse(widthText); var height = heightText is null ? decodedLayers[0].Texture.Height : int.Parse(heightText);
        var composition = TextureLayerCompositionService.Compose(width, height, decodedLayers, background[0], background[1], background[2], background[3]); string encoding;
        if (Path.GetExtension(composeOutput).Equals(".png", StringComparison.OrdinalIgnoreCase)) { BlpTextureService.WritePng(composeOutput, composition.Texture, overwrite); encoding = "PNG RGBA"; }
        else if (Path.GetExtension(composeOutput).Equals(".blp", StringComparison.OrdinalIgnoreCase)) { BlpTextureService.EncodeBlp2(composition.Texture, composeOutput, new(codec, generateMipmaps, quality), overwrite); encoding = BlpTextureService.Inspect(composeOutput).Encoding; }
        else return Fail("texture-compose output must end in .png or .blp.");
        if (reportFormat == "json") Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Output = composeOutput, Width = width, Height = height, Encoding = encoding, OutputBytes = new FileInfo(composeOutput).Length, Layers = composition.Layers }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else { Console.WriteLine($"Output\t{composeOutput}\nDimensions\t{width}x{height}\nEncoding\t{encoding}\nOutputBytes\t{new FileInfo(composeOutput).Length:N0}\nLayers\t{composition.Layers.Count:N0} (bottom-to-top)"); foreach (var layer in composition.Layers) Console.WriteLine($"LAYER\t{layer.Name}\tvisible={layer.Visible}\tinCanvas={layer.PixelsInCanvas:N0}\tcontributing={layer.ContributingPixels:N0}\tchanged={layer.ChangedPixels:N0}\tclipped={layer.ClippedPixels:N0}"); }
        return 0;
    }
    if (args is ["texture-mask", var maskSourcePath, var maskPath, var maskOutput, .. var maskOptions])
    {
        var sourceMipText = Option(maskOptions, "--source-mip=") ?? "0"; var maskMipText = Option(maskOptions, "--mask-mip=") ?? "0";
        if (!int.TryParse(sourceMipText, out var sourceMip) || sourceMip < 0 || !int.TryParse(maskMipText, out var maskMip) || maskMip < 0) return Fail("--source-mip and --mask-mip must be non-negative integers.");
        var channelText = (Option(maskOptions, "--mask=") ?? "alpha").Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant(); var channel = channelText switch { "alpha" or "a" => TextureMaskChannel.Alpha, "luminance" or "luma" or "rgb" => TextureMaskChannel.Luminance, "red" or "r" => TextureMaskChannel.Red, "green" or "g" => TextureMaskChannel.Green, "blue" or "b" => TextureMaskChannel.Blue, _ => (TextureMaskChannel)(-1) }; if ((int)channel < 0) return Fail("--mask must be alpha, luminance, red, green, or blue.");
        var maskTransformStrengthText = Option(maskOptions, "--strength=") ?? "1"; if (!double.TryParse(maskTransformStrengthText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var strength) || !double.IsFinite(strength) || strength is < 0 or > 1) return Fail("--strength must be a finite number from 0 through 1.");
        double[] scale; double[] offset; try { scale = ParseTextureVector(Option(maskOptions, "--scale=") ?? "1:1:1:1", "--scale"); offset = ParseTextureVector(Option(maskOptions, "--offset=") ?? "0:0:0:0", "--offset"); } catch (ArgumentException exception) { return Fail(exception.Message); }
        var codecText = (Option(maskOptions, "--codec=") ?? "auto").Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant(); var codec = codecText switch { "auto" => BlpOutputFormat.Auto, "dxt1" => BlpOutputFormat.Dxt1, "dxt1a" or "dxt1alpha" => BlpOutputFormat.Dxt1Alpha, "dxt3" => BlpOutputFormat.Dxt3, "dxt5" => BlpOutputFormat.Dxt5, _ => (BlpOutputFormat)(-1) }; if ((int)codec < 0) return Fail("--codec must be auto, dxt1, dxt1a, dxt3, or dxt5.");
        var qualityText = (Option(maskOptions, "--quality=") ?? "best").ToLowerInvariant(); var quality = qualityText switch { "fast" => BlpOutputQuality.Fast, "balanced" => BlpOutputQuality.Balanced, "best" => BlpOutputQuality.Best, _ => (BlpOutputQuality)(-1) }; if ((int)quality < 0) return Fail("--quality must be fast, balanced, or best.");
        var reportFormat = (Option(maskOptions, "--report=") ?? "text").ToLowerInvariant(); if (reportFormat is not "text" and not "json") return Fail("--report must be text or json.");
        var unknown = maskOptions.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.StartsWith("--source-mip=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--mask-mip=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--mask=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--invert-mask", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--strength=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--offset=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--codec=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--report=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--no-mips", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset texture-mask option: {unknown[0]}");
        maskSourcePath = Path.GetFullPath(maskSourcePath); maskPath = Path.GetFullPath(maskPath); maskOutput = Path.GetFullPath(maskOutput); if (maskOutput.Equals(maskSourcePath, StringComparison.OrdinalIgnoreCase) || maskOutput.Equals(maskPath, StringComparison.OrdinalIgnoreCase)) return Fail("Masked output must not overwrite the source or mask input.");
        if (!Path.GetExtension(maskOutput).Equals(".png", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(maskOutput).Equals(".blp", StringComparison.OrdinalIgnoreCase)) return Fail("texture-mask output must end in .png or .blp.");
        var sourceTexture = DecodeTextureInput(maskSourcePath, sourceMip); var maskTexture = DecodeTextureInput(maskPath, maskMip); var transform = new TextureChannelTransform(scale[0], scale[1], scale[2], scale[3], offset[0], offset[1], offset[2], offset[3]);
        var result = TextureMaskTransformService.Apply(sourceTexture, maskTexture, new(channel, maskOptions.Contains("--invert-mask", StringComparer.OrdinalIgnoreCase), strength, transform)); var overwrite = maskOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); string encoding;
        if (Path.GetExtension(maskOutput).Equals(".png", StringComparison.OrdinalIgnoreCase)) { BlpTextureService.WritePng(maskOutput, result.Texture, overwrite); encoding = "PNG RGBA"; }
        else { BlpTextureService.EncodeBlp2(result.Texture, maskOutput, new(codec, !maskOptions.Contains("--no-mips", StringComparer.OrdinalIgnoreCase), quality), overwrite); encoding = BlpTextureService.Inspect(maskOutput).Encoding; }
        if (reportFormat == "json") Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Source = maskSourcePath, Mask = maskPath, Output = maskOutput, result.Texture.Width, result.Texture.Height, MaskChannel = TextureMaskTransformService.ChannelName(channel), Inverted = maskOptions.Contains("--invert-mask", StringComparer.OrdinalIgnoreCase), Strength = strength, Scale = scale, Offset = offset, result.MinimumMask, result.MaximumMask, result.PixelsInfluenced, result.PixelsChanged, Encoding = encoding, OutputBytes = new FileInfo(maskOutput).Length }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"Source\t{maskSourcePath}\nMask\t{maskPath}\nOutput\t{maskOutput}\nDimensions\t{result.Texture.Width}x{result.Texture.Height}\nMaskChannel\t{TextureMaskTransformService.ChannelName(channel)}\nMaskRange\t{result.MinimumMask}..{result.MaximumMask}\nInfluencedPixels\t{result.PixelsInfluenced:N0}\nChangedPixels\t{result.PixelsChanged:N0}\nEncoding\t{encoding}\nOutputBytes\t{new FileInfo(maskOutput).Length:N0}");
        return 0;
    }
    if (args is ["texture-brush", var editSource, var editOutput, .. var editOptions])
    {
        var mipText = Option(editOptions, "--mip=") ?? "0"; if (!int.TryParse(mipText, out var mip) || mip < 0) return Fail("--mip must be a non-negative integer.");
        var textureRadiusText = Option(editOptions, "--radius=") ?? "8"; var textureOpacityText = Option(editOptions, "--opacity=") ?? "1";
        if (!double.TryParse(textureRadiusText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radius) || !double.TryParse(textureOpacityText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var opacity)) return Fail("--radius and --opacity must use invariant finite numbers.");
        var colorParts = (Option(editOptions, "--color=") ?? "255:255:255:255").Split(':'); if (colorParts.Length != 4 || colorParts.Any(value => !byte.TryParse(value, out _))) return Fail("--color must be R:G:B:A with four byte values from 0 through 255.");
        var colors = colorParts.Select(byte.Parse).ToArray(); var modeText = (Option(editOptions, "--tool=") ?? "color-alpha").Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        var mode = modeText switch { "color-alpha" or "rgba" => TexturePaintMode.ColorAndAlpha, "rgb" or "rgb-only" => TexturePaintMode.RgbOnly, "alpha" or "alpha-only" => TexturePaintMode.AlphaOnly, "erase-alpha" or "erase" => TexturePaintMode.EraseAlpha, _ => (TexturePaintMode)(-1) }; if ((int)mode < 0) return Fail("--tool must be color-alpha, rgb, alpha, or erase-alpha.");
        var falloffText = (Option(editOptions, "--falloff=") ?? "smooth").ToLowerInvariant(); var falloff = falloffText switch { "smooth" => TextureBrushFalloff.Smooth, "linear" => TextureBrushFalloff.Linear, "hard" => TextureBrushFalloff.Hard, _ => (TextureBrushFalloff)(-1) }; if ((int)falloff < 0) return Fail("--falloff must be smooth, linear, or hard.");
        var points = editOptions.Where(option => option.StartsWith("--point=", StringComparison.OrdinalIgnoreCase)).Select(option => option[8..].Split(':')).Select(parts => parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ? new TexturePoint(x, y) : throw new ArgumentException("Each --point must be x:y using invariant pixel coordinates.")).ToArray();
        var fill = editOptions.Contains("--fill", StringComparer.OrdinalIgnoreCase); var invert = editOptions.Contains("--invert-alpha", StringComparer.OrdinalIgnoreCase); if ((fill ? 1 : 0) + (invert ? 1 : 0) + (points.Length > 0 ? 1 : 0) != 1) return Fail("Choose exactly one edit operation: repeat --point for one stroke, --fill, or --invert-alpha.");
        var overwrite = editOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var noMips = editOptions.Contains("--no-mips", StringComparer.OrdinalIgnoreCase);
        var formatText = (Option(editOptions, "--format=") ?? "auto").Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant(); var format = formatText switch { "auto" => BlpOutputFormat.Auto, "dxt1" => BlpOutputFormat.Dxt1, "dxt1a" or "dxt1alpha" => BlpOutputFormat.Dxt1Alpha, "dxt3" => BlpOutputFormat.Dxt3, "dxt5" => BlpOutputFormat.Dxt5, _ => (BlpOutputFormat)(-1) }; if ((int)format < 0) return Fail("--format must be auto, dxt1, dxt1a, dxt3, or dxt5.");
        var qualityText = (Option(editOptions, "--quality=") ?? "best").ToLowerInvariant(); var quality = qualityText switch { "fast" => BlpOutputQuality.Fast, "balanced" => BlpOutputQuality.Balanced, "best" => BlpOutputQuality.Best, _ => (BlpOutputQuality)(-1) }; if ((int)quality < 0) return Fail("--quality must be fast, balanced, or best.");
        var unknown = editOptions.Where(option => !option.StartsWith("--point=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--mip=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--radius=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--opacity=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--color=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--tool=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--falloff=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--fill", StringComparison.OrdinalIgnoreCase) && !option.Equals("--invert-alpha", StringComparison.OrdinalIgnoreCase) && !option.Equals("--no-mips", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown asset texture-brush option: {unknown[0]}");
        editSource = Path.GetFullPath(editSource); editOutput = Path.GetFullPath(editOutput); if (editSource.Equals(editOutput, StringComparison.OrdinalIgnoreCase)) return Fail("Texture editing requires a separate output path; the source remains immutable.");
        var texture = Path.GetExtension(editSource).Equals(".blp", StringComparison.OrdinalIgnoreCase) ? BlpTextureService.Decode(editSource, mip) : BlpTextureService.DecodeImage(editSource); var settings = new TextureBrushSettings(radius, opacity, colors[0], colors[1], colors[2], colors[3], mode, falloff);
        var result = invert ? TexturePixelEditService.InvertAlpha(texture) : fill ? TexturePixelEditService.Fill(texture, settings) : TexturePixelEditService.ApplyStroke(texture, points, settings);
        if (Path.GetExtension(editOutput).Equals(".png", StringComparison.OrdinalIgnoreCase)) BlpTextureService.WritePng(editOutput, texture, overwrite);
        else if (Path.GetExtension(editOutput).Equals(".blp", StringComparison.OrdinalIgnoreCase)) BlpTextureService.EncodeBlp2(texture, editOutput, new(format, !noMips, quality), overwrite);
        else return Fail("Texture brush output must end in .png or .blp.");
        Console.WriteLine($"Output\t{editOutput}\nOperation\t{(invert ? "InvertAlpha" : fill ? "Fill" : "Stroke")}\nChangedPixels\t{result.ChangedPixels:N0}\nBounds\t{result.MinimumX},{result.MinimumY}..{result.MaximumX},{result.MaximumY}\nDimensions\t{texture.Width}x{texture.Height}"); return result.Changed ? 0 : 3;
    }
    if (args is ["texture-encode", var encodeSource, var encodeOutput, .. var encodeOptions])
    {
        var formatText = (Option(encodeOptions, "--format=") ?? "auto").Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var format = formatText switch { "auto" => BlpOutputFormat.Auto, "dxt1" => BlpOutputFormat.Dxt1, "dxt1a" or "dxt1alpha" => BlpOutputFormat.Dxt1Alpha, "dxt3" => BlpOutputFormat.Dxt3, "dxt5" => BlpOutputFormat.Dxt5, _ => (BlpOutputFormat)(-1) };
        if ((int)format < 0) return Fail("--format must be auto, dxt1, dxt1a, dxt3, or dxt5.");
        var qualityText = (Option(encodeOptions, "--quality=") ?? "best").ToLowerInvariant();
        var quality = qualityText switch { "fast" => BlpOutputQuality.Fast, "balanced" => BlpOutputQuality.Balanced, "best" => BlpOutputQuality.Best, _ => (BlpOutputQuality)(-1) };
        if ((int)quality < 0) return Fail("--quality must be fast, balanced, or best.");
        var mipmaps = !encodeOptions.Contains("--no-mips", StringComparer.OrdinalIgnoreCase);
        var overwrite = encodeOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var unknown = encodeOptions.Where(option => !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--no-mips", StringComparison.OrdinalIgnoreCase) && !option.Equals("--mips", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset texture-encode option: {unknown[0]}");
        BlpTextureService.EncodeFromImage(encodeSource, encodeOutput, new(format, mipmaps, quality), overwrite);
        var info = BlpTextureService.Inspect(encodeOutput);
        Console.Error.WriteLine($"Encoded {info.Width}x{info.Height} {info.Encoding} BLP2 with {info.MipLevels.Count} mip level(s): {info.Path}");
        return 0;
    }
    if (args is ["texture-validate", var validatePath, .. var validateOptions])
    {
        var recursive = validateOptions.Contains("--recursive", StringComparer.OrdinalIgnoreCase);
        var unknown = validateOptions.Where(option => !option.Equals("--recursive", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset texture-validate option: {unknown[0]}");
        var summary = BlpTextureService.ValidateEach(validatePath, recursive, result => Console.WriteLine(result.Valid
            ? $"{(result.Info!.Warnings.Count == 0 ? "PASS" : "WARN")}\t{result.Info.Width}x{result.Info.Height}\t{result.Info.Encoding}\t{result.Info.MipLevels.Count}\t{result.Path}{(result.Info.Warnings.Count == 0 ? string.Empty : $"\t{string.Join(" | ", result.Info.Warnings)}") }"
            : $"FAIL\t{result.Error}\t{result.Path}"));
        Console.Error.WriteLine($"Validated {summary.Total:N0} BLP texture(s): {summary.Total - summary.Failures:N0} decodable, {summary.Warnings:N0} with warning(s), {summary.Failures:N0} invalid.");
        return summary.Failures == 0 && summary.Warnings == 0 ? 0 : 3;
    }
    if (args is ["library-plan", var sourceRoot, var libraryRoot, .. var planOptions])
    {
        var maxText = Option(planOptions, "--max-gb=") ?? "2";
        var unknown = planOptions.Where(option => !option.StartsWith("--max-gb=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-plan option: {unknown[0]}");
        var maximum = checked((long)(double.Parse(maxText, System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024 * 1024));
        var plan = BulkAssetLibraryService.CreatePlan(sourceRoot, libraryRoot, maximum, new Progress<(int Done, int Total, string Path)>(value => Console.Error.WriteLine($"Plan {value.Done:N0}/{value.Total:N0}\t{value.Path}")));
        Console.Error.WriteLine($"Asset library plan: {plan.Archives.Count(archive => archive.Eligible):N0} eligible archive(s), {plan.Archives.Count(archive => !archive.Eligible):N0} skipped by size, {plan.Archives.Sum(archive => archive.Entries):N0} archive entries, {plan.LooseBlpFiles + plan.Archives.Sum(archive => archive.BlpFiles):N0} BLP file(s).\nPlan: {Path.Combine(plan.LibraryRoot, "asset-library-plan.json")}");
        return plan.Archives.Any(archive => archive.Error is not null) ? 3 : 0;
    }
    if (args is ["library-run", var runLibraryRoot, .. var runOptions])
    {
        var workersText = Option(runOptions, "--workers=") ?? "6";
        var unknown = runOptions.Where(option => !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-run option: {unknown[0]}");
        var progress = new Progress<(string Stage, int Done, int Total, string Path)>(value => Console.Error.WriteLine($"{value.Stage}\t{value.Done:N0}/{value.Total:N0}\t{value.Path}"));
        var result = BulkAssetLibraryService.RunAsync(runLibraryRoot, int.Parse(workersText, System.Globalization.CultureInfo.InvariantCulture), progress).GetAwaiter().GetResult();
        Console.Error.WriteLine($"Asset library complete: {result.CompletedArchives:N0} archive(s), {result.CopiedLooseBlps:N0} loose BLP copy/copies, {result.ConvertedPngs:N0} PNG conversion(s), {result.FailedArchives:N0} archive failure(s), {result.ConversionFailures:N0} conversion failure(s).\nCatalog: {result.CatalogPath}\nCheckpoint: {result.CheckpointPath}");
        return result.FailedArchives == 0 && result.ConversionFailures == 0 ? 0 : 3;
    }
    if (args is ["library-import", var extractedRoot, var importLibraryRoot, var provenance, .. var importOptions])
    {
        var workersText = Option(importOptions, "--workers=") ?? "6";
        var unknown = importOptions.Where(option => !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-import option: {unknown[0]}");
        var progress = new Progress<(string Stage, long Done, long Total, string Path)>(value =>
            Console.Error.WriteLine(value.Total > 0 ? $"{value.Stage}\t{value.Done:N0}/{value.Total:N0}\t{value.Path}" : $"{value.Stage}\t{value.Path}"));
        var result = BulkAssetLibraryService.ImportExtractedArchiveAsync(extractedRoot, importLibraryRoot, provenance,
            int.Parse(workersText, System.Globalization.CultureInfo.InvariantCulture), progress).GetAwaiter().GetResult();
        Console.Error.WriteLine($"Extracted archive import complete: {result.Provenance}, {result.SourceFiles:N0} source file(s), {result.SourceBytes / (1024d * 1024 * 1024):0.##} GiB, {result.ImportedFiles:N0} newly copied, {result.ConvertedPngs:N0} PNG conversion(s), {result.ConversionFailures:N0} conversion failure(s).\nCatalog: {result.CatalogPath}");
        return result.ConversionFailures == 0 ? 0 : 3;
    }
    if (args is ["library-repair", var repairLibraryRoot, .. var repairOptions])
    {
        var workersText = Option(repairOptions, "--workers=") ?? "6";
        var unknown = repairOptions.Where(option => !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-repair option: {unknown[0]}");
        var progress = new Progress<(string Stage, int Done, int Total, string Path)>(value => Console.Error.WriteLine($"{value.Stage}\t{value.Done:N0}/{value.Total:N0}\t{value.Path}"));
        var result = BulkAssetLibraryService.RepairConversionsAsync(repairLibraryRoot, int.Parse(workersText, System.Globalization.CultureInfo.InvariantCulture), progress).GetAwaiter().GetResult();
        Console.Error.WriteLine($"Asset conversion repair complete: {result.NewlyConvertedPngs:N0} newly recovered PNG(s), {result.RemainingFailures:N0} genuinely unsupported BLP(s).\nCatalog: {result.CatalogPath}\nCheckpoint: {result.CheckpointPath}");
        return result.RemainingFailures == 0 ? 0 : 3;
    }
    if (args is ["library-artifacts", var artifactLibraryRoot, .. var artifactOptions])
    {
        var apply = artifactOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var sourceRoots = artifactOptions.Where(option => option.StartsWith("--source-root=", StringComparison.OrdinalIgnoreCase)).Select(option => option[14..]).ToArray();
        var unknown = artifactOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--source-root=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-artifacts option: {unknown[0]}");
        var progress = new ConsoleProgress(5);
        var result = BulkAssetLibraryService.RepairArchiveArtifacts(artifactLibraryRoot, apply, sourceRoots, progress);
        Console.Error.WriteLine($"Archive artifact {(result.Applied ? "repair" : "audit")}: {result.InvalidArtifacts:N0} invalid generated BLP(s), {result.Recovered:N0} {(result.Applied ? "recovered" : "recoverable")}, {result.Quarantined:N0} quarantined, {result.SourceInvalid:N0} invalid in the source archive, {result.ExtractionFailures:N0} source extraction failure(s), {result.Unmapped:N0} unmapped.\nReport: {result.ReportPath}" +
            (result.Applied ? $"\nCatalog: {result.CatalogPath}" : "\nNo processed asset changed. Review the report, then repeat with --apply."));
        return result.InvalidArtifacts == 0 || result.Applied && result.Unmapped == 0 && result.Recovered + result.Quarantined == result.InvalidArtifacts ? 0 : 3;
    }
    if (args is ["library-layout", var layoutLibraryRoot, .. var layoutOptions])
    {
        var apply = layoutOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = layoutOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-layout option: {unknown[0]}");
        var progress = new Progress<(long Done, long Total, string Path)>(value => Console.Error.WriteLine($"Layout\t{value.Done:N0}/{value.Total:N0}\t{value.Path}"));
        var result = BulkAssetLibraryService.MigrateToContentFirstLayout(layoutLibraryRoot, apply, progress);
        Console.Error.WriteLine($"Content-first layout {(result.Applied ? "migration" : "dry run")}: {result.SourceFolders:N0} provenance folder(s), {result.Files:N0} file(s), {result.Bytes / (1024d * 1024 * 1024):0.##} GiB, {result.MovedFiles:N0} moved, {result.Conflicts:N0} conflict(s).{(result.Applied ? $"\nCatalog: {result.CatalogPath}" : "\nRun again with --apply after reviewing this result.")}");
        return result.Conflicts == 0 ? 0 : 3;
    }
    if (args is ["library-consolidate", var consolidateLibraryRoot, .. var consolidateOptions])
    {
        var apply = consolidateOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = consolidateOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown asset library-consolidate option: {unknown[0]}");
        var progress = new Progress<(long Done, long Total, string Path)>(value => Console.Error.WriteLine($"Consolidate\t{value.Done:N0}/{value.Total:N0}\t{value.Path}"));
        var result = BulkAssetLibraryService.ConsolidateLooseLayout(consolidateLibraryRoot, apply, progress);
        var catalogStatus = result.CatalogRebuildError is null
            ? result.Applied ? $"\nCatalog: {result.CatalogPath}" : string.Empty
            : $"\nCATALOG REBUILD FAILED after file consolidation committed: {result.CatalogRebuildError}\nRecover with: wowcrucible asset library-catalog \"{Path.GetFullPath(consolidateLibraryRoot)}\"";
        Console.Error.WriteLine($"Loose consolidation {(result.Applied ? "applied" : "dry run")}: {result.Files:N0} file(s), {result.Bytes / (1024d * 1024 * 1024):0.##} GiB, {result.MovedFiles:N0} move(s), {result.ExactDuplicates:N0} byte-identical duplicate(s), {result.Conflicts:N0} non-identical conflict(s).{(result.Applied ? $"\nJournal: {result.JournalPath}{catalogStatus}" : result.Conflicts == 0 ? "\nNo files changed. Run again with --apply after reviewing this result." : "\nNo files changed. Resolve every conflict before applying.")}");
        return result.Conflicts == 0 && result.CatalogRebuildError is null ? 0 : 3;
    }
    if (args is ["library-catalog", var catalogLibraryRoot])
    {
        var catalogPath = BulkAssetLibraryService.RebuildCatalog(catalogLibraryRoot);
        Console.Error.WriteLine($"Asset catalog rebuilt successfully: {catalogPath}");
        return 0;
    }
    if (args is ["library-status", var statusLibraryRoot])
    {
        var plan = BulkAssetLibraryService.LoadPlan(statusLibraryRoot);
        var checkpointPath = Path.Combine(Path.GetFullPath(statusLibraryRoot), "asset-library-checkpoint.json");
        var checkpoint = File.Exists(checkpointPath) ? System.Text.Json.JsonSerializer.Deserialize<BulkAssetLibraryCheckpoint>(File.ReadAllText(checkpointPath)) : null;
        Console.WriteLine($"Source\t{plan.SourceRoot}\nLibrary\t{plan.LibraryRoot}\nEligibleArchives\t{plan.Archives.Count(archive => archive.Eligible && archive.Error is null)}\nCompletedArchives\t{checkpoint?.CompletedArchiveIds.Count ?? 0}\nSkippedArchives\t{plan.Archives.Count(archive => !archive.Eligible)}\nArchiveEntries\t{plan.Archives.Sum(archive => archive.Entries)}\nBLPs\t{plan.LooseBlpFiles + plan.Archives.Sum(archive => archive.BlpFiles)}\nConvertedPNGs\t{checkpoint?.ConvertedPngFiles ?? 0}\nEntryOrArchiveFailures\t{checkpoint?.Failures.Count ?? 0}\nCheckpoint\t{(File.Exists(checkpointPath) ? checkpointPath : "not started")}");
        if (checkpoint is not null) foreach (var failure in checkpoint.Failures) Console.WriteLine($"FAILURE\t{failure.Key}\t{failure.Value}");
        return checkpoint?.Failures.Count > 0 ? 3 : 0;
    }
    if (args is ["compare-folders", var comparisonLibrary, .. var comparisonFilter])
    {
        var query = string.Join(' ', comparisonFilter); var index = AssetComparisonService.BuildIndex(comparisonLibrary);
        foreach (var directory in index.Directories.Where(directory => query.Length == 0 || directory.LogicalPath.Contains(query, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{directory.PngFiles}\t{directory.ProvenanceSources}\t{directory.LogicalPath}");
        Console.Error.WriteLine($"Indexed {index.TotalPngFiles:N0} PNGs in {index.Directories.Count:N0} content directories. Results are grouped by directory, never by filename."); return 0;
    }
    if (args is ["compare-files", var fileComparisonLibrary, var logicalDirectory])
    {
        var index = AssetComparisonService.BuildIndex(fileComparisonLibrary); var entries = AssetComparisonService.GetDirectoryPngs(index, logicalDirectory);
        foreach (var entry in entries) Console.WriteLine($"{entry.Provenance}\t{entry.FileName}\t{entry.Bytes}\t{entry.FullPath}");
        Console.Error.WriteLine($"Found {entries.Count:N0} direct PNG(s) from {entries.Select(entry => entry.Provenance).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} source(s) in '{logicalDirectory}'."); return 0;
    }
    if (args is ["models", var modelLibrary, var modelLogicalDirectory])
    {
        var index = AssetComparisonService.BuildIndex(modelLibrary); var discovery = AssetComparisonService.GetRelevantModels(index, modelLogicalDirectory);
        foreach (var model in discovery.Models) Console.WriteLine($"{model.Compatibility}\t{model.Version?.ToString() ?? "-"}\t{model.Provenance}\t{model.LogicalPath}\t{model.FileName}\t{model.SkinPath ?? "-"}\t{model.Status}");
        Console.Error.WriteLine($"Discovered {discovery.Models.Count:N0} M2 model(s), {discovery.Models.Count(model => model.Compatibility == AssetModelCompatibility.Ready):N0} ready, using nearest content scope '{discovery.DiscoveryScope}'."); return discovery.Models.Any(model => model.Compatibility == AssetModelCompatibility.Ready) ? 0 : 3;
    }
    if (args is ["definitive-status", var projectLibrary])
    {
        var projectPath = DefinitiveAssetProjectService.DefaultPath(projectLibrary); var project = DefinitiveAssetProjectService.LoadOrCreate(projectPath, projectLibrary);
        foreach (var group in project.Entries.GroupBy(entry => entry.GroupId)) { var first = group.First(); Console.WriteLine($"{first.Decision}\t{first.Category}\t{group.Count()}\t{first.Provenance}\t{first.ArchivePath}\t{first.Notes}"); }
        Console.Error.WriteLine($"Definitive Set: {project.Entries.Count:N0} file record(s) across {project.Entries.Select(entry => entry.GroupId).Distinct().Count():N0} decision group(s).\nProject: {projectPath}"); return 0;
    }
    if (args is ["definitive-stage", var stageLibrary, var definitiveOutput])
    {
        var projectPath = DefinitiveAssetProjectService.DefaultPath(stageLibrary); var project = DefinitiveAssetProjectService.LoadOrCreate(projectPath, stageLibrary); var result = DefinitiveAssetProjectService.StageKeepers(projectPath, project, definitiveOutput);
        Console.Error.WriteLine($"Staged {result.Files:N0} keeper file(s), {result.Bytes:N0} bytes.\nManifest: {result.ManifestPath}"); return 0;
    }
    if (args is ["dependency-graph", var dependencyLibrary, var dependencyRoot, .. var dependencyOptions])
    {
        var json = dependencyOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var onlyProblems = dependencyOptions.Contains("--only-problems", StringComparer.OrdinalIgnoreCase); var manifestPath = Option(dependencyOptions, "--manifest="); var outputMpq = Option(dependencyOptions, "--output-mpq=") ?? "patch-Crucible-Assets.MPQ"; var targetIndexPath = Option(dependencyOptions, "--target-index=");
        var unknown = dependencyOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--only-problems", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--manifest=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output-mpq=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--target-index=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--target-choice=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown dependency-graph option: {unknown[0]}");
        var targetChoices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in dependencyOptions.Where(option => option.StartsWith("--target-choice=", StringComparison.OrdinalIgnoreCase)))
        {
            var value = option["--target-choice=".Length..]; var separator = value.IndexOf('|'); if (separator <= 0 || separator == value.Length - 1) return Fail("--target-choice requires <client-path>|<archive-relative-path>.");
            targetChoices[PatchInputMapper.NormalizeArchivePath(value[..separator])] = PatchInputMapper.NormalizeArchivePath(value[(separator + 1)..]);
        }
        if (targetChoices.Count > 0 && targetIndexPath is null) return Fail("--target-choice requires --target-index.");
        var index = AssetComparisonService.BuildIndex(dependencyLibrary); var location = ClientAssetDependencyService.InferLocation(index, dependencyRoot); var target = targetIndexPath is null ? null : ClientEffectiveAssetCatalog.Load(targetIndexPath); var graph = ClientAssetDependencyService.Analyze(index, location, null, target, targetChoices);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(graph, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"ROOT\t{graph.Root.ClientPath}\t{graph.Root.Provenance}\t{graph.Root.SourcePath}\nNODES\t{graph.Nodes.Count}\nPATCH_FILES\t{graph.PatchEntries.Count}\nINHERITED_TARGET\t{graph.Inherited.Count}\nEXTERNAL_BINDINGS\t{graph.ExternalBindings.Count}\nBLOCKING\t{graph.Blocking.Count}");
            foreach (var node in graph.Nodes.Where(node => !onlyProblems || node.State is ClientAssetDependencyState.Missing or ClientAssetDependencyState.CrossSourceConflict or ClientAssetDependencyState.TargetAmbiguous or ClientAssetDependencyState.Invalid))
                Console.WriteLine($"{node.State.ToString().ToUpperInvariant()}\tdepth={node.Depth}\t{node.Kind}\t{node.ClientPath}\t{node.SourcePath ?? "-"}\t{node.Message}");
        }
        if (manifestPath is not null)
        {
            if (graph.Blocking.Count > 0) { Console.Error.WriteLine($"BLOCKED: Dependency closure has {graph.Blocking.Count:N0} blocking node(s); no manifest was written."); return 3; }
            if (graph.PatchEntries.Count == 0) { Console.Error.WriteLine($"NO PATCH NEEDED: all {graph.Inherited.Count:N0} dependency node(s) are supplied by the selected target client; no empty manifest was written."); return 0; }
            PatchManifestService.Save(manifestPath, Path.GetFileNameWithoutExtension(manifestPath), outputMpq, graph.PatchEntries, policy: new(ExpectedEntryCount: graph.PatchEntries.Count), targetClient: graph.TargetRequirement); Console.Error.WriteLine($"Wrote dependency-complete patch manifest with {graph.PatchEntries.Count:N0} file(s){(graph.TargetRequirement is null ? string.Empty : $", bound to target fingerprint {graph.TargetRequirement.IndexFingerprint} with {graph.TargetRequirement.InheritedAssets.Count:N0} inherited path(s)")}: {Path.GetFullPath(manifestPath)}");
        }
        return graph.Blocking.Count == 0 ? 0 : 3;
    }
    if (args is ["inspect", .. var inspectInputs] && inspectInputs.Length > 0)
    {
        foreach (var input in inspectInputs)
        {
            var inspection = NativeAssetConversionService.Inspect(input);
            Console.WriteLine($"{inspection.Compatibility}\t{inspection.Format}\t{inspection.Magic}\t{inspection.Version?.ToString() ?? "-"}\t{inspection.Size}\t{inspection.Path}");
            foreach (var finding in inspection.Findings) Console.WriteLine($"  {finding}");
            foreach (var dependency in inspection.Dependencies) Console.WriteLine($"  dependency\t{dependency.Kind}\t{dependency.Path}\t{dependency.Sha256}");
        }
        return 0;
    }
    if (args is ["m2-downport-plan", var downportPlanModelPath, .. var downportPlanOptions])
    {
        var json = downportPlanOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var skinPath = Option(downportPlanOptions, "--skin="); var listfilePath = Option(downportPlanOptions, "--listfile=");
        var unknown = downportPlanOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--skin=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown m2-downport-plan option: {unknown[0]}");
        var plan = StaticM2DownportService.Plan(downportPlanModelPath, skinPath, listfilePath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"READY\t{plan.Ready}\nMODEL\t{plan.SourceModelPath}\nMODEL_SHA256\t{plan.SourceModelSha256}\nSKIN\t{plan.SourceSkinPath ?? "<missing>"}\nLISTFILE\t{plan.SourceListfilePath ?? "<not required/supplied>"}\nSOURCE\tversion={plan.SourceVersion}\tflags=0x{plan.SourceFlags:X}\nOUTPUT\tversion=264\tflags=0x{plan.OutputFlags:X}\nGEOMETRY\tvertices={plan.VertexCount:N0}\ttriangles={plan.TriangleCount:N0}\tsubmeshes={plan.SubmeshCount:N0}\tmaterials={plan.MaterialCount:N0}\nANIMATION\tsequences={plan.AnimationSequenceCount:N0}\tglobal-clocks={plan.GlobalSequenceCount:N0}\nCONSTANT_COLOR_TRACKS\t{plan.ConstantColorTrackCount:N0}\nSHADOW_BATCHES\t{plan.ShadowBatchCount:N0}");
            foreach (var value in plan.ResolvedTexturePaths) Console.WriteLine($"TEXTURE_PATH\tindex={value.TextureIndex}\tfileDataId={value.FileDataId}\t{value.ClientPath}");
            foreach (var value in plan.Transformations) Console.WriteLine($"TRANSFORM\t{value}");
            foreach (var value in plan.Losses) Console.WriteLine($"LOSS\t{value}");
            foreach (var value in plan.Blockers) Console.WriteLine($"BLOCKER\t{value}");
        }
        return plan.Ready ? 0 : 3;
    }
    if (args.Length > 1 && args[0].Equals("m2-downport-scan", StringComparison.OrdinalIgnoreCase))
    {
        var scanOptions = args[1..].Where(value => value.StartsWith("--", StringComparison.Ordinal)).ToArray(); var scanInputs = args[1..].Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var json = scanOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var listfilePath = Option(scanOptions, "--listfile="); var unknown = scanOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown m2-downport-scan option: {unknown[0]}");
        if (scanInputs.Length == 0) return Fail("m2-downport-scan requires at least one M2 file or folder.");
        var scan = StaticM2DownportService.Scan(scanInputs, listfilePath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(scan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"FILES\t{scan.Entries.Count:N0}\nCONVERSION_READY\t{scan.Ready:N0}\nALREADY_WOTLK_335\t{scan.AlreadyWotlk335:N0}\nBLOCKED\t{scan.Blocked:N0}\nFAILED\t{scan.Failed:N0}");
            foreach (var entry in scan.Entries)
            {
                Console.WriteLine($"{entry.Status.ToString().ToUpperInvariant()}\t{entry.Path}");
                if (entry.Error is not null) Console.WriteLine($"  ERROR\t{entry.Error}");
                else if (entry.Plan is not null) foreach (var blocker in entry.Plan.Blockers) Console.WriteLine($"  BLOCKER\t{blocker}");
            }
        }
        return scan.Blocked == 0 && scan.Failed == 0 ? 0 : 3;
    }
    if (args is ["m2-downport-batch-plan", var batchSourceRoot, .. var batchPlanOptions])
    {
        var json = batchPlanOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var listfilePath = Option(batchPlanOptions, "--listfile="); var autoListfile = batchPlanOptions.Contains("--auto-listfile", StringComparer.OrdinalIgnoreCase);
        var unknown = batchPlanOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--auto-listfile", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown m2-downport-batch-plan option: {unknown[0]}");
        if (autoListfile && listfilePath is not null) return Fail("Use either --auto-listfile or --listfile=PATH, not both.");
        StaticM2BatchPlan plan;
        if (autoListfile)
        {
            var automatic = StaticM2BatchDownportService.PlanAuto(batchSourceRoot); plan = automatic.Plan;
            foreach (var finding in automatic.Discovery.Findings) Console.Error.WriteLine($"AUTO_LISTFILE\t{finding}");
        }
        else plan = StaticM2BatchDownportService.Plan(batchSourceRoot, listfilePath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"SOURCE_ROOT\t{plan.SourceRoot}\nLISTFILE\t{plan.SourceListfilePath ?? "<not selected>"}\nFINGERPRINT\t{plan.Fingerprint}\nFILES\t{plan.Entries.Count:N0}\nCONVERSION_READY\t{plan.Ready:N0}\nALREADY_WOTLK_335\t{plan.AlreadyWotlk335:N0}\nBLOCKED\t{plan.Blocked:N0}\nFAILED\t{plan.Failed:N0}");
            foreach (var entry in plan.Entries) { Console.WriteLine($"{entry.Status.ToString().ToUpperInvariant()}\t{entry.RelativePath}"); if (entry.Error is not null) Console.WriteLine($"  ERROR\t{entry.Error}"); else foreach (var blocker in entry.ModelPlan?.Blockers ?? []) Console.WriteLine($"  BLOCKER\t{blocker}"); }
        }
        return plan.Blocked == 0 && plan.Failed == 0 ? 0 : 3;
    }
    if (args is ["m2-downport-batch", var batchApplySourceRoot, var batchOutputRoot, .. var batchOptions])
    {
        var listfilePath = Option(batchOptions, "--listfile="); var autoListfile = batchOptions.Contains("--auto-listfile", StringComparer.OrdinalIgnoreCase); var readyOnly = batchOptions.Contains("--ready-only", StringComparer.OrdinalIgnoreCase); var workersText = Option(batchOptions, "--workers=") ?? "0";
        var unknown = batchOptions.Where(option => !option.Equals("--ready-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--auto-listfile", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown m2-downport-batch option: {unknown[0]}");
        if (autoListfile && listfilePath is not null) return Fail("Use either --auto-listfile or --listfile=PATH, not both.");
        if (!int.TryParse(workersText, out var workers) || workers is < 0 or > StaticM2BatchDownportService.MaximumWorkers) return Fail($"--workers must be 1 through {StaticM2BatchDownportService.MaximumWorkers}, or 0 for automatic.");
        StaticM2BatchPlan plan;
        if (autoListfile)
        {
            var automatic = StaticM2BatchDownportService.PlanAuto(batchApplySourceRoot); plan = automatic.Plan;
            foreach (var finding in automatic.Discovery.Findings) Console.Error.WriteLine($"AUTO_LISTFILE\t{finding}");
        }
        else plan = StaticM2BatchDownportService.Plan(batchApplySourceRoot, listfilePath);
        var result = StaticM2BatchDownportService.Convert(plan, batchOutputRoot, readyOnly, workers);
        Console.WriteLine($"OUTPUT\t{result.OutputDirectory}\nPAYLOAD\t{result.PayloadDirectory}\nRECEIPT\t{result.ReceiptPath}\nCONVERTED\t{result.Outputs.Count:N0}\nBLOCKED_RECORDED\t{result.Plan.Blocked:N0}\nFAILED_RECORDED\t{result.Plan.Failed:N0}\nWORKERS\t{result.Workers:N0}");
        foreach (var output in result.Outputs) Console.WriteLine($"MODEL\t{output.ModelRelativePath}\t{output.ModelSha256}\nSKIN\t{output.SkinRelativePath}\t{output.SkinSha256}");
        return result.Plan.Blocked == 0 && result.Plan.Failed == 0 ? 0 : 3;
    }
    if (args is ["m2-downport", var downportModelPath, var downportOutput, .. var downportOptions])
    {
        var skinPath = Option(downportOptions, "--skin="); var listfilePath = Option(downportOptions, "--listfile="); var unknown = downportOptions.Where(option => !option.StartsWith("--skin=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown m2-downport option: {unknown[0]}");
        var plan = StaticM2DownportService.Plan(downportModelPath, skinPath, listfilePath);
        if (!plan.Ready) { Console.Error.WriteLine("BLOCKED:\n- " + string.Join("\n- ", plan.Blockers)); return 3; }
        var result = StaticM2DownportService.Convert(plan, downportOutput);
        Console.WriteLine($"MODEL\t{result.OutputModelPath}\nMODEL_SHA256\t{result.OutputModelSha256}\nSKIN\t{result.OutputSkinPath}\nSKIN_SHA256\t{result.OutputSkinSha256}\nRECEIPT\t{result.ReceiptPath}\nVALIDATED\tvertices={result.ValidatedVertices:N0}\ttriangles={result.ValidatedTriangles:N0}\tsubmeshes={result.ValidatedSubmeshes:N0}\tmaterials={result.ValidatedMaterials:N0}");
        foreach (var loss in result.Plan.Losses) Console.WriteLine($"LOSS\t{loss}");
        return 0;
    }
    if (args is ["wmo-preview-info", var wmoPath, .. var wmoOptions])
    {
        var json = wmoOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var includeGroups = wmoOptions.Contains("--groups", StringComparer.OrdinalIgnoreCase); var contentRoot = Option(wmoOptions, "--content-root=");
        var unknown = wmoOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--groups", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--content-root=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown wmo-preview-info option: {unknown[0]}");
        var geometry = WmoPreviewGeometryService.Load(wmoPath); var textures = WmoPreviewGeometryService.ResolveTextureFiles(geometry, contentRoot);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Geometry = geometry, ResolvedTextureFiles = textures }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        else
        {
            Console.WriteLine($"Root\t{geometry.RootPath}\nVersion\t{geometry.Version}\nGroups\t{geometry.Groups.Count:N0}\nVertices\t{geometry.Vertices.Count:N0}\nTriangles\t{geometry.TriangleIndices.Count / 3:N0}\nMaterials\t{geometry.Materials.Count:N0}\nResolvedTextures\t{textures.Count:N0}\nMinimum\t{geometry.Minimum}\nMaximum\t{geometry.Maximum}");
            foreach (var material in geometry.Materials) Console.WriteLine($"MATERIAL\t{material.Index}\tshader={material.Shader}\tblend={material.BlendMode}\tflags=0x{material.Flags:X}\ttexture={material.Texture1 ?? "<none>"}\tresolved={textures.GetValueOrDefault(material.Index, "<missing>")}");
            foreach (var finding in geometry.Findings) Console.WriteLine($"FINDING\t{finding}");
            if (includeGroups) foreach (var group in geometry.Groups) { Console.WriteLine($"GROUP\t{group.Index:000}\tvertices={group.VertexCount:N0}\ttriangles={group.TriangleIndexCount / 3:N0}\tbatches={group.BatchCount:N0}\tflags=0x{group.Flags:X}\t{group.Path}"); foreach (var finding in group.Findings) Console.WriteLine($"GROUP_FINDING\t{group.Index:000}\t{finding}"); }
        }
        return geometry.Findings.Count == 0 ? 0 : 3;
    }
    if (args is ["path-candidates", var libraryPath, var clientPath, .. var candidateOptions])
    {
        var json = candidateOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var preferred = Option(candidateOptions, "--preferred=");
        var unknown = candidateOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--preferred=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown path-candidates option: {unknown[0]}");
        var index = ClientAssetDependencyService.OpenLibraryLayout(libraryPath); var candidates = ClientAssetDependencyService.FindCandidates(index, clientPath);
        var selected = !string.IsNullOrWhiteSpace(preferred)
            ? candidates.SingleOrDefault(value => value.Provenance.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            : candidates.Count == 1 ? candidates[0] : null;
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Library = index.LibraryRoot, ClientPath = PatchInputMapper.NormalizeArchivePath(clientPath), Preferred = preferred, Selected = selected, Candidates = candidates, RequiresExplicitChoice = selected is null && candidates.Count > 1 }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"ClientPath\t{PatchInputMapper.NormalizeArchivePath(clientPath)}\nCandidates\t{candidates.Count:N0}\nSelected\t{selected?.Provenance ?? "<none>"}");
            foreach (var candidate in candidates) Console.WriteLine($"CANDIDATE\t{candidate.Provenance}\t{candidate.SourcePath}");
            if (candidates.Count > 1 && selected is null) Console.WriteLine("FINDING\tMultiple provenance layers exist; use --preferred=<exact provenance> to select one explicitly.");
            else if (!string.IsNullOrWhiteSpace(preferred) && selected is null) Console.WriteLine($"FINDING\tPreferred provenance was not found: {preferred}");
        }
        return selected is not null ? 0 : 3;
    }
    if (args is ["model-export", var exportModelPath, var exportObjPath, .. var exportOptions])
    {
        var overwrite = exportOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var allGeosets = exportOptions.Contains("--all-geosets", StringComparer.OrdinalIgnoreCase); var naked = exportOptions.Contains("--naked", StringComparer.OrdinalIgnoreCase);
        var skinPath = Option(exportOptions, "--skin="); var animationText = Option(exportOptions, "--animation="); var timeText = Option(exportOptions, "--time="); var groupText = Option(exportOptions, "--groups=");
        var textureOptions = exportOptions.Where(option => option.StartsWith("--texture=", StringComparison.OrdinalIgnoreCase)).ToArray();
        var unknown = exportOptions.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--all-geosets", StringComparison.OrdinalIgnoreCase) && !option.Equals("--naked", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--skin=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--animation=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--time=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--groups=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--texture=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown model-export option: {unknown[0]}");
        if (allGeosets && (naked || groupText is not null)) return Fail("--all-geosets cannot be combined with --naked or --groups.");
        if (timeText is not null && animationText is null) return Fail("--time requires --animation=<sequence-index>.");
        var selectedGroups = naked ? new Dictionary<int, int>(M2GeosetCatalog.NakedCharacterSelection) : new Dictionary<int, int>();
        if (groupText is not null)
        {
            foreach (var token in groupText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = token.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var group) || !int.TryParse(parts[1], out var variant) || group < 0 || variant < 0 || variant > 99) return Fail($"Invalid geoset selection '{token}'; use group:variant with non-negative values and variants 0-99.");
                selectedGroups[group] = variant;
            }
        }
        var selection = selectedGroups.Count == 0 ? null : new M2GeosetSelection(selectedGroups, naked ? "naked CLI export preset" : "explicit CLI export selection");
        var geometry = M2PreviewGeometryService.Load(exportModelPath, skinPath, allGeosets ? M2PreviewVisibilityMode.AllGeosets : M2PreviewVisibilityMode.Automatic, selection);
        M2AnimationPose? pose = null;
        if (animationText is not null)
        {
            if (!int.TryParse(animationText, out var sequence) || sequence < 0 || sequence >= geometry.Sequences.Count) return Fail($"--animation must be a sequence index from 0 through {Math.Max(0, geometry.Sequences.Count - 1)}.");
            if (timeText is not null && (!double.TryParse(timeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedTime) || !double.IsFinite(parsedTime))) return Fail("--time must be a finite millisecond value.");
            var time = timeText is null ? 0d : double.Parse(timeText, System.Globalization.CultureInfo.InvariantCulture); pose = M2AnimationService.CreatePose(geometry); M2AnimationService.SampleInto(geometry, sequence, time, pose);
        }
        var textures = new Dictionary<int, RgbaTexture>();
        foreach (var option in textureOptions)
        {
            var value = option["--texture=".Length..]; var separator = value.IndexOf(':');
            if (separator <= 0 || !int.TryParse(value[..separator], out var slot) || slot < 0 || separator == value.Length - 1) return Fail($"Invalid texture binding '{value}'; use --texture=slot:path.blp.");
            textures[slot] = BlpTextureService.Decode(value[(separator + 1)..]);
        }
        var result = M2ObjExportService.Export(geometry, exportObjPath, pose, textures, overwrite);
        Console.WriteLine($"OBJ\t{result.ObjPath}\nMTL\t{result.MaterialPath}\nReceipt\t{result.ReceiptPath}\nVertices\t{result.Vertices:N0}\nTriangles\t{result.Triangles:N0}\nPosed\t{result.Posed}\nTextures\t{result.TexturePaths.Count:N0}");
        foreach (var texture in result.TexturePaths) Console.WriteLine($"TEXTURE\t{texture}");
        return 0;
    }
    if (args is ["creature-appearance-port-plan", var portSourceDbc, var portTargetDbc, var portDisplayText, .. var portPlanOptions])
    {
        var schema = Option(portPlanOptions, "--schema="); var output = Option(portPlanOptions, "--output="); var overwrite = portPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var json = portPlanOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = portPlanOptions.Where(option => !option.StartsWith("--schema=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown creature-appearance-port-plan option: {unknown[0]}");
        if (string.IsNullOrWhiteSpace(schema)) return Fail("creature-appearance-port-plan requires --schema=<WotLK-12340.xml>.");
        if (!uint.TryParse(portDisplayText, out var displayId) || displayId == 0) return Fail("Creature display ID must be a positive unsigned integer.");
        if (output is not null && File.Exists(output) && !overwrite) return Fail($"Plan already exists; use --overwrite to replace it: {Path.GetFullPath(output)}");
        var plan = CreatureAppearancePortService.CreatePlan(portSourceDbc, portTargetDbc, schema, displayId, CancellationToken.None);
        if (output is not null) CreatureAppearancePortService.SavePlan(output, plan);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"SOURCE_DBC\t{plan.SourceDbcRoot}\nTARGET_DBC\t{plan.TargetDbcRoot}\nSOURCE_DISPLAY\t{plan.SourceDisplayId}\nTARGET_DISPLAY\t{plan.TargetDisplayId}\nADD_ROWS\t{plan.AddedRows:N0}\nREUSE_ROWS\t{plan.ReusedRows:N0}\nCHANGED_TABLES\t{string.Join(',', plan.ChangedTables)}\nREQUIRED_ASSETS\t{plan.RequiredAssets.Count:N0}");
            foreach (var row in plan.Rows) Console.WriteLine($"ROW\t{row.Action}\t{row.Table}\t{row.SourceId}->{row.TargetId}\t{string.Join(',', row.ReferenceRewrites.Select(pair => $"{pair.Key}={pair.Value}"))}");
            foreach (var asset in plan.RequiredAssets) Console.WriteLine($"ASSET\t{asset.Kind}\t{asset.ClientPath}\t{asset.SourceTable}:{asset.SourceId}");
            foreach (var finding in plan.Findings) Console.WriteLine($"FINDING\t{finding}");
            if (output is not null) Console.WriteLine($"PLAN\t{Path.GetFullPath(output)}");
        }
        return 0;
    }
    if (args is ["npc-chr-plan", var chrPath, var chrTexture, var chrDbcRoot, var chrSchema, var chrHost, var chrPortText, var chrUser, var chrDatabase, var chrPlanPath, .. var chrPlanOptions])
    {
        var allowed = new[] { "--display-start=", "--extra-start=", "--sound=", "--scale=", "--alpha=", "--password-env=", "--ssl=", "--format=" };
        var unknown = chrPlanOptions.Where(option => !allowed.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown npc-chr-plan option: {unknown[0]}");
        if (!uint.TryParse(chrPortText, out var chrPort) || chrPort is 0 or > 65535) return Fail("Database port must be from 1 to 65535.");
        var passwordEnvironment = Option(chrPlanOptions, "--password-env=") ?? "WOW_CRUCIBLE_DB_PASSWORD"; var password = Environment.GetEnvironmentVariable(passwordEnvironment);
        if (password is null) return Fail($"Set the {passwordEnvironment} environment variable for item-template display resolution. Passwords are not accepted on the command line.");
        if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(Option(chrPlanOptions, "--ssl=") ?? "Preferred", true, out var ssl)) return Fail("Unknown MySQL SSL mode.");
        static uint? OptionalId(string[] values, string prefix) { var text = Option(values, prefix); if (text is null) return null; return uint.TryParse(text, out var parsed) && parsed > 0 ? parsed : throw new FormatException($"{prefix.TrimEnd('=')} must be a positive unsigned ID."); }
        var displayStart = OptionalId(chrPlanOptions, "--display-start="); var extraStart = OptionalId(chrPlanOptions, "--extra-start=");
        if (!uint.TryParse(Option(chrPlanOptions, "--sound=") ?? "0", out var sound)) return Fail("--sound must be an unsigned sound ID.");
        if (!float.TryParse(Option(chrPlanOptions, "--scale=") ?? "1", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) || !float.IsFinite(scale) || scale <= 0) return Fail("--scale must be finite and positive.");
        if (!uint.TryParse(Option(chrPlanOptions, "--alpha=") ?? "255", out var alpha) || alpha > 255) return Fail("--alpha must be 0 through 255.");
        var character = NpcChrAppearanceService.Parse(chrPath); var profile = new DatabaseConnectionProfile(chrHost, chrPort, chrUser, password, chrDatabase, ssl);
        var mapping = NpcChrAppearanceService.ResolveItemDisplaysAsync(profile, character.Equipment.ArmorSlots.Select(slot => slot.ItemEntry), CancellationToken.None).GetAwaiter().GetResult();
        var plan = NpcChrAppearanceService.CreatePlan(chrPath, chrTexture, chrDbcRoot, chrSchema, mapping, new(displayStart, extraStart, sound, scale, alpha), CancellationToken.None);
        var overwrite = chrPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); NpcChrAppearanceService.SavePlan(chrPlanPath, plan, overwrite);
        var json = chrPlanOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"READY\t{plan.Ready}\nMODEL\t{plan.Character.ModelPath}\tCreatureModelData={plan.ModelId}\nEXTRA\t{plan.ExtraId}\t{(plan.ReusesExtra ? "REUSE" : "ADD")}\nDISPLAY\t{plan.DisplayId}\t{(plan.ReusesDisplay ? "REUSE" : "ADD")}\nBAKED_TEXTURE\tTextures\\BakedNpcTextures\\{plan.BakedTextureName}\nADD_ROWS\t{plan.AddedRows}\nPLAN\t{Path.GetFullPath(chrPlanPath)}");
            foreach (var item in plan.ItemDisplays.Where(item => item.ItemEntry != 0)) Console.WriteLine($"ITEM\t{item.Slot}\tentry={item.ItemEntry}\tdisplay={item.ItemDisplayId}\tresolved={item.Resolved}");
            foreach (var pair in plan.WeaponItemEntries.Where(pair => pair.Value != 0)) Console.WriteLine($"EQUIPMENT_SOURCE\t{pair.Key}\titem={pair.Value}");
            foreach (var finding in plan.Findings) Console.WriteLine($"FINDING\t{finding}"); foreach (var blocker in plan.Blockers) Console.WriteLine($"BLOCKER\t{blocker}");
        }
        return plan.Ready ? 0 : 3;
    }
    if (args is ["npc-chr-apply", var chrApplyPlan, var chrOutput, .. var chrApplyOptions])
    {
        var json = chrApplyOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = chrApplyOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown npc-chr-apply option: {unknown[0]}");
        var result = NpcChrAppearanceService.Apply(NpcChrAppearanceService.LoadPlan(chrApplyPlan), chrOutput, CancellationToken.None);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else { Console.WriteLine($"OUTPUT\t{result.OutputDirectory}\nDISPLAY\t{result.Plan.DisplayId}\nEXTRA\t{result.Plan.ExtraId}\nBAKED_TEXTURE\t{result.BakedTexturePath}\nMANIFEST\t{result.ManifestPath}\nPATCH\t{result.PatchPath}\nRECEIPT\t{result.ReceiptPath}"); foreach (var pair in result.OutputDbcFiles) Console.WriteLine($"DBC\t{pair.Key}\t{pair.Value}\t{result.OutputSha256[pair.Key]}"); }
        return 0;
    }
    if (args is ["item-client-plan", var itemClientDbc, var itemClientDisplayDbc, var itemClientSchema, var itemClientHost, var itemClientPortText, var itemClientUser, var itemClientDatabase, var itemClientPlanPath, .. var itemClientOptions])
    {
        var allowed = new[] { "--password-env=", "--ssl=", "--format=" }; var unknown = itemClientOptions.Where(option => !allowed.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-client-plan option: {unknown[0]}");
        if (!uint.TryParse(itemClientPortText, out var port) || port is 0 or > 65535) return Fail("Database port must be from 1 to 65535.");
        var passwordEnvironment = Option(itemClientOptions, "--password-env=") ?? "WOW_CRUCIBLE_DB_PASSWORD"; var password = Environment.GetEnvironmentVariable(passwordEnvironment); if (password is null) return Fail($"Set the {passwordEnvironment} environment variable. Passwords are not accepted on the command line.");
        if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(Option(itemClientOptions, "--ssl=") ?? "Preferred", true, out var ssl)) return Fail("Unknown MySQL SSL mode.");
        var plan = ItemClientSyncService.CreatePlanAsync(itemClientDbc, itemClientDisplayDbc, itemClientSchema, new(itemClientHost, port, itemClientUser, password, itemClientDatabase, ssl), CancellationToken.None).GetAwaiter().GetResult();
        ItemClientSyncService.SavePlan(itemClientPlanPath, plan, itemClientOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase));
        if (itemClientOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"READY\t{plan.Ready}\nTARGET_ROWS\t{plan.TargetRowCount}\nSERVER_ROWS\t{plan.ServerRowCount}\nADD\t{plan.AddedRows}\nUPDATE\t{plan.UpdatedRows}\nPRESERVE_CLIENT_ONLY\t{plan.ClientOnlyRows.Count}\nMISSING_DISPLAYS\t{plan.MissingDisplayIds.Count}\nPLAN\t{Path.GetFullPath(itemClientPlanPath)}");
            foreach (var finding in plan.Findings) Console.WriteLine($"FINDING\t{finding}"); foreach (var blocker in plan.Blockers) Console.WriteLine($"BLOCKER\t{blocker}");
        }
        return plan.Ready ? 0 : 3;
    }
    if (args is ["item-client-apply", var itemClientApplyPlan, var itemClientOutput, .. var itemClientApplyOptions])
    {
        var unknown = itemClientApplyOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown item-client-apply option: {unknown[0]}");
        var result = ItemClientSyncService.Apply(ItemClientSyncService.LoadPlan(itemClientApplyPlan), itemClientOutput, CancellationToken.None);
        if (itemClientApplyOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"OUTPUT\t{result.OutputDirectory}\nITEM_DBC\t{result.ItemDbcPath}\t{result.ItemDbcSha256}\nWMV_CATALOG\t{result.WmvCatalogPath}\nMANIFEST\t{result.ManifestPath}\nPATCH\t{result.PatchPath}\nRECEIPT\t{result.ReceiptPath}");
        return 0;
    }
    if (args is ["creature-appearance-port-apply", var portPlanPath, var portOutputDirectory, .. var portApplyOptions])
    {
        var json = portApplyOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = portApplyOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown creature-appearance-port-apply option: {unknown[0]}");
        var plan = CreatureAppearancePortService.LoadPlan(portPlanPath); var result = CreatureAppearancePortService.Apply(plan, portOutputDirectory, CancellationToken.None);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"OUTPUT\t{result.OutputDirectory}\nTARGET_DISPLAY\t{result.TargetDisplayId}\nRECEIPT\t{result.ReceiptPath}");
            foreach (var pair in result.OutputFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) Console.WriteLine($"DBC\t{pair.Key}\t{pair.Value}\t{result.OutputSha256[pair.Key]}");
        }
        return 0;
    }
    if (args is ["creature-appearance-patch-plan", var portReceiptPath, var processedLibrary, .. var patchPlanOptions])
    {
        var patchProvenance = Option(patchPlanOptions, "--provenance="); var output = Option(patchPlanOptions, "--output="); var overwrite = patchPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var json = patchPlanOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = patchPlanOptions.Where(option => !option.StartsWith("--provenance=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown creature-appearance-patch-plan option: {unknown[0]}");
        if (output is not null && File.Exists(output) && !overwrite) return Fail($"Patch plan already exists; use --overwrite to replace it: {Path.GetFullPath(output)}");
        var result = CreatureAppearancePortService.LoadResult(portReceiptPath); var plan = CreatureAppearancePatchService.CreatePlan(result, processedLibrary, patchProvenance);
        if (output is not null) CreatureAppearancePatchService.SavePlan(output, plan);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"READY\t{plan.Ready}\nDISPLAY\t{plan.AppearancePlan.SourceDisplayId}->{plan.AppearancePlan.TargetDisplayId}\nPROVENANCE\t{plan.EffectiveProvenance ?? "<unbound>"}\nENTRIES\t{plan.Entries.Count:N0}\nASSETS\t{plan.Assets.Count:N0}\nBLOCKERS\t{plan.Blockers.Count:N0}");
            foreach (var entry in plan.Entries) Console.WriteLine($"ENTRY\t{entry.Kind}\t{entry.ArchivePath}\t{entry.SourcePath}\t{entry.Sha256}");
            foreach (var blocker in plan.Blockers) Console.WriteLine($"BLOCKER\t{blocker}");
            foreach (var finding in plan.Findings) Console.WriteLine($"FINDING\t{finding}");
            if (output is not null) Console.WriteLine($"PLAN\t{Path.GetFullPath(output)}");
        }
        return plan.Ready ? 0 : 3;
    }
    if (args is ["creature-appearance-patch-manifest", var appearancePatchPlanPath, var appearanceManifestPath, .. var patchManifestOptions])
    {
        var outputMpq = Option(patchManifestOptions, "--mpq=") ?? "patch-Crucible-Appearance.MPQ"; var overwrite = patchManifestOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var unknown = patchManifestOptions.Where(option => !option.StartsWith("--mpq=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown creature-appearance-patch-manifest option: {unknown[0]}");
        if (File.Exists(appearanceManifestPath) && !overwrite) return Fail($"Manifest already exists; use --overwrite to replace it: {Path.GetFullPath(appearanceManifestPath)}");
        var plan = CreatureAppearancePatchService.LoadPlan(appearancePatchPlanPath); var manifest = CreatureAppearancePatchService.ExportManifest(plan, appearanceManifestPath, outputMpq);
        Console.WriteLine($"MANIFEST\t{Path.GetFullPath(appearanceManifestPath)}\nOUTPUT_MPQ\t{manifest.OutputFileName}\nENTRIES\t{manifest.Entries.Count:N0}\nDISPLAY\t{plan.AppearancePlan.TargetDisplayId}");
        return 0;
    }
    if (args is ["creature-display-catalog", var creatureCatalogDbc, .. var creatureCatalogOptions])
    {
        var schema = Option(creatureCatalogOptions, "--schema="); var search = Option(creatureCatalogOptions, "--search="); var limitText = Option(creatureCatalogOptions, "--limit="); var json = creatureCatalogOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = creatureCatalogOptions.Where(option => !option.StartsWith("--schema=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--search=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown creature-display-catalog option: {unknown[0]}");
        if (!Directory.Exists(creatureCatalogDbc)) return Fail($"Creature DBC folder does not exist: {Path.GetFullPath(creatureCatalogDbc)}");
        if (schema is not null && !File.Exists(schema)) return Fail($"Creature appearance schema does not exist: {Path.GetFullPath(schema)}");
        if (limitText is not null && (!int.TryParse(limitText, out var parsedLimit) || parsedLimit < 0)) return Fail("--limit must be zero (all rows) or a positive integer.");
        var limit = limitText is null ? 0 : int.Parse(limitText, System.Globalization.CultureInfo.InvariantCulture); var catalog = new CreatureDisplayPreviewService().LoadCatalog(creatureCatalogDbc, schema);
        var matches = catalog.Entries.Where(entry => entry.Matches(search)).ToArray(); var selected = limit == 0 ? matches : matches.Take(limit).ToArray();
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Catalog = catalog with { Entries = Array.Empty<CreatureDisplayCatalogEntry>() }, Search = search, Matches = matches.Length, Returned = selected.Length, Entries = selected }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"DBC_ROOT\t{catalog.DbcRoot}\nTOTAL\t{catalog.Entries.Count:N0}\nUSABLE\t{catalog.UsableEntries:N0}\nMISSING_MODEL\t{catalog.MissingModelEntries:N0}\nINVALID\t{catalog.InvalidEntries:N0}\nMATCHES\t{matches.Length:N0}\nRETURNED\t{selected.Length:N0}");
            foreach (var entry in selected) Console.WriteLine($"DISPLAY\t{entry.DisplayId}\tmodel={entry.ModelId}\tpath={entry.ModelClientPath}\tdisplay-scale={entry.DisplayScale:0.###}\tmodel-scale={entry.ModelScale:0.###}\ttextures={string.Join('|', entry.TextureVariations)}\t{(entry.Usable ? "USABLE" : entry.Finding)}");
        }
        return matches.Length == 0 ? 3 : 0;
    }
    if (args is ["creature-appearances", var creatureModelClientPath, .. var creatureAppearanceOptions])
    {
        var dbc = Option(creatureAppearanceOptions, "--dbc="); var schema = Option(creatureAppearanceOptions, "--schema="); var library = Option(creatureAppearanceOptions, "--library="); var sourceProvenance = Option(creatureAppearanceOptions, "--provenance="); var json = creatureAppearanceOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = creatureAppearanceOptions.Where(option => !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--schema=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--library=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--provenance=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown creature-appearances option: {unknown[0]}");
        if ((string.IsNullOrWhiteSpace(dbc) || !Directory.Exists(dbc)) && string.IsNullOrWhiteSpace(sourceProvenance)) return Fail("--dbc must point to the target/server DBC folder unless --library and --provenance select an extracted source layer.");
        if (schema is not null && !File.Exists(schema)) return Fail($"Creature appearance schema does not exist: {Path.GetFullPath(schema)}");
        if (library is not null && !Directory.Exists(library)) return Fail($"Processed asset library does not exist: {Path.GetFullPath(library)}");
        if (!string.IsNullOrWhiteSpace(sourceProvenance) && string.IsNullOrWhiteSpace(library)) return Fail("--provenance requires --library so its exact source DBC and model layer can be resolved.");
        var service = new CreatureDisplayPreviewService();
        var lookup = string.IsNullOrWhiteSpace(sourceProvenance)
            ? new CreatureModelDisplayLookup(service.ResolveModelDisplays(dbc!, schema, creatureModelClientPath, library, CancellationToken.None), Path.GetFullPath(dbc!), "Resolved from the configured target/server DBCs.")
            : service.ResolveModelDisplaysForProvenance(dbc, schema, creatureModelClientPath, library, sourceProvenance, CancellationToken.None);
        var displays = lookup.Displays;
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(displays, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else foreach (var display in displays)
        {
            Console.WriteLine($"DISPLAY\t{display.DisplayId}\tmodel={display.ModelId}\tpath={display.ModelClientPath}\tdisplay-scale={display.DisplayScale:0.###}\tmodel-scale={display.ModelScale:0.###}\ttextures={string.Join('|', display.TextureVariations)}");
            foreach (var source in display.Sources) Console.WriteLine($"SOURCE\tdisplay={display.DisplayId}\t{(source.Ready ? "READY" : "MISSING_SKIN")}\t{source.Provenance}\t{source.ModelPath}\t{source.SkinPath}\ttextures={source.CreatureTextures.Count}");
        }
        var ready = displays.Sum(display => display.Sources.Count(source => source.Ready)); Console.Error.WriteLine($"{lookup.Finding} Resolved {displays.Count:N0} CreatureDisplayInfo appearance(s) and {ready:N0} ready same-provenance source(s) for {PatchInputMapper.NormalizeArchivePath(creatureModelClientPath)}.");
        return displays.Count == 0 || library is not null && ready == 0 ? 3 : 0;
    }
    if (args is ["preview-info", var previewModelPath, .. var previewOptions])
    {
        var known = previewOptions.Where(option => option.Equals("--all-geosets", StringComparison.OrdinalIgnoreCase) || option.Equals("--naked", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--skin=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--groups=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--hair=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--facial-hair=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--animation=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--time=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (known.Length != previewOptions.Length) return Fail($"Unknown preview-info option: {previewOptions.Except(known).First()}");
        var allGeosets = previewOptions.Contains("--all-geosets", StringComparer.OrdinalIgnoreCase); var naked = previewOptions.Contains("--naked", StringComparer.OrdinalIgnoreCase); var groupText = Option(previewOptions, "--groups=");
        if (allGeosets && (naked || groupText is not null)) return Fail("--all-geosets cannot be combined with --naked or --groups because it intentionally shows every variant.");
        var mode = allGeosets ? M2PreviewVisibilityMode.AllGeosets : M2PreviewVisibilityMode.Automatic;
        var dbcFolder = Option(previewOptions, "--dbc="); var hairText = Option(previewOptions, "--hair="); var facialText = Option(previewOptions, "--facial-hair=");
        if (naked && (hairText is not null || facialText is not null)) return Fail("--naked cannot be combined with --hair or --facial-hair.");
        var selectedGroups = naked ? new Dictionary<int, int>(M2GeosetCatalog.NakedCharacterSelection) : new Dictionary<int, int>(); var selectionSource = naked ? "naked character preset" : string.Empty;
        if (hairText is not null || facialText is not null)
        {
            if (dbcFolder is null) return Fail("--hair and --facial-hair require --dbc=<folder> so Crucible can resolve exact build-12340 geosets.");
            var identity = CharacterAppearanceService.Infer(Path.GetDirectoryName(Path.GetFullPath(previewModelPath)) ?? string.Empty, Path.GetFileName(previewModelPath))
                ?? throw new InvalidDataException("The model path/name does not identify a supported playable race and sex.");
            var plan = CharacterAppearanceService.ResolveGeosets(dbcFolder, identity, ParseVariation(hairText), ParseVariation(facialText));
            foreach (var pair in plan.GroupVariants) selectedGroups[pair.Key] = pair.Value; selectionSource = "CharHairGeosets.dbc + CharacterFacialHairStyles.dbc";
            foreach (var warning in plan.Warnings) Console.Error.WriteLine($"WARNING: {warning}");
        }
        if (groupText is not null)
        {
            foreach (var pair in ParseGroups(groupText)) selectedGroups[pair.Key] = pair.Value;
            selectionSource = selectionSource.Length == 0 ? "explicit CLI group selection" : selectionSource + " + explicit CLI overrides";
        }
        var selection = selectedGroups.Count == 0 ? null : new M2GeosetSelection(selectedGroups, selectionSource);
        var geometry = M2PreviewGeometryService.Load(previewModelPath, Option(previewOptions, "--skin="), mode, selection);
        Console.WriteLine($"Model\t{geometry.ModelPath}\nSkin\t{geometry.SkinPath}\nVertices\t{geometry.Vertices.Count:N0}\nBones\t{geometry.Bones.Count:N0}\nAttachments\t{geometry.Attachments.Count:N0}\nCameras\t{geometry.Cameras.Count:N0}\nLights\t{geometry.Lights.Count:N0}\nParticle emitters\t{geometry.ParticleEmitters.Count:N0}\nRibbon emitters\t{geometry.RibbonEmitters.Count:N0}\nGeosets\t{geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} ({geometry.VisibilityMode})\nTriangles\t{geometry.TriangleIndices.Count / 3:N0}/{geometry.TotalTriangleIndices / 3:N0}\nMinimum\t{geometry.Minimum}\nMaximum\t{geometry.Maximum}");
        if (geometry.GeosetSelection is not null) Console.WriteLine($"GEOSET_SELECTION\t{geometry.GeosetSelection.Source}\t{string.Join(",", geometry.GeosetSelection.GroupVariants.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}");
        foreach (var finding in geometry.GeosetSelectionFindings) Console.WriteLine($"GEOSET_SELECTION_RESULT\tgroup={finding.Group}\tname={finding.GroupName}\trequested={finding.RequestedVariant}\tgeoset={finding.RequestedGeoset}\tavailable={(finding.AvailableVariants.Count == 0 ? "<none>" : string.Join(',', finding.AvailableVariants))}\tmatching={finding.MatchingSubmeshes}\tapplied={finding.Applied}\tmissing={finding.Missing}");
        foreach (var group in M2GeosetCatalog.Describe(geometry.Submeshes)) Console.WriteLine($"GEOSET_GROUP\t{group.Group}\t{group.Name}\tvariants={string.Join(',', group.Variants.Select(variant => variant.Variant))}\tvisible={string.Join(',', group.Variants.Where(variant => variant.Visible).Select(variant => variant.Variant))}");
        foreach (var section in geometry.Submeshes) Console.WriteLine($"SUBMESH\t{section.Index}\tgeoset={section.GeosetId}\tgroup={section.GeosetGroup}:{section.GeosetGroupName}\tvariant={section.GeosetVariant}\tvisible={section.Visible}\ttriangles={section.TriangleIndexCount / 3}");
        foreach (var slot in geometry.TextureSlots) Console.WriteLine($"TEXTURE\t{slot.Index}\t{slot.Type}\t{slot.Flags}\t{slot.EmbeddedPath ?? "<external appearance binding>"}");
        foreach (var renderFlag in geometry.RenderFlags) Console.WriteLine($"RENDER_FLAG\t{renderFlag.Index}\tflags=0x{renderFlag.Flags:X}\tblend={renderFlag.BlendMode}\tunlit={renderFlag.Unlit}\ttwo-sided={renderFlag.TwoSided}");
        foreach (var material in geometry.MaterialUnits)
        {
            Console.WriteLine($"MATERIAL\t{material.Index}\tsubmesh={material.SubmeshIndex}\tshader={material.ShaderId}\trender-flag={material.RenderFlagsIndex}\tlookup={material.TextureLookupIndex}\ttexture={(material.TextureDefinitionIndex < 0 ? "<unresolved>" : material.TextureDefinitionIndex)}\tpasses={material.TextureCount}\tcombiner={material.Combiner.Name}\tsupported={material.Combiner.Supported}\texact={material.Combiner.Exact}");
            foreach (var stage in material.TextureStages) Console.WriteLine($"TEXTURE_STAGE\tmaterial={material.Index}\tstage={stage.StageIndex}\tlookup={stage.TextureLookupIndex}\ttexture={(stage.TextureDefinitionIndex < 0 ? "<unresolved>" : stage.TextureDefinitionIndex)}\tuv={stage.CoordinateSource}({stage.TextureCoordinateLookup})\tblend={stage.Blend}\tweight={stage.TransparencyDefinitionIndex?.ToString() ?? "<none>"}\ttransform={stage.TextureAnimationDefinitionIndex?.ToString() ?? "<none>"}");
        }
        foreach (var batch in geometry.Batches) Console.WriteLine($"BATCH\t{submeshLabel(batch)}\tindices={batch.TriangleStart}+{batch.TriangleIndexCount}\tmaterial={batch.MaterialUnitIndex?.ToString() ?? "<none>"}\ttexture={batch.TextureDefinitionIndex?.ToString() ?? "<none>"}\tblend={batch.BlendMode}\tflags=0x{batch.RenderFlags:X}");
        foreach (var attachment in geometry.Attachments) Console.WriteLine($"ATTACHMENT\trecord={attachment.Index}\tid={attachment.Id}\t{attachment.Name}\tbone={attachment.BoneIndex}\tposition={attachment.Position.X:R},{attachment.Position.Y:R},{attachment.Position.Z:R}\tlookup={(attachment.LookupSlots.Count == 0 ? "<none>" : string.Join(',', attachment.LookupSlots))}");
        foreach (var camera in geometry.Cameras) Console.WriteLine($"CAMERA\trecord={camera.Index}\ttype={camera.Type}\t{camera.Name}\tfov-raw={camera.FieldOfViewRaw:R}\tfov-degrees={camera.FieldOfViewDegrees:R}\tnear={camera.NearClip:R}\tfar={camera.FarClip:R}\tposition={camera.BasePosition.X:R},{camera.BasePosition.Y:R},{camera.BasePosition.Z:R}\ttarget={camera.BaseTarget.X:R},{camera.BaseTarget.Y:R},{camera.BaseTarget.Z:R}\tlookup={(camera.LookupSlots.Count == 0 ? "<none>" : string.Join(',', camera.LookupSlots))}");
        foreach (var light in geometry.Lights) Console.WriteLine($"LIGHT\trecord={light.Index}\ttype={light.Type}\t{light.Name}\tbone={light.BoneIndex}\tposition={light.Position.X:R},{light.Position.Y:R},{light.Position.Z:R}");
        foreach (var emitter in geometry.ParticleEmitters) Console.WriteLine($"PARTICLE_EMITTER\trecord={emitter.Index}\ttype={emitter.EmitterType}\t{emitter.EmitterName}\tbone={emitter.BoneIndex}\ttexture={emitter.TextureDefinitionIndex}\ttextures={string.Join(',', emitter.TextureDefinitionIndices)}\tblend={emitter.BlendMode}\tsheet={emitter.Columns}x{emitter.Rows}\tflags=0x{emitter.Flags:X8}\tposition={emitter.Position.X:R},{emitter.Position.Y:R},{emitter.Position.Z:R}");
        foreach (var emitter in geometry.RibbonEmitters) Console.WriteLine($"RIBBON_EMITTER\trecord={emitter.Index}\tbone={emitter.BoneIndex}\ttexture={emitter.TextureDefinitionIndex}\trender-flags={emitter.RenderFlagsIndex}\tblend={emitter.BlendMode}\tedges-per-second={emitter.EdgesPerSecond:R}\tlifetime={emitter.EdgeLifetimeSeconds:R}\tangle={emitter.EmissionAngle:R}\tposition={emitter.Position.X:R},{emitter.Position.Y:R},{emitter.Position.Z:R}");
        foreach (var sequence in geometry.Sequences) Console.WriteLine($"SEQUENCE\tindex={sequence.Index}\tid={sequence.AnimationId}:{sequence.SubAnimationId}\tduration={sequence.DurationMilliseconds}\tflags=0x{sequence.Flags:X}\talias={(sequence.IsAlias ? sequence.AliasSequence : "<none>")}");
        var animationText = Option(previewOptions, "--animation="); var timeText = Option(previewOptions, "--time=");
        if (timeText is not null && animationText is null) return Fail("--time requires --animation=<sequence-index>.");
        if (animationText is not null)
        {
            if (!int.TryParse(animationText, out var animationIndex) || animationIndex < 0 || animationIndex >= geometry.Sequences.Count) return Fail($"--animation must be a sequence index from 0 through {Math.Max(0, geometry.Sequences.Count - 1)}.");
            if (timeText is not null && (!double.TryParse(timeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedTime) || !double.IsFinite(parsedTime))) return Fail("--time must be a finite millisecond value.");
            var time = timeText is null ? 0d : double.Parse(timeText, System.Globalization.CultureInfo.InvariantCulture); var pose = M2AnimationService.CreatePose(geometry);
            M2AnimationService.SampleInto(geometry, animationIndex, time, pose);
            Console.WriteLine($"POSE\tsequence={animationIndex}\ttime={pose.TimeMilliseconds:R}\tminimum={pose.Minimum.X:R},{pose.Minimum.Y:R},{pose.Minimum.Z:R}\tmaximum={pose.Maximum.X:R},{pose.Maximum.Y:R},{pose.Maximum.Z:R}\tvertices={pose.Vertices.Length}\tbones={pose.BoneTransforms.Length}");
            for (var index = 0; index < pose.Cameras.Length; index++) Console.WriteLine($"CAMERA_POSE\trecord={index}\tposition={pose.Cameras[index].Position.X:R},{pose.Cameras[index].Position.Y:R},{pose.Cameras[index].Position.Z:R}\ttarget={pose.Cameras[index].Target.X:R},{pose.Cameras[index].Target.Y:R},{pose.Cameras[index].Target.Z:R}\troll={pose.Cameras[index].RollRadians:R}");
            for (var index = 0; index < pose.Lights.Length; index++) Console.WriteLine($"LIGHT_POSE\trecord={index}\tposition={pose.Lights[index].Position.X:R},{pose.Lights[index].Position.Y:R},{pose.Lights[index].Position.Z:R}\tambient={pose.Lights[index].AmbientColor.X:R},{pose.Lights[index].AmbientColor.Y:R},{pose.Lights[index].AmbientColor.Z:R}*{pose.Lights[index].AmbientIntensity:R}\tdiffuse={pose.Lights[index].DiffuseColor.X:R},{pose.Lights[index].DiffuseColor.Y:R},{pose.Lights[index].DiffuseColor.Z:R}*{pose.Lights[index].DiffuseIntensity:R}\tattenuation={pose.Lights[index].AttenuationStart:R}..{pose.Lights[index].AttenuationEnd:R}:{pose.Lights[index].UseAttenuation}");
            var sprites = M2ParticlePreviewService.BuildSprites(geometry, pose);
            Console.WriteLine($"PARTICLE_POSE\tsprites={sprites.Count:N0}\temitters={sprites.Select(sprite => sprite.EmitterIndex).Distinct().Count():N0}\tcap=2,000");
            foreach (var sprite in sprites.Take(16)) Console.WriteLine($"PARTICLE_SPRITE\temitter={sprite.EmitterIndex}\ttexture={sprite.TextureDefinitionIndex}\ttile={sprite.TileIndex}\tposition={sprite.Position.X:R},{sprite.Position.Y:R},{sprite.Position.Z:R}\tsize={sprite.Size:R}\tcolor={sprite.Color.X:R},{sprite.Color.Y:R},{sprite.Color.Z:R},{sprite.Color.W:R}");
            var trails = M2RibbonPreviewService.BuildTrails(geometry, pose);
            Console.WriteLine($"RIBBON_POSE\ttrails={trails.Count:N0}\tsections={trails.Sum(trail => trail.Sections.Count):N0}\tcap=2,048");
            foreach (var trail in trails.Take(8)) Console.WriteLine($"RIBBON_TRAIL\temitter={trail.EmitterIndex}\ttexture={trail.TextureDefinitionIndex}\tblend={trail.BlendMode}\tsections={trail.Sections.Count}\tcolor={trail.Color.X:R},{trail.Color.Y:R},{trail.Color.Z:R},{trail.Color.W:R}\tstart={trail.Sections[0].Center.X:R},{trail.Sections[0].Center.Y:R},{trail.Sections[0].Center.Z:R}\tend={trail.Sections[^1].Center.X:R},{trail.Sections[^1].Center.Y:R},{trail.Sections[^1].Center.Z:R}");
        }
        return 0;

        static string submeshLabel(M2PreviewBatch batch) => $"submesh={batch.SubmeshIndex},geoset={batch.GeosetId}";
        static uint? ParseVariation(string? value) => value is null ? null : uint.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"Invalid non-negative appearance variation: {value}");
        static IReadOnlyDictionary<int, int> ParseGroups(string value)
        {
            var result = new Dictionary<int, int>();
            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = token.Split(':', StringSplitOptions.TrimEntries); if (parts.Length != 2 || !int.TryParse(parts[0], out var group) || !int.TryParse(parts[1], out var variant) || group is < 0 or > 655 || variant is < 0 or > 99)
                    throw new ArgumentException($"Invalid geoset group selection '{token}'. Use group:variant with group 0..655 and variant 0..99; variant 0 hides that group.");
                result[group] = variant;
            }
            if (result.Count == 0) throw new ArgumentException("--groups requires at least one group:variant selection.");
            return result;
        }
    }
    if (args is ["appearance-info", var charSectionsPath, var logicalPath, var modelFile])
    {
        var identity = CharacterAppearanceService.Infer(logicalPath, modelFile) ?? throw new InvalidDataException("The logical path/model name does not identify a supported playable race and sex.");
        var skins = CharacterAppearanceService.LoadBaseSkins(charSectionsPath, identity);
        Console.WriteLine($"Character\t{identity.RaceName}\t{identity.SexName}\tRaceID={identity.RaceId}\tSexID={identity.SexId}\nBaseSkins\t{skins.Count:N0}");
        foreach (var skin in skins) Console.WriteLine($"SKIN\t{skin.Id}\tvariation={skin.VariationIndex}\tcolor={skin.ColorIndex}\tflags=0x{skin.Flags:X}\t{skin.TexturePath}");
        return 0;
    }
    if (args is ["appearance-render", var appearanceLibrary, var appearanceDbcFolder, var appearanceLogicalPath, var appearanceModelFile, var appearanceOutput, .. var appearanceOptions])
    {
        var skinId = UIntOption(appearanceOptions,"--skin="); var faceId = UIntOption(appearanceOptions,"--face="); var facialId = UIntOption(appearanceOptions,"--facial-hair="); var hairId = UIntOption(appearanceOptions,"--hair=");
        var requestedSource=Option(appearanceOptions,"--source=");var hairOutput=Option(appearanceOptions,"--hair-output=");
        var unknown=appearanceOptions.Where(option=>!option.StartsWith("--skin=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--face=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--facial-hair=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--hair=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--source=",StringComparison.OrdinalIgnoreCase)&&!option.StartsWith("--hair-output=",StringComparison.OrdinalIgnoreCase)&&!option.Equals("--overwrite",StringComparison.OrdinalIgnoreCase)).ToArray();if(unknown.Length>0)throw new ArgumentException($"Unknown appearance-render option: {unknown[0]}");
        var identity=CharacterAppearanceService.Infer(appearanceLogicalPath,appearanceModelFile)??throw new InvalidDataException("The logical path/model name does not identify a supported playable race and sex.");var index=AssetComparisonService.BuildIndex(appearanceLibrary);
        var plan=CharacterAppearancePreviewService.Build(index,appearanceDbcFolder,identity,skinId,faceId,facialId,hairId);if(requestedSource is not null){var source=plan.Sources.FirstOrDefault(item=>item.Provenance.Equals(requestedSource,StringComparison.OrdinalIgnoreCase)||item.FullPath.Equals(requestedSource,StringComparison.OrdinalIgnoreCase))??throw new KeyNotFoundException($"Appearance source '{requestedSource}' was not found. Available: {string.Join(", ",plan.Sources.Select(item=>item.Provenance))}");plan=CharacterAppearancePreviewService.Build(index,appearanceDbcFolder,identity,skinId,faceId,facialId,hairId,source.FullPath);}
        if(plan.SelectedSource is null)throw new InvalidOperationException($"Choose --source from: {string.Join(", ",plan.Sources.Select(item=>item.Provenance))}");var composed=CharacterAppearancePreviewService.Compose(index,plan);var overwrite=appearanceOptions.Contains("--overwrite",StringComparer.OrdinalIgnoreCase);BlpTextureService.WritePng(appearanceOutput,composed.Body,overwrite);if(hairOutput is not null&&composed.Hair is not null)BlpTextureService.WritePng(hairOutput,composed.Hair,overwrite);
        Console.WriteLine($"CHARACTER\t{identity.RaceName} {identity.SexName}\nSOURCE\t{plan.SelectedSource.Provenance}\nBODY\t{Path.GetFullPath(appearanceOutput)}\nHAIR\t{(hairOutput is null||composed.Hair is null?"not written":Path.GetFullPath(hairOutput))}\nMISSING\t{string.Join(",",composed.Missing)}\nGEOSETS\t{string.Join(",",plan.Geosets.GroupVariants.Select(pair=>$"{pair.Key}:{pair.Value}"))}");return 0;

        static uint? UIntOption(string[] options,string prefix){var value=Option(options,prefix);if(value is null)return null;return uint.TryParse(value,out var parsed)?parsed:throw new ArgumentException($"{prefix.TrimEnd('=')} requires an unsigned integer.");}
    }
    if (args is ["appearance-compose", var baseTexturePath, var outputPng, .. var composeOptions])
    {
        var knownPrefixes = new[] { "--torso=", "--pelvis=", "--face-upper=", "--face-lower=", "--facial-upper=", "--facial-lower=", "--scalp-upper=", "--scalp-lower=" };
        var unknown = composeOptions.Where(option => !knownPrefixes.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) throw new ArgumentException($"Unknown appearance-compose option(s): {string.Join(", ", unknown)}");
        var layers = new List<CharacterTextureLayer>();
        Add("--torso=", CharacterTextureRegion.Torso); Add("--pelvis=", CharacterTextureRegion.Pelvis); Add("--face-upper=", CharacterTextureRegion.FaceUpper); Add("--face-lower=", CharacterTextureRegion.FaceLower);
        Add("--facial-upper=", CharacterTextureRegion.FaceUpper); Add("--facial-lower=", CharacterTextureRegion.FaceLower); Add("--scalp-upper=", CharacterTextureRegion.FaceUpper); Add("--scalp-lower=", CharacterTextureRegion.FaceLower);
        var composed = CharacterTextureComposer.Compose(BlpTextureService.Decode(baseTexturePath), layers);
        BlpTextureService.WritePng(outputPng, composed, composeOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase));
        Console.WriteLine($"Composed\t{composed.Width}x{composed.Height}\t{layers.Count:N0} layer(s)\t{Path.GetFullPath(outputPng)}");
        return 0;
        void Add(string prefix, CharacterTextureRegion region) { var path = Option(composeOptions, prefix); if (path is not null) layers.Add(new(BlpTextureService.Decode(path), region)); }
    }
    if (args is ["workspace", var outputRoot, .. var workspaceInputs] && workspaceInputs.Length > 0)
    {
        var workspace = NativeAssetConversionService.CreateWorkspace(workspaceInputs, outputRoot);
        Console.Error.WriteLine($"Created native conversion workspace: {workspace.RootPath}\nAlready compatible: {workspace.CompatibleAssets:N0}\nRequire conversion: {workspace.ConversionRequired:N0}\nBlocked/invalid: {workspace.BlockedAssets:N0}\nReport: {Path.Combine(workspace.RootPath, "conversion-report.json")}");
        return workspace.BlockedAssets == 0 ? 0 : 3;
    }
    return AssetHelp(2);

    static void PrintTextureConsumerBuild(TextureConsumerIndexBuildResult result)
    {
        var summary = result.Summary;
        Console.WriteLine($"INDEX\t{summary.IndexPath}\nCATALOG\t{summary.CatalogPath}\nCATALOG_ROWS\t{summary.CatalogRows:N0}\nELIGIBLE_ASSETS\t{summary.EligibleAssets:N0}\nINDEXED_ASSETS\t{summary.IndexedAssets:N0}\nUPDATED_ASSETS\t{result.UpdatedAssets:N0}\nUNCHANGED_ASSETS\t{result.UnchangedAssets:N0}\nREMOVED_ASSETS\t{result.RemovedAssets:N0}\nUNSUPPORTED_ASSETS\t{summary.UnsupportedAssets:N0}\nINVALID_ASSETS\t{summary.InvalidAssets:N0}\nMISSING_ASSETS\t{summary.MissingAssets:N0}\nCATALOG_ISSUES\t{summary.CatalogIssues:N0}\nTEXTURE_REFERENCES\t{summary.TextureReferences:N0}\nCOVERAGE_COMPLETE\t{summary.CoverageComplete}\nDURATION_MS\t{result.DurationMilliseconds:0.###}");
    }
}

static int AssetHelp(int code = 0) => GroupHelp("""
Usage:
  wowcrucible asset texture-consumers-build <processed-library> [--format=text|json]
  wowcrucible asset texture-consumers <processed-library> <texture.blp|client-path> [--refresh] [--dbc=folder] [--schema=file] [--server=installed-server] [--format=text|json]
  wowcrucible asset texture-info <file.blp>
  wowcrucible asset texture-decode <file.blp> <output.png> [--mip=N] [--overwrite]
  wowcrucible asset texture-proof <input.blp|image> [--mip=N] [--codec=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--amplify=N] [--difference=output.png] [--preview=output.png] [--max-rgb-mae=N] [--max-alpha-mae=N] [--max-alpha-crossings=N] [--report=text|json] [--overwrite]
  wowcrucible asset texture-compose <output.png|blp> <bottom-source> [higher-sources...] [--layer="path|blend|opacity|x|y|visible"] [--width=N] [--height=N] [--background=R:G:B:A] [--codec=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--report=text|json] [--overwrite]
  wowcrucible asset texture-mask <source.blp|image> <mask.blp|image> <output.png|blp> [--source-mip=N] [--mask-mip=N] [--mask=alpha|luminance|red|green|blue] [--invert-mask] [--strength=0..1] [--scale=R:G:B:A] [--offset=R:G:B:A] [--codec=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--report=text|json] [--overwrite]
  wowcrucible asset texture-brush <input.blp|image> <output.png|blp> (--point=x:y [...]|--fill|--invert-alpha) [--mip=N] [--radius=N] [--opacity=0..1] [--color=R:G:B:A] [--tool=color-alpha|rgb|alpha|erase-alpha] [--falloff=smooth|linear|hard] [--format=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--overwrite]
  wowcrucible asset texture-encode <image.png|jpg|bmp|tga> <output.blp> [--format=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--overwrite]
  wowcrucible asset texture-validate <file-or-folder> [--recursive]
  wowcrucible asset inspect <model.m2|building.wmo>...
  wowcrucible asset m2-material-audit <wrath-m2-skin-root|file.skin> [--workers=N] [--examples=N] [--format=text|json]
  wowcrucible asset m2-downport-plan <modern.m2> [--skin=file.skin] [--listfile=id-path.csv] [--format=text|json]
  wowcrucible asset m2-downport-scan <file-or-folder>... [--listfile=id-path.csv] [--format=text|json]
  wowcrucible asset m2-downport <modern.m2> <new-output-folder> [--skin=file.skin] [--listfile=id-path.csv]
  wowcrucible asset m2-downport-batch-plan <source-root> [--listfile=id-path.csv|--auto-listfile] [--format=text|json]
  wowcrucible asset m2-downport-batch <source-root> <new-output-folder> [--listfile=id-path.csv|--auto-listfile] [--ready-only] [--workers=N]
  wowcrucible asset dependency-graph <processed-library> <root.m2|wmo|adt|wdt> [--target-index=client-index] [--target-choice=client-path|archive]... [--only-problems] [--manifest=patch.json] [--output-mpq=name.MPQ] [--format=text|json]
  wowcrucible asset gameobject-index-plan <client-index> <GameObjectDisplayInfo.dbc> <schema.xml> <new-workspace> <virtual-model-path>... [--display-start=N] [--template-start=N] [--occupied=ids.txt] [--archive-choice=client-path|Data\archive.MPQ]... [--format=text|json]
  wowcrucible asset indexed-snapshot-verify <indexed-assets.snapshot.json> [--archives] [--format=text|json]
  wowcrucible asset gameobject-bulk-plan <GameObjectDisplayInfo.dbc> <schema.xml> <plan.json> <model-or-folder>... [--library=processed-folder] [--client-root=folder] [--display-start=N] [--template-start=N] [--occupied=ids.txt] [--format=text|json] [--overwrite]
  wowcrucible asset gameobject-bulk-apply <plan.json> <new-or-empty-output-folder> [--format=text|json]
  wowcrucible asset creature-display-catalog <dbc-folder> [--schema=file] [--search=terms] [--limit=N] [--format=text|json]
  wowcrucible asset creature-appearance-port-plan <source-dbc-folder> <target-dbc-folder> <display-id> --schema=file [--output=plan.json] [--format=text|json] [--overwrite]
  wowcrucible asset npc-chr-plan <file.chr> <texture> <target-dbc-folder> <schema.xml> <host> <port> <user> <database> <plan.json> [--display-start=N] [--extra-start=N] [--sound=N] [--scale=1] [--alpha=255] [--password-env=NAME] [--format=text|json] [--overwrite]
  wowcrucible asset npc-chr-apply <plan.json> <new-or-empty-output-folder> [--format=text|json]
  wowcrucible asset item-client-plan <Item.dbc> <ItemDisplayInfo.dbc> <schema.xml> <host> <port> <user> <database> <plan.json> [--password-env=NAME] [--format=text|json] [--overwrite]
  wowcrucible asset item-client-apply <plan.json> <new-or-empty-output-folder> [--format=text|json]
  wowcrucible asset creature-appearance-port-apply <plan.json> <new-or-empty-output-folder> [--format=text|json]
  wowcrucible asset creature-appearance-patch-plan <port-receipt.json> <processed-library> [--provenance=name] [--output=patch-plan.json] [--format=text|json] [--overwrite]
  wowcrucible asset creature-appearance-patch-manifest <patch-plan.json> <manifest.json> [--mpq=patch-name.MPQ] [--overwrite]
  wowcrucible asset creature-appearances <model-client-path> [--dbc=folder] [--schema=file] [--library=folder --provenance=name] [--format=text|json]
  wowcrucible asset preview-info <wrath-model.m2> [--skin=file.skin] [--dbc=folder] [--hair=N] [--facial-hair=N] [--animation=sequence-index] [--time=milliseconds] [--naked|--groups=group:variant,...|--all-geosets]
  wowcrucible asset model-export <wrath-model.m2> <output.obj> [--skin=file.skin] [--animation=sequence-index --time=milliseconds] [--texture=slot:file.blp]... [--naked|--groups=group:variant,...|--all-geosets] [--overwrite]
  wowcrucible asset wmo-preview-info <root-or-group.wmo> [--groups] [--content-root=folder] [--format=text|json]
  wowcrucible asset path-candidates <processed-library> <client-path> [--preferred=provenance] [--format=text|json]
  wowcrucible asset appearance-info <CharSections.dbc> <logical-path> <model-file>
  wowcrucible asset appearance-render <library> <dbc-folder> <logical-path> <model-file> <body.png> [--skin=N --face=N --facial-hair=N --hair=N --source=name --hair-output=file] [--overwrite]
  wowcrucible asset appearance-compose <base.blp> <output.png> [component options] [--overwrite]
  wowcrucible asset models <library-folder> <logical-directory>
  wowcrucible asset definitive-status <library-folder>
  wowcrucible asset definitive-stage <library-folder> <output-folder>
  wowcrucible asset workspace <new-output-folder> <files/folders...>
  wowcrucible asset library-plan <source-folder> <library-folder> [--max-gb=2]
  wowcrucible asset library-run <library-folder> [--workers=6]
  wowcrucible asset library-import <extracted-folder> <library-folder> <provenance> [--workers=6]
  wowcrucible asset library-repair <library-folder> [--workers=6]
  wowcrucible asset library-artifacts <library-folder> [--source-root=folder]... [--apply]
  wowcrucible asset library-layout <library-folder> [--apply]
  wowcrucible asset library-consolidate <library-folder> [--apply]
  wowcrucible asset library-catalog <library-folder>
  wowcrucible asset library-status <library-folder>
  wowcrucible asset compare-folders <library-folder> [path-filter]
  wowcrucible asset compare-files <library-folder> <logical-directory>

Full guide: docs/CLI-REFERENCE.md
""", code);

static async Task<int> Project(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return ProjectHelp();
    if (args is ["create", var root, var name, .. var createOptions])
    {
        var target = Option(createOptions, "--target=") ?? TargetProfileCatalog.DefaultProfileId; var library = Option(createOptions, "--asset-library=");
        var unknown = createOptions.Where(option => !option.StartsWith("--target=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--asset-library=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown project create option: {unknown[0]}");
        var project = CrucibleContentProjectService.Create(root, name, target, library); Console.Error.WriteLine($"Created {project.Name} at {Path.GetFullPath(root)}\nTarget: {project.TargetProfile}\nID registry: {Path.Combine(Path.GetFullPath(root), project.IdRegistryFile)}"); return 0;
    }
    if (args is ["status", var projectRoot])
    {
        var project = CrucibleContentProjectService.Load(projectRoot); var registry = CrucibleContentProjectService.LoadRegistry(projectRoot);
        Console.WriteLine($"Name\t{project.Name}\nTarget\t{project.TargetProfile}\nAssetLibrary\t{project.AssetLibrary ?? "not linked"}\nReservations\t{registry.Reservations.Count}\nReservedIDs\t{registry.Reservations.Sum(reservation => reservation.Values.Count)}");
        foreach (var group in registry.Reservations.GroupBy(reservation => reservation.Domain)) Console.WriteLine($"DOMAIN\t{group.Key}\t{group.Sum(reservation => reservation.Values.Count)}"); return 0;
    }
    if (args is ["reserve-ids", var reserveRoot, var domainText, var countText, .. var reserveOptions] && Enum.TryParse<ContentIdDomain>(domainText, true, out var domain) && int.TryParse(countText, out var count))
    {
        var startText = Option(reserveOptions, "--start=") ?? "100000"; var occupiedPath = Option(reserveOptions, "--occupied="); var purpose = Option(reserveOptions, "--purpose=") ?? "Unspecified content";
        var unknown = reserveOptions.Where(option => !option.StartsWith("--start=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--occupied=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--purpose=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown ID reservation option: {unknown[0]}");
        if (!uint.TryParse(startText, out var start)) return Fail("--start must be an unsigned integer."); IReadOnlyList<uint> occupied = occupiedPath is null ? [] : CrucibleContentProjectService.ReadOccupiedIds(occupiedPath);
        var result = CrucibleContentProjectService.ReserveIds(reserveRoot, domain, count, start, occupied, purpose); Console.WriteLine(string.Join(Environment.NewLine, result.Reservation.Values));
        Console.Error.WriteLine($"Reserved {result.Reservation.Values.Count:N0} {domain} ID(s), {result.Reservation.Values.First():N0}–{result.Reservation.Values.Last():N0}, for {result.Reservation.Purpose}.{(occupiedPath is null ? " WARNING: no live DBC/SQL occupied-ID list was supplied." : $" Checked occupied IDs from {Path.GetFullPath(occupiedPath)}.")}"); return occupiedPath is null ? 3 : 0;
    }
    var liveOperation = args.FirstOrDefault()?.ToLowerInvariant();
    var reserveLive = liveOperation == "reserve-live";
    var connectionOffset = reserveLive ? 4 : 2;
    if (liveOperation is "occupancy" or "reserve-live" && args.Length >= connectionOffset + 5 &&
        Enum.TryParse<ContentIdDomain>(args[reserveLive ? 2 : 1], true, out var liveDomain))
    {
        var liveCount = 0;
        if (reserveLive && (!int.TryParse(args[3], out liveCount) || liveCount < 1)) return Fail("reserve-live count must be a positive integer.");
        var host = args[connectionOffset]; var portText = args[connectionOffset + 1]; var user = args[connectionOffset + 2]; var database = args[connectionOffset + 3];
        if (!uint.TryParse(portText, out var port) || port is 0 or > 65535) return Fail("Database port must be from 1 to 65535.");
        var options = args[(connectionOffset + 4)..]; var dbc = Option(options, "--dbc="); var schema = Option(options, "--schema="); var startText = Option(options, "--start="); var purpose = Option(options, "--purpose=") ?? "Unspecified content"; var json = options.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var passwordEnvironment = Option(options, "--password-env=") ?? "WOW_CRUCIBLE_DB_PASSWORD"; var sslText = Option(options, "--ssl=") ?? "Preferred";
        var allowed = new[] { "--dbc=", "--schema=", "--start=", "--purpose=", "--password-env=", "--ssl=" };
        var unknown = options.Where(option => !allowed.Any(prefix => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown project {liveOperation} option: {unknown[0]}");
        if (string.IsNullOrWhiteSpace(passwordEnvironment)) return Fail("--password-env requires a non-empty environment-variable name.");
        var password = Environment.GetEnvironmentVariable(passwordEnvironment); if (password is null) return Fail($"Set the {passwordEnvironment} environment variable for this process. Passwords are not accepted on the command line.");
        if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(sslText, true, out var ssl)) return Fail($"Unknown SSL mode: {sslText}");
        uint? start = null; if (startText is not null) { if (!uint.TryParse(startText, out var parsedStart)) return Fail("--start must be an unsigned integer."); start = parsedStart; }
        var profile = new DatabaseConnectionProfile(host, port, user, password, database, ssl); var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var report = await new ContentIdOccupancyService().InspectAsync(liveDomain, profile, capabilities, dbc, schema, cancellationToken: cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"Domain\t{report.Domain}\nRegistryNamespace\t{report.RegistryNamespace}\nComplete\t{report.Complete}\nOccupiedIDs\t{report.OccupiedIds.Count}\nMaximumOccupied\t{report.MaximumOccupied?.ToString() ?? "none"}");
            foreach (var source in report.Sources) Console.WriteLine($"SOURCE\t{source.Kind}\t{source.Name}\t{(source.Available ? "AVAILABLE" : "MISSING")}\t{source.Ids}\t{source.Location}\t{source.Detail}");
            foreach (var warning in report.Warnings) Console.Error.WriteLine($"WARNING\t{warning}");
        }
        if (!reserveLive) return report.Complete ? 0 : 3;
        if (!report.Complete) { Console.Error.WriteLine("Refusing to reserve IDs because one or more authoritative occupancy sources could not be read."); return 3; }
        var result = CrucibleContentProjectService.ReserveVerifiedIds(args[1], report, liveCount, start, purpose);
        Console.WriteLine(string.Join(Environment.NewLine, result.Reservation.Values));
        Console.Error.WriteLine($"Reserved {result.Reservation.Values.Count:N0} collision-checked {liveDomain} ID(s), {result.Reservation.Values.First():N0}–{result.Reservation.Values.Last():N0}, in namespace {report.RegistryNamespace} for {result.Reservation.Purpose}."); return 0;
    }
    return ProjectHelp(2);
}

static int ProjectHelp(int code = 0) => GroupHelp($"Usage:\n  wowcrucible project create <folder> <name> [--target={TargetProfileCatalog.DefaultProfileId}] [--asset-library=folder]\n  wowcrucible project status <project-folder>\n  wowcrucible project reserve-ids <project-folder> <domain> <count> [--start=N] [--occupied=ids.txt] [--purpose=text]\n  wowcrucible project occupancy <domain> <host> <port> <user> <database> --dbc=folder --schema=schema.xml [--format=text|json]\n  wowcrucible project reserve-live <project-folder> <domain> <count> <host> <port> <user> <database> --dbc=folder --schema=schema.xml [--start=N] [--purpose=text]\n\nLive commands read passwords from WOW_CRUCIBLE_DB_PASSWORD by default and refuse reservation unless every mapped SQL/DBC identity source is available. Mount and Spell deliberately share the same registry namespace.\n\nID domains: Item, ItemSet, Spell, CreatureTemplate, CreatureModelData, CreatureDisplayInfo, CreatureDisplayInfoExtra, GameObject, GameObjectDisplayInfo, Race, Class, Faction, Mount, Quest, Custom", code);

static int Client(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return ClientHelp();
    if (args is ["install-patch", var sourcePatch, var installClientRoot, .. var installOptions])
    {
        var targetName = Option(installOptions, "--name=");
        var unknown = installOptions.Where(option => !option.StartsWith("--name=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client patch install option: {unknown[0]}");
        var result = ClientPatchDeploymentService.Install(sourcePatch, installClientRoot, targetName);
        Console.Error.WriteLine($"Installed {result.InstalledPath}\nSHA-256 {result.Sha256}\nBackup: {result.BackupPath ?? "not needed"}\nCache: {(result.Cache.Existed ? $"deleted {result.Cache.DeletedFiles:N0} file(s), {result.Cache.DeletedBytes:N0} bytes" : "already absent")}");
        return 0;
    }
    if (args is ["clear-cache", var cacheClientRoot])
    {
        var result = ClientPatchDeploymentService.InvalidateCache(cacheClientRoot);
        Console.Error.WriteLine(result.Existed
            ? $"Deleted {result.CachePath} ({result.DeletedFiles:N0} file(s), {result.DeletedBytes:N0} bytes)."
            : $"Client cache is already absent: {result.CachePath}");
        return 0;
    }
    if (args is ["publisher-key", var publisherPrivateKey, var publisherPublicKey, .. var publisherKeyOptions])
    {
        var passwordEnvironment = Option(publisherKeyOptions, "--password-env=") ?? "WOW_CRUCIBLE_PUBLISHER_PASSWORD";
        var unknown = publisherKeyOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown publisher-key option: {unknown[0]}");
        if (string.IsNullOrWhiteSpace(passwordEnvironment)) return Fail("--password-env requires a non-empty environment-variable name.");
        var password = Environment.GetEnvironmentVariable(passwordEnvironment); if (password is null) return Fail($"Set the {passwordEnvironment} environment variable. Publisher passwords are never accepted on the command line.");
        var result = ClientReleaseSigningService.CreatePublisherKey(publisherPrivateKey, publisherPublicKey, password.AsSpan());
        Console.WriteLine($"PRIVATE_KEY\t{result.PrivateKeyPath}\nPUBLIC_KEY\t{result.PublicKeyPath}\nKEY_ID\t{result.KeyId}\nALGORITHM\t{result.Algorithm}");
        return 0;
    }
    if (args is ["release-sign", var signBundle, var signPrivateKey, var signedChannelOutput, .. var signOptions])
    {
        var passwordEnvironment = Option(signOptions, "--password-env=") ?? "WOW_CRUCIBLE_PUBLISHER_PASSWORD";
        var unknown = signOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown release-sign option: {unknown[0]}");
        if (string.IsNullOrWhiteSpace(passwordEnvironment)) return Fail("--password-env requires a non-empty environment-variable name.");
        var password = Environment.GetEnvironmentVariable(passwordEnvironment); if (password is null) return Fail($"Set the {passwordEnvironment} environment variable. Publisher passwords are never accepted on the command line.");
        var result = ClientReleaseSigningService.SignBundle(signBundle, signPrivateKey, password.AsSpan(), signedChannelOutput);
        Console.WriteLine($"SIGNED_CHANNEL\t{result.SignedChannelPath}\nCHANNEL_SHA256\t{result.SignedChannelSha256}\nKEY_ID\t{result.KeyId}\nCONTENT_ID\t{result.Manifest.ContentId}\nFILES\t{result.Manifest.Files.Count:N0}");
        return 0;
    }
    if (args is ["release-verify", var verifyChannel, var verifyPublicKey, .. var verifyOptions])
    {
        var json = verifyOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = verifyOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown release-verify option: {unknown[0]}");
        var result = ClientReleaseSigningService.VerifySignedChannel(verifyChannel, verifyPublicKey);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"VERIFIED\tTrue\nSIGNED_CHANNEL\t{result.SignedChannelPath}\nCHANNEL_SHA256\t{result.SignedChannelSha256}\nTRUSTED_PUBLIC_KEY\t{result.TrustedPublicKeyPath}\nKEY_ID\t{result.KeyId}\nCHANNEL\t{result.Manifest.Channel}\nRELEASE\t{result.Manifest.Name}\nCONTENT_ID\t{result.Manifest.ContentId}\nFILES\t{result.Manifest.Files.Count:N0}\nBYTES\t{result.Body.PayloadBytes:N0}");
        return 0;
    }
    if (args is ["release-create", var releaseSource, var releaseBundle, .. var releaseOptions])
    {
        var name = Option(releaseOptions, "--name="); var channel = Option(releaseOptions, "--channel=") ?? "public"; var changelogFile = Option(releaseOptions, "--changelog=");
        var unknown = releaseOptions.Where(option => !option.StartsWith("--name=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--channel=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--changelog=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--optional=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown release-create option: {unknown[0]}"); if (string.IsNullOrWhiteSpace(name)) return Fail("release-create requires --name=Release Name.");
        var changelog = changelogFile is null ? string.Empty : File.ReadAllText(changelogFile); var rules = ParseReleaseGroupRules(releaseOptions);
        var result = ClientReleaseService.CreateBundle(releaseSource, releaseBundle, name, channel, changelog, rules);
        Console.WriteLine($"BUNDLE\t{result.BundleRoot}\nMANIFEST\t{result.ManifestPath}\nCONTENT_ID\t{result.Manifest.ContentId}\nFILES\t{result.Manifest.Files.Count:N0}\nBYTES\t{result.PayloadBytes:N0}");
        foreach (var group in result.Manifest.Files.Where(file => file.OptionalGroup is not null).GroupBy(file => file.OptionalGroup, StringComparer.OrdinalIgnoreCase)) Console.WriteLine($"OPTIONAL\t{group.Key}\t{group.Count():N0}");
        return 0;
    }
    if (args is ["release-plan", var releaseManifest, var releaseClientRoot, var releasePlanPath, .. var releasePlanOptions])
    {
        var groups = releasePlanOptions.Where(option => option.StartsWith("--group=", StringComparison.OrdinalIgnoreCase)).Select(option => option[8..]).ToArray(); var overwrite = releasePlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var trustedKey = Option(releasePlanOptions, "--trusted-key=");
        var unknown = releasePlanOptions.Where(option => !option.StartsWith("--group=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--trusted-key=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown release-plan option: {unknown[0]}");
        var plan = trustedKey is null
            ? ClientReleaseService.CreatePlan(releaseManifest, releaseClientRoot, groups)
            : ClientReleaseService.CreateTrustedPlan(releaseManifest, trustedKey, releaseClientRoot, groups);
        ClientReleaseService.SavePlan(releasePlanPath, plan, overwrite); PrintReleasePlan(plan); Console.WriteLine($"PLAN\t{Path.GetFullPath(releasePlanPath)}"); return plan.Ready ? 0 : 3;
    }
    if (args is ["release-apply", var releaseApplyPlanPath, var releaseReceiptPath, .. var releaseApplyOptions])
    {
        var apply = releaseApplyOptions.Contains("--apply", StringComparer.OrdinalIgnoreCase); var overwrite = releaseApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var unknown = releaseApplyOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown release-apply option: {unknown[0]}");
        var plan = ClientReleaseService.LoadPlan(releaseApplyPlanPath); PrintReleasePlan(plan); if (!apply) { Console.Error.WriteLine("Dry-run only. Re-run with --apply after reviewing every action."); return plan.Ready ? 0 : 3; }
        var result = ClientReleaseService.Apply(plan, releaseReceiptPath, overwrite); Console.WriteLine($"RECEIPT\t{result.ReceiptPath}\nSTATE\t{result.InstalledStatePath}\nCHANGED\t{result.ChangedFiles:N0}\nREMOVED\t{result.RemovedFiles:N0}\nCLOSED_WOW\t{string.Join(',', result.ClosedWowProcessIds)}\nCACHE_FILES\t{result.Cache.DeletedFiles:N0}"); return 0;
    }
    if (args is ["release-rollback", var releaseRollbackReceipt, .. var releaseRollbackOptions])
    {
        var apply = releaseRollbackOptions.Contains("--apply", StringComparer.OrdinalIgnoreCase); var unknown = releaseRollbackOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown release-rollback option: {unknown[0]}");
        var receipt = ClientReleaseService.ValidateRollback(releaseRollbackReceipt); Console.WriteLine($"TARGET\t{receipt.TargetClientRoot}\nACTIONS\t{receipt.Actions.Count:N0}\nBACKUP\t{receipt.BackupRoot}\nSTATUS\t{receipt.Status}"); if (!apply) { Console.Error.WriteLine("Rollback validation passed; dry-run only. Re-run with --apply to restore exact preimages."); return 0; }
        var result = ClientReleaseService.Rollback(releaseRollbackReceipt); Console.WriteLine($"RECEIPT\t{result.ReceiptPath}\nRESTORED\t{result.RestoredFiles:N0}\nREMOVED\t{result.RemovedFiles:N0}\nCACHE_FILES\t{result.Cache.DeletedFiles:N0}"); return 0;
    }
    if (args is ["fusion", var baseRoot, .. var fusionInputs])
    {
        var stage = Option(fusionInputs, "--stage="); var output = Option(fusionInputs, "--output="); var showAll = fusionInputs.Contains("--all", StringComparer.OrdinalIgnoreCase);
        var sourcePaths = fusionInputs.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var unknown = fusionInputs.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.StartsWith("--stage=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !value.Equals("--all", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client fusion option: {unknown[0]}");
        if (sourcePaths.Length == 0) return Fail("Client fusion requires at least one extracted/effective override source.");
        var sources = sourcePaths.Select((path, index) => new ClientFusionSource($"source-{index + 1}-{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}", path)).ToArray();
        var plan = ClientFusionPlanner.Analyze(baseRoot, sources, new ConsoleProgress(5));
        foreach (var entry in plan.Entries.Where(entry => showAll && entry.Status != ClientFusionStatus.IdenticalToBase || entry.Status == ClientFusionStatus.Conflict))
            Console.WriteLine($"{entry.Status}\t{entry.ArchivePath}\t{entry.Candidates.Count}\t{entry.Guidance}");
        var conflicts = plan.Entries.Count(entry => entry.Status == ClientFusionStatus.Conflict);
        Console.Error.WriteLine($"Fusion plan: {plan.Entries.Count(entry => entry.Status != ClientFusionStatus.IdenticalToBase):N0} changed path(s), {conflicts:N0} unresolved conflict(s), {plan.Entries.Count(entry => entry.Status == ClientFusionStatus.IdenticalToBase):N0} base-identical omission(s).");
        if (output is not null) { ClientFusionPlanner.Save(output, plan); Console.Error.WriteLine($"Saved fusion plan: {Path.GetFullPath(output)}"); }
        if (stage is not null)
        {
            var result = ClientFusionPlanner.Stage(stage, plan);
            Console.Error.WriteLine($"Staged {result.StagedFiles:N0} resolved path(s); excluded {result.UnresolvedConflicts:N0} conflict(s). Manifest: {result.ManifestPath}");
        }
        return conflicts == 0 ? 0 : 3;
    }
    if (args is ["fusion-dbc-plan", var fusionPlanPath, var fusionSchemaPath, .. var fusionDbcPlanOptions])
    {
        var planOutput = Option(fusionDbcPlanOptions, "--output="); var overwrite = fusionDbcPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var json = fusionDbcPlanOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = fusionDbcPlanOptions.Where(option => !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown fusion-dbc-plan option: {unknown[0]}"); if (planOutput is not null && File.Exists(planOutput) && !overwrite) return Fail($"DBC fusion plan already exists; use --overwrite to replace it: {Path.GetFullPath(planOutput)}");
        var plan = ClientFusionDbcService.CreatePlan(ClientFusionPlanner.Load(fusionPlanPath), fusionSchemaPath); if (planOutput is not null) ClientFusionDbcService.SavePlan(planOutput, plan);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"TABLES\t{plan.Tables.Count:N0}\nRESOLVABLE\t{plan.ResolvableTables:N0}\nBLOCKED\t{plan.BlockedTables:N0}");
            foreach (var table in plan.Tables) { Console.WriteLine($"DBC\t{(table.Ready ? table.RequiresOutput ? "MERGE" : "OMIT_EQUAL" : "BLOCKED")}\t{table.ArchivePath}\tadd={table.Additions.Count:N0}\treuse={table.ReusedRows:N0}\tconflicts={table.Conflicts.Count:N0}"); foreach (var conflict in table.Conflicts) Console.WriteLine($"CONFLICT\t{table.Table}\tID={conflict.Id}\t{conflict.ExistingSource}\t{conflict.IncomingSource}\t{string.Join(',', conflict.DifferingColumns)}"); foreach (var blocker in table.Blockers) Console.WriteLine($"BLOCKER\t{table.Table}\t{blocker}"); }
            foreach (var finding in plan.Findings) Console.WriteLine($"FINDING\t{finding}"); if (planOutput is not null) Console.WriteLine($"PLAN\t{Path.GetFullPath(planOutput)}");
        }
        return plan.BlockedTables == 0 ? 0 : 3;
    }
    if (args is ["fusion-dbc-apply", var fusionDbcPlanPath, var fusionDbcOutput])
    {
        var result = ClientFusionDbcService.Apply(ClientFusionDbcService.LoadPlan(fusionDbcPlanPath), fusionDbcOutput); Console.WriteLine($"OUTPUT\t{result.OutputDirectory}\nRECEIPT\t{result.ReceiptPath}\nMERGED\t{result.OutputFiles.Count:N0}\nOMITTED_EQUAL\t{result.OmittedArchivePaths.Count:N0}\nBLOCKED\t{result.BlockedArchivePaths.Count:N0}");
        foreach (var pair in result.OutputFiles) Console.WriteLine($"DBC\t{pair.Key}\t{pair.Value}\t{result.OutputSha256[pair.Key]}"); return result.BlockedArchivePaths.Count == 0 ? 0 : 3;
    }
    if (args is ["fusion-dbc-remap-plan", var remapFusionPlanPath, var remapSchemaPath, var remapDefinitionsRoot, .. var remapPlanOptions])
    {
        var planOutput = Option(remapPlanOptions, "--output="); var overwrite = remapPlanOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var json = remapPlanOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = remapPlanOptions.Where(option => !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown fusion-dbc-remap-plan option: {unknown[0]}"); if (planOutput is not null && File.Exists(planOutput) && !overwrite) return Fail($"DBC remap plan already exists; use --overwrite to replace it: {Path.GetFullPath(planOutput)}");
        var plan = ClientFusionDbcRemapService.CreatePlan(ClientFusionPlanner.Load(remapFusionPlanPath), remapSchemaPath, remapDefinitionsRoot); if (planOutput is not null) ClientFusionDbcRemapService.SavePlan(planOutput, plan);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"TABLES\t{plan.Tables.Count:N0}\nOPERATIONS\t{plan.Tables.Sum(table => table.Operations.Count):N0}\nADD\t{plan.AddedRows:N0}\nREUSE\t{plan.ReusedMappings:N0}\nBLOCKERS\t{plan.Blockers.Count:N0}");
            foreach (var table in plan.Tables) { Console.WriteLine($"DBC\t{table.Table}\tadd={table.AddedRows:N0}\treuse={table.ReusedMappings:N0}\toperations={table.Operations.Count:N0}"); foreach (var operation in table.Operations) Console.WriteLine($"MAP\t{table.Table}\t{operation.SourceName}\t{operation.SourceId}>{operation.TargetId}\t{(operation.AddsRow ? "ADD" : "REUSE")}\trefs={string.Join(',', operation.ReferenceRewrites.Select(pair => $"{pair.Key}:{pair.Value}"))}"); }
            foreach (var blocker in plan.Blockers) Console.WriteLine($"BLOCKER\t{blocker}"); foreach (var finding in plan.Findings) Console.WriteLine($"FINDING\t{finding}"); if (planOutput is not null) Console.WriteLine($"PLAN\t{Path.GetFullPath(planOutput)}");
        }
        return plan.Ready ? 0 : 3;
    }
    if (args is ["fusion-dbc-remap-apply", var remapPlanPath, var remapOutput])
    {
        var result = ClientFusionDbcRemapService.Apply(ClientFusionDbcRemapService.LoadPlan(remapPlanPath), remapOutput); Console.WriteLine($"OUTPUT\t{result.OutputDirectory}\nRECEIPT\t{result.ReceiptPath}\nMERGED\t{result.OutputFiles.Count:N0}\nOMITTED_EQUAL\t{result.OmittedArchivePaths.Count:N0}\nADD\t{result.Plan.AddedRows:N0}\nREUSE\t{result.Plan.ReusedMappings:N0}");
        foreach (var pair in result.OutputFiles) Console.WriteLine($"DBC\t{pair.Key}\t{pair.Value}\t{result.OutputSha256[pair.Key]}"); return 0;
    }
    if (args is ["fusion-stage", var stagedFusionPlanPath, var stagedFusionRoot, .. var fusionStageOptions])
    {
        var dbcReceipt = Option(fusionStageOptions, "--dbc-receipt="); var remapReceipt = Option(fusionStageOptions, "--dbc-remap-receipt="); var unknown = fusionStageOptions.Where(option => !option.StartsWith("--dbc-receipt=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc-remap-receipt=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown fusion-stage option: {unknown[0]}"); if (dbcReceipt is not null && remapReceipt is not null) return Fail("Select either --dbc-receipt or --dbc-remap-receipt, not both.");
        var plan = ClientFusionPlanner.Load(stagedFusionPlanPath); var dbcResult = dbcReceipt is null ? null : ClientFusionDbcService.LoadResult(dbcReceipt); var remapResult = remapReceipt is null ? null : ClientFusionDbcRemapService.LoadResult(remapReceipt); var result = ClientFusionPlanner.Stage(stagedFusionRoot, plan, dbcResult: dbcResult, dbcRemapResult: remapResult);
        Console.WriteLine($"ROOT\t{result.RootPath}\nMANIFEST\t{result.ManifestPath}\nSTAGED\t{result.StagedFiles:N0}\nBASE_IDENTICAL\t{result.SkippedBaseFiles:N0}\nUNRESOLVED\t{result.UnresolvedConflicts:N0}"); return result.UnresolvedConflicts == 0 ? 0 : 3;
    }
    if (args is ["index", var clientRoot, var outputDirectory, .. var options])
    {
        var unknown = options.Where(option => !option.Equals("--no-hash", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--client-exe=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client index option: {unknown[0]}");
        var progress = new ClientIndexConsoleProgress();
        var listFile = Option(options, "--listfile=");
        var clientExecutable = Option(options, "--client-exe=");
        var index = new ClientArchiveIndexService().Build(clientRoot, outputDirectory, !options.Contains("--no-hash", StringComparer.OrdinalIgnoreCase), progress, externalListFile: listFile, executablePath: clientExecutable);
        Console.Error.WriteLine($"Indexed {index.Archives.Count:N0} MPQs for {index.Name}: {index.Archives.Sum(archive => archive.PayloadFiles):N0} payload paths, {index.Archives.Sum(archive => archive.AnonymousFiles):N0} unresolved name(s), {index.Archives.Count(archive => archive.Error is not null):N0} archive error(s). Index: {Path.GetFullPath(outputDirectory)}");
        return index.Archives.Any(archive => archive.Error is not null) ? 1 : 0;
    }
    if (args is ["corpus", var outputFile, .. var indexDirectories] && indexDirectories.Length > 0)
    {
        var count = ClientArchiveIndexService.CreatePathCorpus(indexDirectories, outputFile);
        Console.Error.WriteLine($"Wrote {count:N0} distinct known MPQ paths to {Path.GetFullPath(outputFile)}");
        return 0;
    }
    if (args is ["extract", var extractIndexDirectory, var archiveRelativePath, var destination, .. var extractOptions])
    {
        var quiet = extractOptions.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        var resolvedOnly = extractOptions.Contains("--resolved-only", StringComparer.OrdinalIgnoreCase);
        var anonymousOnly = extractOptions.Contains("--anonymous-only", StringComparer.OrdinalIgnoreCase);
        var overwrite = extractOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var workerText = Option(extractOptions, "--workers="); var workers = workerText is null ? 0 : int.Parse(workerText);
        if (workerText is not null && workers is (< 1 or > PatchArchiveService.MaximumExtractionWorkers)) throw new ArgumentOutOfRangeException(nameof(workers), $"Extraction workers must be from 1 to {PatchArchiveService.MaximumExtractionWorkers}.");
        var unknown = extractOptions.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.Equals("--resolved-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--anonymous-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client extract option: {unknown[0]}");
        var filters = extractOptions.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (filters.Length > 1) return Fail("Client extract accepts at most one path filter.");
        var started = Stopwatch.StartNew();
        var result = ClientArchiveIndexService.ExtractIndexed(extractIndexDirectory, archiveRelativePath, destination, filters.FirstOrDefault(), resolvedOnly, anonymousOnly, overwrite, quiet ? null : new ConsoleProgress(100), workers: workers);
        Console.Error.WriteLine($"Selected {result.SelectedFiles:N0} indexed file(s); extracted {result.ExtractedFiles:N0}, resumed past {result.SkippedExistingFiles:N0} existing file(s) in {started.Elapsed.TotalSeconds:0.00}s. Destination: {Path.GetFullPath(destination)}");
        return 0;
    }
    if (args is ["show", var showIndexDirectory])
    {
        var index = ClientArchiveIndexService.Load(showIndexDirectory);
        var loose = index.LooseFiles ?? [];
        Console.WriteLine($"Client\t{index.Name}\nRoot\t{index.ClientRoot}\nComplete\t{index.Complete}\nArchives\t{index.CompletedArchives}\nActiveLocale\t{index.ActiveLocale ?? "unknown"}\nExecutablePath\t{index.Executable?.Path ?? "missing"}\nExecutable\t{index.Executable?.FileVersion ?? "missing"}\nExecutableSha256\t{index.Executable?.Sha256 ?? "missing"}\nPayloadPaths\t{index.Archives.Sum(archive => archive.PayloadFiles)}\nAnonymousPaths\t{index.Archives.Sum(archive => archive.AnonymousFiles)}\nBackupArchives\t{index.Archives.Count(archive => archive.Scope == ClientArchiveScope.Backup)}\nInactiveLocaleArchives\t{index.Archives.Count(archive => archive.Scope == ClientArchiveScope.InactiveLocale)}\nCustomSubdirectoryArchives\t{index.Archives.Count(archive => archive.Scope == ClientArchiveScope.CustomSubdirectory)}\nLooseFiles\t{loose.Count}\nRuntimeFiles\t{loose.Count(file => file.Scope == ClientLooseFileScope.Runtime)}\nAddOnFiles\t{loose.Count(file => file.Scope == ClientLooseFileScope.AddOn)}\nArchiveErrors\t{index.Archives.Count(archive => archive.Error is not null)}");
        return 0;
    }
    return ClientHelp(2);
}

static IReadOnlyList<ClientReleaseGroupRule> ParseReleaseGroupRules(IEnumerable<string> options)
{
    var rules = new List<ClientReleaseGroupRule>();
    foreach (var option in options.Where(option => option.StartsWith("--optional=", StringComparison.OrdinalIgnoreCase)))
    {
        var value = option[11..]; var separator = value.IndexOf('|'); if (separator < 1 || separator == value.Length - 1) throw new ArgumentException($"Optional group must use --optional=group|relative-prefix: {value}");
        rules.Add(new(value[..separator], value[(separator + 1)..]));
    }
    return rules;
}

static void PrintReleasePlan(ClientReleasePlan plan)
{
    Console.WriteLine($"READY\t{plan.Ready}\nTARGET\t{plan.TargetClientRoot}\nTRUST\t{(plan.Trust is null ? "LOCAL_UNSIGNED" : "VERIFIED_SIGNED_CHANNEL")}\nPUBLISHER_KEY_ID\t{plan.Trust?.KeyId ?? "-"}\nADD\t{plan.Adds:N0}\nREPLACE\t{plan.Replacements:N0}\nREMOVE_MANAGED\t{plan.Removals:N0}\nUNCHANGED\t{plan.Unchanged:N0}\nGROUPS\t{string.Join(',', plan.SelectedOptionalGroups)}");
    foreach (var action in plan.Actions) Console.WriteLine($"ACTION\t{action.Kind}\t{action.RelativePath}\t{action.OptionalGroup ?? "required"}\t{action.Detail}"); foreach (var blocker in plan.Blockers) Console.WriteLine($"BLOCKER\t{blocker}");
}

static int ClientHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible client install-patch <patch.mpq> <client-root> [--name=patch-X.MPQ]\n  wowcrucible client clear-cache <client-root>\n  wowcrucible client publisher-key <new-private.pem> <new-public.pem> [--password-env=WOW_CRUCIBLE_PUBLISHER_PASSWORD]\n  wowcrucible client release-create <source-folder> <new-bundle-folder> --name=NAME [--channel=public] [--changelog=notes.txt] [--optional=group|relative-prefix]...\n  wowcrucible client release-sign <bundle-or-manifest> <encrypted-private.pem> <bundle\\channel.crucible.json> [--password-env=WOW_CRUCIBLE_PUBLISHER_PASSWORD]\n  wowcrucible client release-verify <channel.crucible.json> <trusted-public.pem> [--format=text|json]\n  wowcrucible client release-plan <bundle-or-signed-channel> <client-root> <plan.json> [--trusted-key=public.pem] [--group=name]... [--overwrite]\n  wowcrucible client release-apply <plan.json> <receipt.json> [--apply] [--overwrite]\n  wowcrucible client release-rollback <receipt.json> [--apply]\n  wowcrucible client index <client-root> <index-directory> [--no-hash] [--listfile=paths.txt] [--client-exe=Wow.exe]\n  wowcrucible client corpus <output-listfile> <index-directory>...\n  wowcrucible client extract <index-directory> <archive-relative-path> <folder> [path-glob-or-text] [--resolved-only|--anonymous-only] [--overwrite] [--quiet] [--workers=N]\n  wowcrucible client show <index-directory>\n  wowcrucible client fusion <base-root> <override-root>... [--output=plan.json] [--stage=review-folder] [--all]\n  wowcrucible client fusion-dbc-plan <fusion-plan.json> <schema.xml> [--output=dbc-plan.json] [--format=text|json] [--overwrite]\n  wowcrucible client fusion-dbc-apply <dbc-plan.json> <new-or-empty-output-folder>\n  wowcrucible client fusion-dbc-remap-plan <fusion-plan.json> <schema.xml> <WoWDBDefs-definitions> [--output=remap-plan.json] [--format=text|json] [--overwrite]\n  wowcrucible client fusion-dbc-remap-apply <remap-plan.json> <new-or-empty-output-folder>\n  wowcrucible client fusion-stage <fusion-plan.json> <stage-folder> [--dbc-receipt=client-fusion-dbc.crucible.json|--dbc-remap-receipt=client-fusion-dbc-remap.crucible.json]\n\nPublisher private keys are encrypted PKCS#8 files and must remain outside release bundles. Passwords come only from an environment variable. A trusted plan re-verifies the signed descriptor, public-key identity, manifest, every payload, target preimage, and ownership state again during apply. Unsigned local bundles remain available for private/offline work and are labeled LOCAL_UNSIGNED. Apply and rollback are dry-run unless --apply is explicit. Network transport remains separate and is not implied by signing.", code);

static async Task<int> Server(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return ServerHelp();
    if (args is ["client-plan", var planServerFolder, var clientDbcRoot, .. var planOptions])
    {
        var source = Option(planOptions, "--source="); var output = Option(planOptions, "--output="); var stage = Option(planOptions, "--stage=");
        var unknown = planOptions.Where(option => !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--stage=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown server client-plan option: {unknown[0]}");
        var planWorkspace = await ServerWorkspaceDetector.DetectAsync(planServerFolder);
        var plan = ClientServerDeploymentPlanner.Analyze(clientDbcRoot, planWorkspace, source);
        foreach (var entry in plan.Entries.Where(entry => entry.Status != ClientServerPlanStatus.Identical))
            Console.WriteLine($"{entry.Status}\t{entry.DbcFileName}\t{entry.Consumption}\t{entry.SqlTableName ?? "-"}\t{entry.Guidance}");
        if (output is not null) { ClientServerDeploymentPlanner.Save(output, plan); Console.Error.WriteLine($"Saved deployment plan: {Path.GetFullPath(output)}"); }
        if (stage is not null)
        {
            var result = ClientServerDeploymentPlanner.Stage(stage, plan);
            Console.Error.WriteLine($"Staged {result.ClientFiles:N0} client and {result.ServerFiles:N0} server DBC file(s); {result.BlockedFiles:N0} unresolved. Plan: {result.PlanPath}");
        }
        var blocked = plan.Entries.Count(entry => entry.Status is ClientServerPlanStatus.ConflictingClientLayers or ClientServerPlanStatus.InvalidDbc or ClientServerPlanStatus.UnknownConsumer or ClientServerPlanStatus.MissingServerDbc);
        Console.Error.WriteLine($"Client-to-server plan: {plan.Entries.Count:N0} table(s), {blocked:N0} blocked/unresolved.");
        return blocked == 0 ? 0 : 3;
    }
    if (args is ["dbc-apply", var applyServerFolder, var applyBundleRoot])
    {
        var applyWorkspace = await ServerWorkspaceDetector.DetectAsync(applyServerFolder);
        var result = await new DbcSqlDeploymentBundleService().ApplyAsync(applyBundleRoot, applyWorkspace.WorldDatabase, CancellationToken.None);
        Console.WriteLine($"RECEIPT\t{result.ReceiptPath}"); Console.WriteLine($"SERVER_SHA256\t{result.ServerSha256}");
        Console.WriteLine($"SQL_ROWS\t{result.SqlRows}"); Console.WriteLine($"RESTART\t{result.Restart}");
        Console.Error.WriteLine($"Verified synchronized deployment of {result.SqlRows:N0} SQL row(s) plus the server DBC. Receipt: {result.ReceiptPath}. Required next step: {result.Restart}.");
        return 0;
    }
    if (args is ["dbc-rollback", var rollbackServerFolder, var receiptPath])
    {
        var rollbackWorkspace = await ServerWorkspaceDetector.DetectAsync(rollbackServerFolder);
        var result = await new DbcSqlDeploymentBundleService().RollbackAsync(receiptPath, rollbackWorkspace.WorldDatabase, CancellationToken.None);
        Console.WriteLine($"RECEIPT\t{result.ReceiptPath}"); Console.WriteLine($"SQL_ROWS\t{result.SqlRows}"); Console.WriteLine($"RESTORED_SERVER_SHA256\t{result.RestoredServerSha256 ?? "<file removed>"}");
        Console.Error.WriteLine($"Verified rollback of {result.SqlRows:N0} SQL row(s) and the server DBC pre-image. Restart worldserver before runtime testing.");
        return 0;
    }
    if (args is ["dbc-module-export", var exportBundleRoot, var moduleRoot])
    {
        var path = new DbcSqlDeploymentBundleService().ExportModuleMigration(exportBundleRoot, moduleRoot); Console.WriteLine(path);
        Console.Error.WriteLine($"Exported the reviewed idempotent world migration without connecting to a database: {path}"); return 0;
    }
    if (args is ["bindings", var bindingFolder, .. var bindingOptions])
    {
        var source = Option(bindingOptions, "--source=");
        var unknown = bindingOptions.Where(option => !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown server bindings option: {unknown[0]}");
        var bindingWorkspace = await ServerWorkspaceDetector.DetectAsync(bindingFolder);
        foreach (var binding in ServerTableBindingCatalog.Resolve(bindingWorkspace.CoreFamily, source))
            Console.WriteLine($"{binding.Consumption}\t{binding.DbcFileName}\t{binding.SqlTableName ?? "-"}\t{binding.KeyStrategy.Kind}\t{binding.Restart}\t{binding.Profile}\t{binding.SupportedRevision}");
        return 0;
    }
    if (args is ["dbc-audit", var auditFolder, var dbcInput, var schemaPath, .. var auditOptions])
    {
        var source = Option(auditOptions, "--source="); var migration = Option(auditOptions, "--migration="); var bundleOutput = Option(auditOptions, "--bundle=");
        var showAll = auditOptions.Any(option => option.Equals("--all", StringComparison.OrdinalIgnoreCase)); var summaryOnly = auditOptions.Any(option => option.Equals("--summary", StringComparison.OrdinalIgnoreCase));
        var unknown = auditOptions.Where(option => !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--migration=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--bundle=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--all", StringComparison.OrdinalIgnoreCase) && !option.Equals("--summary", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown server dbc-audit option: {unknown[0]}");
        var auditWorkspace = await ServerWorkspaceDetector.DetectAsync(auditFolder);
        var dbcPath = File.Exists(dbcInput) ? Path.GetFullPath(dbcInput) : Path.Combine(auditWorkspace.DbcPath, Path.GetFileName(dbcInput));
        if (!File.Exists(dbcPath)) throw new FileNotFoundException("The DBC was not found in the detected server data folder.", dbcPath);
        var dbc = WdbcFile.Load(dbcPath); var resolution = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(Path.GetFileNameWithoutExtension(dbcPath), dbc.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(Path.GetFileNameWithoutExtension(dbcPath), dbc.FieldCount, resolution));
        var binding = ServerTableBindingCatalog.ApplySchemaKey(ServerTableBindingCatalog.ResolveFile(auditWorkspace.CoreFamily, dbcPath, source), resolution);
        PrintBinding(binding);
        if (binding.Consumption != ServerTableConsumption.SqlOverlayed || binding.SqlTableName is null) return binding.Consumption == ServerTableConsumption.Unknown ? 1 : 0;
        var capabilities = await new DatabaseCapabilityService().InspectAsync(auditWorkspace.WorldDatabase);
        var table = capabilities.FindTable(binding.SqlTableName) ?? throw new InvalidDataException($"The live world database has no expected overlay table {binding.SqlTableName}.");
        var audit = await new DbcSqlAuditService().AuditAsync(auditWorkspace.WorldDatabase, binding, dbcPath, resolution, table);
        if (!summaryOnly)
            foreach (var row in audit.Rows.Where(row => showAll || row.Status != DbcSqlRowStatus.Same))
                Console.WriteLine($"{row.Status}\t{row.Key}\t{row.Dimensions}\tDBC {FormatValues(row.DbcValues)}\tSQL {FormatValues(row.SqlValues)}");
        Console.Error.WriteLine($"Audited {audit.Rows.Count:N0} effective rows: {audit.Rows.Count(row => row.Status == DbcSqlRowStatus.Same):N0} same, {audit.Rows.Count(row => row.Status == DbcSqlRowStatus.SqlOverridesDbc):N0} SQL override(s), {audit.Rows.Count(row => row.Status == DbcSqlRowStatus.DbcOnly):N0} DBC-only, {audit.Rows.Count(row => row.Status == DbcSqlRowStatus.MissingDbcRow):N0} SQL-only/missing DBC. SQL overrides only rows that actually exist in the overlay.");
        if (migration is not null)
        {
            File.WriteAllText(migration, DbcSqlAuditService.CreateIdempotentMigration(audit));
            Console.Error.WriteLine($"Wrote idempotent DBC-to-SQL migration preview: {Path.GetFullPath(migration)}");
        }
        if (bundleOutput is not null)
        {
            var serverDbcPath = Path.Combine(auditWorkspace.DbcPath, Path.GetFileName(dbcPath));
            var bundle = new DbcSqlDeploymentBundleService().Create(bundleOutput, auditWorkspace.WorldDatabase, audit, resolution, schemaPath, serverDbcPath);
            Console.Error.WriteLine($"Created verified portable DBC/SQL deployment bundle for {bundle.Plan.Rows.Count:N0} row(s): {bundle.RootPath}");
        }
        return audit.MismatchCount == 0 ? 0 : 3;
    }
    if (args is not [var action, var folder] || action is not ("detect" or "inspect")) return ServerHelp(2);
    var workspace = await ServerWorkspaceDetector.DetectAsync(folder);
    Console.WriteLine($"Root\t{workspace.RootPath}"); Console.WriteLine($"Core\t{workspace.CoreFamily}");
    Console.WriteLine($"Config\t{workspace.ConfigLocation}"); Console.WriteLine($"DBC\t{workspace.DbcPath}");
    Console.WriteLine($"WorldDatabase\t{workspace.WorldDatabase.Database}"); Console.WriteLine($"DatabaseEndpoint\t{workspace.WorldDatabase.Host}:{workspace.WorldDatabase.Port}");
    Console.WriteLine($"DatabaseUser\t{workspace.WorldDatabase.User}"); Console.WriteLine($"Layout\t{(workspace.UsesWsl ? "WSL split" : "Native/local")}");
    if (action == "inspect")
    {
        var capabilities = await new DatabaseCapabilityService().InspectAsync(workspace.WorldDatabase);
        Console.WriteLine($"DatabaseServer\t{capabilities.ServerVersion}");
        foreach (var table in capabilities.Tables.Values.OrderBy(table => table.Name)) Console.WriteLine($"TABLE\t{table.Name}\t{table.Columns.Count} columns");
        foreach (var inspected in ServerTableBindingCatalog.AttachCapabilities(ServerTableBindingCatalog.BuiltIn(workspace.CoreFamily), capabilities).Where(item => item.Binding.Consumption == ServerTableConsumption.SqlOverlayed))
            Console.WriteLine($"DBC_BINDING\t{inspected.Binding.DbcFileName}\t{inspected.Binding.SqlTableName}\t{(inspected.ExpectedSqlTablePresent ? "ready" : "MISSING SQL TABLE")}");
        Console.Error.WriteLine($"Found {capabilities.DbcOverlayTables.Count:N0} live DBC SQL overlay table(s).");
    }
    return 0;
}

static int ServerHelp(int code = 0)
{
    var text = "Usage:\n  wowcrucible server detect <installed-server-folder>\n  wowcrucible server inspect <installed-server-folder>\n  wowcrucible server bindings <installed-server-folder> [--source=core-source]\n  wowcrucible server dbc-audit <installed-server-folder> <dbc-file-or-name> <schema.xml> [--source=core-source] [--all|--summary] [--migration=output.sql] [--bundle=folder]\n  wowcrucible server dbc-apply <installed-server-folder> <bundle-folder>\n  wowcrucible server dbc-rollback <installed-server-folder> <deployment-receipt.json>\n  wowcrucible server dbc-module-export <bundle-folder> <module-root>\n  wowcrucible server client-plan <installed-server-folder> <extracted-dbc-root> [--source=core-source] [--output=plan.json] [--stage=review-folder]";
    if (code == 0) Console.WriteLine(text); else Console.Error.WriteLine(text); return code;
}

static async Task<int> Database(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Console.WriteLine("  wowcrucible db pet-compare <host> <port> <user> <database> <left-creature> <right-creature> [--levels=1-80] [--metric=hp] [--output=report] [--overwrite] [--format=text|json] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db pet-preview <host> <port> <user> <database> <creature-entry> --dbc=folder [--schema=definitions.xml] [--library=processed-assets] [--format=text|json]\n  wowcrucible db pet-graph <host> <port> <user> <database> <creature-entry> --dbc=folder --schema=definitions.xml [--format=text|json]\n  wowcrucible db table-design <host> <port> <user> <database> <table> <add|modify|rename|drop|clone|rename-table|add-fk|drop-fk|add-check|drop-check> [column-or-constraint] [options] [--apply] [--format=text|json]"); return DatabaseHelp(); }
    if (args[0].Equals("draft-template", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 3) return Fail("db draft-template requires a supported authoring domain and an output JSON path."); var options = args[3..]; var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown draft-template option: {unknown[0]}");
        object draft = args[1].ToLowerInvariant() switch
        {
            "gameobject" or "go" => new GameObjectTemplateDraft(900000, 3, 0, "New Crucible Gameobject", "", "", "", 1, new long[24], "", ""),
            "creature" => new CreatureTemplateDraft(900000, "New Crucible Creature", "", [], 80, 80, 35, 0, 0, 7, 0, 1, 1, 1, 1.14286f, 1, 1, 1, 1, 2000, 2000, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", ""),
            "quest" => new QuestPortableDraft(QuestTemplateAdapter.CreateDefaultValues(QuestTemplateAdapter.CreatePortableTable()).ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase), new()),
            _ => CreateBehaviorDraft(args[1])
        };
        var path = Path.GetFullPath(args[2]); if (File.Exists(path) && !overwrite) return Fail($"Output already exists: {path}. Use --overwrite intentionally."); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(draft, draft.GetType(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, cancellationToken); Console.Error.WriteLine($"Draft template: {path}"); return 0;
    }
    if (args[0].Equals("recovery-audit", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 3) return DatabaseHelp(2);
        var auditOptions = args[3..];
        var baselineOptions = auditOptions.Where(option => option.StartsWith("--baseline=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (baselineOptions.Length > 1) return Fail("Specify --baseline only once.");
        var baseline = baselineOptions.Length == 0 ? null : baselineOptions[0][11..];
        if (baselineOptions.Length == 1 && string.IsNullOrWhiteSpace(baseline)) return Fail("--baseline requires a non-empty snapshot path.");
        var includes = auditOptions.Where(option => option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray();
        var excludes = auditOptions.Where(option => option.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray();
        var includeSensitive = auditOptions.Any(option => option.Equals("--include-sensitive", StringComparison.OrdinalIgnoreCase));
        var overwrite = auditOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = auditOptions.Where(option => !option.StartsWith("--baseline=", StringComparison.OrdinalIgnoreCase) &&
            !option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase) &&
            !option.Equals("--include-sensitive", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown recovery-audit option: {unknown[0]}");
        var progress = new Progress<LegacyDatabaseAuditProgress>(value =>
            Console.Error.WriteLine(value.Table is null ? value.Stage : $"{value.Stage}\t{value.CompletedTables:N0}/{value.TotalTables:N0}\t{value.Table}\t{value.Rows:N0} change record(s)"));
        var result = await new LegacyDatabaseAuditService().AuditAsync(args[1], args[2], baseline,
            new(includes, excludes, includeSensitive, overwrite), progress, cancellationToken);
        foreach (var table in result.Manifest.Tables.Where(table => table.Status != LegacyDatabaseTableAuditStatus.Unchanged))
        {
            Console.WriteLine($"TABLE\t{table.Domain}\t{table.Status}\t{table.Name}\t+{table.AddedRows}\t~{table.ModifiedRows}\t-{table.RemovedRows}\t?{table.UnattributedRows}\t{table.ChangedFields} fields");
            foreach (var finding in table.Findings) Console.Error.WriteLine($"FINDING\t{table.Name}\t{finding}");
        }
        foreach (var warning in result.Manifest.Warnings) Console.Error.WriteLine($"WARNING: {warning}");
        Console.Error.WriteLine($"Legacy SQL recovery audit complete: {result.Manifest.TotalChangeRecords:N0} row record(s), {result.Manifest.TotalChangedFields:N0} field value(s), {result.Manifest.Tables.Count:N0} table(s).\nMode: {result.Manifest.Mode}; baseline identity: {result.Manifest.BaselineIdentity}.\nArtifact: {result.Path}\nThis is read-only evidence, not executable SQL.");
        return RecoveryAuditNeedsReview(result.Manifest) ? 3 : 0;
    }
    if (args[0].Equals("recovery-inspect", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2) return DatabaseHelp(2);
        var inspectOptions = args[2..];
        var quick = inspectOptions.Any(option => option.Equals("--quick", StringComparison.OrdinalIgnoreCase));
        var unknown = inspectOptions.Where(option => !option.Equals("--quick", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown recovery-inspect option: {unknown[0]}");
        var inspection = await new LegacyDatabaseAuditService().InspectAsync(args[1], verifyChanges: !quick, cancellationToken);
        if (inspection.Manifest is { } manifest)
        {
            var tables = manifest.Tables ?? [];
            Console.WriteLine($"Format\t{manifest.Format}\t{manifest.FormatVersion}\nCreatedUtc\t{manifest.CreatedUtc:O}\nMode\t{manifest.Mode}\nBaselineIdentity\t{manifest.BaselineIdentity}\nTables\t{tables.Count}\nChangeRecords\t{manifest.TotalChangeRecords}\nChangedFields\t{manifest.TotalChangedFields}\nChangesSha256\t{manifest.ChangesSha256}\nPromotionReady\t{manifest.PromotionReady}");
            foreach (var table in tables)
            {
                Console.WriteLine($"TABLE\t{table.Domain}\t{table.Status}\t{table.Name}\t{table.ChangeRecords}\t{table.ChangedFields}\t{string.Join(',', table.PrimaryKey ?? [])}");
                foreach (var finding in table.Findings ?? []) Console.Error.WriteLine($"FINDING\t{table.Name}\t{finding}");
            }
            foreach (var warning in manifest.Warnings ?? []) Console.Error.WriteLine($"WARNING: {warning}");
        }
        foreach (var finding in inspection.Findings) Console.Error.WriteLine($"INVALID\t{finding}");
        Console.Error.WriteLine(inspection.Valid ? $"Recovery audit is valid ({(quick ? "hash-only" : "full change-record verification")})." : "Recovery audit validation failed.");
        return !inspection.Valid ? 3 : inspection.Manifest is { } validManifest && RecoveryAuditNeedsReview(validManifest) ? 3 : 0;
    }
    if (args[0].Equals("sync-inspect", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2) return DatabaseHelp(2); var options = args[2..]; var sqlOutput = Option(options, "--sql="); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--sql=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-inspect option: {unknown[0]}");
        var service = new DatabaseSynchronizationService(); var plan = await service.LoadPlanAsync(args[1], cancellationToken);
        Console.WriteLine($"Format\t{plan.Format}\t{plan.FormatVersion}\nCreatedUtc\t{plan.CreatedUtc:O}\nTarget\t{plan.Target.User}@{plan.Target.Host}:{plan.Target.Port}/{plan.Target.Database}\nTargetSchemaSha256\t{plan.TargetSchemaSha256 ?? "legacy-unbound"}\nTranslationProfile\t{plan.TranslationProfileName ?? "none"}\nTranslationProfileSha256\t{plan.TranslationProfileSha256 ?? "none"}\nTranslationRules\t{plan.SchemaTranslations?.Count ?? 0}\nOperations\t{plan.Operations.Count}\nIdRemaps\t{plan.IdRemaps.Count}\nDependencyClosure\t{plan.DependencyClosureIncluded}\nDependencyAdditions\t{plan.DependencyInclusions?.Count ?? 0}\nReady\t{plan.Ready}\nAlreadyApplied\t{plan.AlreadyApplied}\nConflicts\t{plan.Conflicts}\nBlocked\t{plan.Blocked}\nRemovalsIncluded\t{plan.RemovalsIncluded}\nContentSha256\t{plan.ContentSha256}");
        foreach (var translation in plan.SchemaTranslations ?? []) Console.WriteLine($"TRANSLATION\t{translation.Action}\t{translation.SourceTable}\t{translation.SourceColumn}\t{translation.TargetTable}\t{translation.TargetColumn}\t{translation.Operations}\t{translation.Description}");
        foreach (var inclusion in plan.DependencyInclusions ?? []) Console.WriteLine($"DEPENDENCY\t{inclusion.Relation}\t{(inclusion.Declared ? "declared" : "named-core")}\t{inclusion.IncludedIdentity}\tfrom={inclusion.SelectedIdentity}\t{inclusion.SelectedEndpoint}={inclusion.IncludedEndpoint}={inclusion.MatchedValue}\t{inclusion.Description}");
        foreach (var remap in plan.IdRemaps) Console.WriteLine($"REMAP\t{remap.Table}\t{remap.Column}\t{remap.SourceId}\t{remap.TargetId}\t{remap.RewrittenReferences}");
        foreach (var rowOperation in plan.Operations) Console.WriteLine($"ROW\t{rowOperation.Status}\t{rowOperation.Kind}\t{rowOperation.Domain}\t{rowOperation.Identity}\t{rowOperation.Finding}"); foreach (var warning in plan.Warnings) Console.Error.WriteLine($"WARNING: {warning}");
        if (sqlOutput is not null) { var output = Path.GetFullPath(sqlOutput); if (File.Exists(output) && !overwrite) return Fail($"SQL preview already exists: {output}. Use --overwrite intentionally."); Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, service.PreviewSql(plan), cancellationToken); Console.Error.WriteLine($"Non-committing SQL preview: {output}"); }
        return plan.Conflicts == 0 && plan.Blocked == 0 ? 0 : 3;
    }
    if (args[0].Equals("sync-bridge-inspect", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length != 2) return Fail("db sync-bridge-inspect requires exactly one bridge.json path.");
        var bridgeProfile = await new DatabaseSyncTranslationService().LoadAsync(args[1], cancellationToken); var unresolved = 0;
        Console.WriteLine($"Format\t{bridgeProfile.Format}\t{bridgeProfile.FormatVersion}\nName\t{bridgeProfile.Name}\nCreatedUtc\t{bridgeProfile.CreatedUtc:O}\nSourceAuditSha256\t{bridgeProfile.SourceAuditSha256}\nTargetSchemaSha256\t{bridgeProfile.TargetSchemaSha256}\nTargetServer\t{bridgeProfile.TargetServerVersion}\nTables\t{bridgeProfile.Tables.Count}");
        foreach (var table in bridgeProfile.Tables)
        {
            var tableBlocked = !table.SuppressPrimaryOutput && string.IsNullOrWhiteSpace(table.TargetTable); if (tableBlocked) unresolved++;
            if (table.SuppressPrimaryOutput && (table.Expansions?.Count ?? 0) == 0) unresolved++;
            var tableTarget = table.SuppressPrimaryOutput ? "SUPPRESSED" : tableBlocked ? "UNRESOLVED" : table.TargetTable;
            Console.WriteLine($"TABLE\t{table.SourceTable}\t{tableTarget}\tobserved={table.ObservedSourceColumns?.Count ?? 0}\tkeys={string.Join(',', table.SourcePrimaryKeyColumns ?? [])}\tinsert-defaults={table.RequiresInsertDefaults}\tsuppress-primary={table.SuppressPrimaryOutput}\texpansions={table.Expansions?.Count ?? 0}");
            foreach (var mapping in table.ColumnMappings) { var blocked = string.IsNullOrWhiteSpace(mapping.TargetColumn); if (blocked) unresolved++; Console.WriteLine($"COLUMN\t{table.SourceTable}.{mapping.SourceColumn}\t{(blocked ? "UNRESOLVED" : table.TargetTable + "." + mapping.TargetColumn)}"); }
            foreach (var drop in table.DroppedSourceColumns) { var keyDrop = (table.SourcePrimaryKeyColumns ?? []).Contains(drop, StringComparer.OrdinalIgnoreCase); if (keyDrop) unresolved++; Console.WriteLine($"DROP\t{table.SourceTable}.{drop}\t{(keyDrop ? "BLOCKED_KEY" : "REVIEWED")}"); }
            foreach (var targetDefault in table.TargetDefaults) { var blocked = targetDefault.Value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing; if (blocked) unresolved++; Console.WriteLine($"DEFAULT\t{table.TargetTable}.{targetDefault.TargetColumn}\t{(blocked ? "UNRESOLVED" : targetDefault.Value.State)}\t{targetDefault.Value.Value}"); }
            foreach (var expansion in table.Expansions ?? [])
            {
                var blocked = string.IsNullOrWhiteSpace(expansion.TargetTable) || expansion.SourceKinds.Count == 0 || expansion.KeyBindings.Count == 0 ||
                    (expansion.TargetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Removed) && expansion.FieldBindings.Count == 0; if (blocked) unresolved++;
                Console.WriteLine($"EXPANSION\t{table.SourceTable}/{expansion.Name}\t{(blocked ? "UNRESOLVED" : expansion.TargetTable)}\tsource={string.Join(',', expansion.SourceKinds)}\ttarget={expansion.TargetKind}\tkeys={expansion.KeyBindings.Count}\tfields={expansion.FieldBindings.Count}");
                foreach (var key in expansion.KeyBindings) { if (UnresolvedExpansionValue(key.Value)) unresolved++; Console.WriteLine($"EXPANSION_KEY\t{expansion.TargetTable}.{key.TargetColumn}\t{DescribeExpansionValue(key.Value)}"); }
                foreach (var field in expansion.FieldBindings)
                {
                    var beforeRequired = expansion.TargetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Removed; var afterRequired = expansion.TargetKind is LegacyDatabaseRowChangeKind.Modified or LegacyDatabaseRowChangeKind.Added;
                    if ((beforeRequired && (field.Before is null || UnresolvedExpansionValue(field.Before))) || (afterRequired && (field.After is null || UnresolvedExpansionValue(field.After)))) unresolved++;
                    Console.WriteLine($"EXPANSION_FIELD\t{expansion.TargetTable}.{field.TargetColumn}\tbefore={DescribeExpansionValue(field.Before)}\tafter={DescribeExpansionValue(field.After)}");
                }
            }
        }
        Console.Error.WriteLine(unresolved == 0 ? "Schema bridge is structurally resolved; sync-plan will still verify its audit and live target-schema bindings." : $"Schema bridge has {unresolved:N0} unresolved or invalid selection(s); planning will block or refuse it."); return unresolved == 0 ? 0 : 3;

        static bool UnresolvedExpansionValue(DatabaseSyncExpansionValue value) => value.Source == DatabaseSyncExpansionValueSource.Constant
            ? value.Constant is null || value.Constant.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing
            : string.IsNullOrWhiteSpace(value.SourceColumn);
        static string DescribeExpansionValue(DatabaseSyncExpansionValue? value) => value is null ? "UNRESOLVED" : value.Source == DatabaseSyncExpansionValueSource.Constant
            ? $"Constant:{value.Constant?.State}:{value.Constant?.Value}" : $"{value.Source}:{value.SourceColumn}";
    }
    if (args[0].Equals("snapshot-inspect", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2) return DatabaseHelp(2);
        var inspectOptions = args[2..];
        var quick = inspectOptions.Any(option => option.Equals("--quick", StringComparison.OrdinalIgnoreCase));
        var unknown = inspectOptions.Where(option => !option.Equals("--quick", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown snapshot-inspect option: {unknown[0]}");
        var inspection = await new LegacyDatabaseSnapshotService().InspectAsync(args[1], verifyRows: !quick, cancellationToken);
        if (inspection.Manifest is { } manifest)
        {
            Console.WriteLine($"Format\t{manifest.Format}\t{manifest.FormatVersion}\nCapturedUtc\t{manifest.CapturedUtc:O}\nDatabase\t{manifest.Source?.Database ?? "<missing>"}\nServer\t{manifest.Source?.ServerVersion ?? "<missing>"}\nTables\t{manifest.Tables?.Count ?? 0}\nRows\t{manifest.TotalRows}\nSchemaSha256\t{manifest.SchemaSha256}\nContentSha256\t{manifest.ContentSha256}\nConsistentSnapshot\t{manifest.ConsistentSnapshotStarted}\nReadOnlyTransaction\t{manifest.ReadOnlyTransactionEnforced}");
            if (manifest.Source?.CoreIdentity is not null) foreach (var identity in manifest.Source.CoreIdentity) Console.WriteLine($"CORE\t{identity.Key}\t{identity.Value}");
            if (manifest.Tables is not null) foreach (var table in manifest.Tables) Console.WriteLine($"TABLE\t{table.Name}\t{table.Rows}\t{table.Columns?.Count ?? 0}\t{string.Join(',', table.PrimaryKey ?? [])}\t{table.RowsSha256}");
        }
        foreach (var finding in inspection.Findings) Console.Error.WriteLine($"INVALID\t{finding}");
        Console.Error.WriteLine(inspection.Valid ? $"Snapshot is valid ({(quick ? "hash-only" : "full row-structure verification")})." : "Snapshot validation failed.");
        return inspection.Valid ? 0 : 3;
    }
    if (args.Length < 5) return DatabaseHelp(2);
    var operation = args[0]; var host = args[1]; var portText = args[2]; var user = args[3]; var database = args[4];
    if (operation.Equals("snapshot", StringComparison.OrdinalIgnoreCase) && (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)))
        return Fail("db snapshot requires an output artifact path after the database name.");
    if (!uint.TryParse(portText, out var port) || port is 0 or > 65535) return Fail("Database port must be from 1 to 65535.");
    var rawOptions = operation.Equals("snapshot", StringComparison.OrdinalIgnoreCase) && args.Length >= 6 ? args[6..] : args[5..];
    var passwordEnvironment = rawOptions.FirstOrDefault(option => option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase))?[15..] ?? "WOW_CRUCIBLE_DB_PASSWORD";
    var sslText = rawOptions.FirstOrDefault(option => option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase))?[6..] ?? "Preferred";
    if (string.IsNullOrWhiteSpace(passwordEnvironment)) return Fail("--password-env requires a non-empty environment-variable name.");
    var password = Environment.GetEnvironmentVariable(passwordEnvironment);
    if (password is null) return Fail($"Set the {passwordEnvironment} environment variable for this process. Passwords are not accepted on the command line.");
    if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(sslText, true, out var ssl)) return Fail($"Unknown SSL mode: {sslText}");
    var profile = new DatabaseConnectionProfile(host, port, user, password, database, ssl);
    if (operation.Equals("table-design", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db table-design requires <table> <operation> after the database name.");
        var tableName = args[5]; var action = args[6].ToLowerInvariant();
        var needsIdentity = action is "add" or "modify" or "rename" or "drop" or "add-fk" or "drop-fk" or "add-check" or "drop-check";
        var columnName = needsIdentity && args.Length > 7 && !args[7].StartsWith("--", StringComparison.Ordinal) ? args[7] : null;
        if (needsIdentity && string.IsNullOrWhiteSpace(columnName)) return Fail($"db table-design {action} requires a column or constraint name.");
        var optionStart = columnName is null ? 7 : 8; var options = args[optionStart..];
        var name = Option(options, "--name="); var definition = Option(options, "--definition="); var after = Option(options, "--after=");
        var columns = Option(options, "--columns="); var references = Option(options, "--references="); var referenceColumns = Option(options, "--reference-columns="); var deleteRule = Option(options, "--delete="); var updateRule = Option(options, "--update="); var expression = Option(options, "--expression=");
        var first = options.Any(option => option.Equals("--first", StringComparison.OrdinalIgnoreCase)); var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--name=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--definition=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--after=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--columns=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--references=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--reference-columns=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--delete=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--update=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--expression=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--first", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown table-design option: {unknown[0]}");
        if (first && after is not null) return Fail("Choose either --first or --after, not both.");
        var kind = action switch { "add" => SqlTableDesignOperation.AddColumn, "modify" => SqlTableDesignOperation.ModifyColumn, "rename" => SqlTableDesignOperation.RenameColumn, "drop" => SqlTableDesignOperation.DropColumn, "clone" => SqlTableDesignOperation.CloneStructure, "rename-table" => SqlTableDesignOperation.RenameTable, "add-fk" => SqlTableDesignOperation.AddForeignKey, "drop-fk" => SqlTableDesignOperation.DropForeignKey, "add-check" => SqlTableDesignOperation.AddCheckConstraint, "drop-check" => SqlTableDesignOperation.DropCheckConstraint, _ => throw new ArgumentException("table-design operation must be add, modify, rename, drop, clone, rename-table, add-fk, drop-fk, add-check, or drop-check.") };
        var placement = first ? SqlColumnPlacement.First : after is not null ? SqlColumnPlacement.After : SqlColumnPlacement.End;
        var request = kind switch
        {
            SqlTableDesignOperation.AddColumn => new SqlTableDesignRequest(kind, NewName: columnName, Definition: definition, Placement: placement, AfterColumn: after),
            SqlTableDesignOperation.ModifyColumn => new SqlTableDesignRequest(kind, ColumnName: columnName, Definition: definition, Placement: placement, AfterColumn: after),
            SqlTableDesignOperation.RenameColumn => new SqlTableDesignRequest(kind, ColumnName: columnName, NewName: name, Definition: definition, Placement: placement, AfterColumn: after),
            SqlTableDesignOperation.DropColumn => new SqlTableDesignRequest(kind, ColumnName: columnName),
            SqlTableDesignOperation.AddForeignKey => new SqlTableDesignRequest(kind, NewName: columnName, Columns: ParseList(columns), ReferencedTable: references, ReferencedColumns: ParseList(referenceColumns), DeleteRule: deleteRule, UpdateRule: updateRule),
            SqlTableDesignOperation.DropForeignKey => new SqlTableDesignRequest(kind, ColumnName: columnName),
            SqlTableDesignOperation.AddCheckConstraint => new SqlTableDesignRequest(kind, NewName: columnName, CheckExpression: expression),
            SqlTableDesignOperation.DropCheckConstraint => new SqlTableDesignRequest(kind, ColumnName: columnName),
            _ => new SqlTableDesignRequest(kind, NewName: name)
        };
        var service = new SqlTableDesignerService(); var plan = await service.PrepareAsync(profile, tableName, request, cancellationToken);
        if (json && !apply) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else if (!apply) { Console.WriteLine(plan.Sql); foreach (var warning in plan.Warnings) Console.Error.WriteLine($"WARNING: {warning}"); Console.Error.WriteLine("Dry-run only. Re-run with --apply after reviewing the exact target, DDL, and warnings."); }
        if (!apply) return 0;
        var result = await service.ApplyAsync(profile, plan, cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else Console.WriteLine($"APPLIED\t{result.Receipt.Operation}\t{result.Receipt.Database}.{result.Receipt.SourceTable}\t{result.Receipt.ResultTable}\nRECEIPT\t{result.ReceiptPath}\nBEFORE_SHA256\t{result.Receipt.BeforeCreateSqlSha256}\nAFTER_SHA256\t{result.Receipt.AfterCreateSqlSha256}");
        return 0;
    }
    if (operation.Equals("pet-graph", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || !uint.TryParse(args[5], out var creatureEntry) || creatureEntry == 0) return Fail("db pet-graph requires a positive creature entry after the database name.");
        var options = args[6..]; var dbc = Option(options, "--dbc="); var schema = Option(options, "--schema="); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--schema=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown pet-graph option: {unknown[0]}");
        if (string.IsNullOrWhiteSpace(dbc) || !Directory.Exists(dbc)) return Fail("--dbc must point to the server DBC folder.");
        if (string.IsNullOrWhiteSpace(schema) || !File.Exists(schema)) return Fail("--schema must point to the WotLK 3.3.5a (12340) definitions XML.");
        var graph = await new PetAbilityGraphService().BuildAsync(profile, dbc, schema, creatureEntry, cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(graph, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"CREATURE\t{graph.CreatureEntry}\t{graph.CreatureName}\tfamily={graph.FamilyId}\tfamily-name={graph.FamilyName}\tpet-talent-type={graph.PetTalentType}");
            foreach (var node in graph.Nodes) Console.WriteLine($"NODE\t{node.Id}\t{node.Kind}\t{node.NumericId}\t{node.Label}\t{node.Detail}\ttier={node.Tier?.ToString() ?? "-"}\tcolumn={node.Column?.ToString() ?? "-"}");
            foreach (var edge in graph.Edges) Console.WriteLine($"EDGE\t{edge.From}\t{edge.Relation}\t{edge.To}\t{edge.Evidence}");
            foreach (var finding in graph.Findings) Console.WriteLine($"FINDING\t{finding}");
        }
        var spellCount = graph.Nodes.Count(node => node.Kind == PetAbilityNodeKind.Spell);
        Console.Error.WriteLine($"Resolved {graph.Nodes.Count:N0} graph node(s), {spellCount:N0} unique spell(s), and {graph.Edges.Count:N0} evidence edge(s) for creature {creatureEntry:N0}.");
        return spellCount == 0 ? 3 : 0;
    }
    if (operation.Equals("pet-preview", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || !uint.TryParse(args[5], out var creatureEntry) || creatureEntry == 0) return Fail("db pet-preview requires a positive creature entry after the database name.");
        var options = args[6..]; var dbc = Option(options, "--dbc="); var schema = Option(options, "--schema="); var library = Option(options, "--library="); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--schema=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--library=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown pet-preview option: {unknown[0]}");
        if (string.IsNullOrWhiteSpace(dbc) || !Directory.Exists(dbc)) return Fail("--dbc must point to the server DBC folder containing CreatureDisplayInfo.dbc and CreatureModelData.dbc.");
        if (schema is not null && !File.Exists(schema)) return Fail($"Creature preview schema does not exist: {Path.GetFullPath(schema)}");
        if (library is not null && !Directory.Exists(library)) return Fail($"Processed asset library does not exist: {Path.GetFullPath(library)}");
        var resolved = (await new CreatureDisplayPreviewService().ResolveCreaturesAsync(profile, dbc, schema, library, [creatureEntry], cancellationToken)).Single();
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(resolved, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"CREATURE\t{resolved.CreatureEntry}\t{resolved.Name}\t{resolved.Finding}");
            foreach (var display in resolved.Displays)
            {
                Console.WriteLine($"DISPLAY\t{display.DisplayId}\tmodel={display.ModelId}\tpath={display.ModelClientPath}\tdisplay-scale={display.DisplayScale:0.###}\tmodel-scale={display.ModelScale:0.###}\t{display.Finding}");
                foreach (var source in display.Sources) Console.WriteLine($"SOURCE\t{(source.Ready ? "READY" : "MISSING_SKIN")}\t{source.Provenance}\t{source.ModelPath}\t{source.SkinPath}\ttextures={source.CreatureTextures.Count}");
            }
        }
        var ready = resolved.Displays.Sum(display => display.Sources.Count(source => source.Ready)); Console.Error.WriteLine($"Resolved {resolved.Displays.Count:N0} display(s) and {ready:N0} ready same-provenance M2/SKIN source(s) for creature {creatureEntry:N0}.");
        return library is not null && ready == 0 ? 3 : resolved.Displays.Count == 0 ? 3 : 0;
    }
    if (operation.Equals("pet-curve", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db pet-curve requires <source-creature> <target-creature> after the database name.");
        if (!uint.TryParse(args[5], out var sourceCreature) || sourceCreature == 0 || !uint.TryParse(args[6], out var targetCreature) || targetCreature == 0) return Fail("Pet curve source and target creature entries must be positive unsigned integers.");
        var options = args[7..]; var levels = Option(options, "--levels=") ?? "1-80"; var parts = levels.Split('-', 2, StringSplitOptions.TrimEntries); if (parts.Length != 2 || !byte.TryParse(parts[0], out var startLevel) || !byte.TryParse(parts[1], out var endLevel) || startLevel == 0 || endLevel < startLevel) return Fail("--levels must be an inclusive range such as 1-80 within 1-255.");
        static decimal ScaleOption(string? text, string name) { if (text is null) return 1m; if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)) throw new ArgumentException($"--{name} must be an invariant decimal number."); return value; }
        var health = ScaleOption(Option(options, "--health="), "health"); var mana = ScaleOption(Option(options, "--mana="), "mana"); var armor = ScaleOption(Option(options, "--armor="), "armor"); var attributes = ScaleOption(Option(options, "--attributes="), "attributes"); var damage = ScaleOption(Option(options, "--damage="), "damage");
        var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var update = options.Any(option => option.Equals("--update-existing", StringComparison.OrdinalIgnoreCase)); var output = Option(options, "--output="); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--levels=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--health=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--mana=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--armor=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--attributes=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--damage=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--update-existing", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown pet-curve option: {unknown[0]}"); if (update && !apply) return Fail("--update-existing changes exact target rows and therefore also requires --apply.");
        var service = new PetLevelCurveService(); var request = new PetLevelCurveRequest(sourceCreature, targetCreature, startLevel, endLevel, new(health, mana, armor, attributes, damage)); var prepared = await service.PrepareAsync(profile, request, cancellationToken); var mode = update ? PetLevelCurveWriteMode.UpdateExactRange : PetLevelCurveWriteMode.InsertMissing; var sql = service.PreviewSql(prepared, mode) + Environment.NewLine; var existing = prepared.ExpectedTargetRows.Count(pair => pair.Value is not null); var missing = prepared.ExpectedTargetRows.Count - existing;
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Request = request, Mode = mode, Rows = prepared.Content.Rows.Count, ExistingTargetRows = existing, MissingTargetRows = missing, prepared.Content.OmittedFields, Sql = sql }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })); else Console.Write(sql);
        if (output is not null) { var path = Path.GetFullPath(output); if (File.Exists(path) && !overwrite) return Fail($"Pet curve SQL already exists: {path}. Use --overwrite intentionally."); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, sql, cancellationToken); Console.Error.WriteLine($"Pet curve SQL: {path}"); }
        Console.Error.WriteLine($"Prepared {prepared.Content.Rows.Count:N0} source-backed level row(s) for creature {targetCreature}: {existing:N0} existing, {missing:N0} missing. Policy: {mode}.");
        if (!apply) { Console.Error.WriteLine("Dry-run only. Re-run with --apply to insert missing rows; add --update-existing only when the reviewed range should replace exact existing levels."); return 0; }
        var result = await service.ApplyAsync(profile, prepared, mode, cancellationToken); Console.Error.WriteLine($"Committed pet curve transactionally: {result.Inserted:N0} inserted, {result.Updated:N0} updated, {result.Skipped:N0} preserved."); return 0;
    }
    if (operation.Equals("pet-compare", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db pet-compare requires <left-creature> <right-creature> after the database name.");
        if (!uint.TryParse(args[5], out var leftCreature) || leftCreature == 0 || !uint.TryParse(args[6], out var rightCreature) || rightCreature == 0) return Fail("Pet comparison creature entries must be positive unsigned integers.");
        var options = args[7..]; var levels = Option(options, "--levels=") ?? "1-80"; var parts = levels.Split('-', 2, StringSplitOptions.TrimEntries); if (parts.Length != 2 || !byte.TryParse(parts[0], out var startLevel) || !byte.TryParse(parts[1], out var endLevel) || startLevel == 0 || endLevel < startLevel) return Fail("--levels must be an inclusive range such as 1-80 within 1-255.");
        var metricName = Option(options, "--metric="); var output = Option(options, "--output="); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--levels=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--metric=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown pet-compare option: {unknown[0]}");
        var comparison = await new PetLevelCurveService().CompareAsync(profile, new(leftCreature, rightCreature, startLevel, endLevel), cancellationToken); var selected = metricName is null ? null : comparison.Metrics.FirstOrDefault(metric => metric.Column.Equals(metricName, StringComparison.OrdinalIgnoreCase)); if (metricName is not null && selected is null) return Fail($"Unknown pet comparison metric '{metricName}'. Available: {string.Join(", ", comparison.Metrics.Select(metric => metric.Column))}");
        static string PetPercent(decimal? value) => value is null ? "n/a" : value.Value.ToString("+0.###;-0.###;0", System.Globalization.CultureInfo.InvariantCulture) + "%"; static string PetNumber(decimal? value) => value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "missing";
        string rendered;
        if (json) { object payload = selected is null ? comparison : new { comparison.Request, Metric = selected, comparison.MissingLeftLevels, comparison.MissingRightLevels }; rendered = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
        else
        {
            var lines = new List<string> { $"COMPARE\t{leftCreature}\t{rightCreature}\t{startLevel}-{endLevel}\tleft-missing={comparison.MissingLeftLevels.Count}\tright-missing={comparison.MissingRightLevels.Count}" };
            foreach (var metric in selected is null ? comparison.Metrics : [selected]) { lines.Add($"METRIC\t{metric.Column}\t{metric.Display}\tleft-growth={PetPercent(metric.LeftGrowthPercent)}\tright-growth={PetPercent(metric.RightGrowthPercent)}\tend-delta={PetPercent(metric.EndDeltaPercent)}\taverage-delta={PetPercent(metric.AverageDeltaPercent)}\tpaired={metric.PairedLevels}"); if (selected is not null) { lines.Add("LEVEL\tLEFT\tRIGHT\tRIGHT_VS_LEFT"); lines.AddRange(metric.Points.Select(point => $"{point.Level}\t{PetNumber(point.Left)}\t{PetNumber(point.Right)}\t{PetPercent(point.DeltaPercent)}")); } }
            if (comparison.MissingLeftLevels.Count > 0) lines.Add($"MISSING_LEFT\t{string.Join(',', comparison.MissingLeftLevels)}"); if (comparison.MissingRightLevels.Count > 0) lines.Add($"MISSING_RIGHT\t{string.Join(',', comparison.MissingRightLevels)}"); rendered = string.Join(Environment.NewLine, lines);
        }
        Console.WriteLine(rendered); if (output is not null) { var path = Path.GetFullPath(output); if (File.Exists(path) && !overwrite) return Fail($"Pet comparison output already exists: {path}. Use --overwrite intentionally."); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, rendered + Environment.NewLine, cancellationToken); Console.Error.WriteLine($"Pet comparison: {path}"); }
        Console.Error.WriteLine($"Compared {comparison.Metrics.Count:N0} numeric stat column(s), {comparison.MissingLeftLevels.Count:N0} left gap(s), and {comparison.MissingRightLevels.Count:N0} right gap(s)."); return comparison.MissingLeftLevels.Count == 0 && comparison.MissingRightLevels.Count == 0 ? 0 : 3;
    }
    if (operation.Equals("favorites", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[5..]; var search = Option(options, "--search="); var verify = options.Any(option => option.Equals("--verify", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--search=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--verify", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown favorites option: {unknown[0]}");
        var favorites = SqlFavoriteStore.Load().Where(favorite => SqlFavoriteWorkspaceService.Matches(favorite, search)).ToArray();
        IReadOnlyList<SqlFavoriteVerification> checks = verify
            ? await new SqlFavoriteWorkspaceService().VerifyAsync(profile, favorites, cancellationToken)
            : favorites.Select(favorite => new SqlFavoriteVerification(favorite.Identity, SqlFavoriteVerificationState.Unchecked, "Not checked; add --verify for exact live primary-key validation.", DateTimeOffset.MinValue)).ToArray();
        var byIdentity = checks.ToDictionary(check => check.Identity, StringComparer.OrdinalIgnoreCase);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(favorites.Select(favorite => new { Favorite = favorite, Verification = byIdentity[favorite.Identity] }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else foreach (var favorite in favorites) { var check = byIdentity[favorite.Identity]; Console.WriteLine($"{check.Display}\t{favorite.Database}\t{favorite.Table}\t{string.Join(",", favorite.Key.Select(pair => $"{pair.Key}={pair.Value}"))}\t{favorite.Label}\t{favorite.Notes}\t{check.Detail}"); }
        Console.Error.WriteLine($"{favorites.Length:N0} favorite(s){(verify ? $" · {checks.Count(check => check.State == SqlFavoriteVerificationState.Live):N0} live · {checks.Count(check => check.State == SqlFavoriteVerificationState.Missing):N0} missing · {checks.Count(check => check.State is SqlFavoriteVerificationState.SchemaMismatch or SqlFavoriteVerificationState.Error):N0} changed/failed" : string.Empty)}. Store: {CruciblePaths.SqlFavoritesFile}");
        return verify && checks.Any(check => check.State != SqlFavoriteVerificationState.Live) ? 3 : 0;
    }
    if (operation.Equals("sync-bridge", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db sync-bridge requires <verified-audit> <output-bridge.json>.");
        var options = args[7..]; var includes = options.Where(option => option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray(); var maximumText = Option(options, "--maximum=") ?? "100000"; var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--maximum=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-bridge option: {unknown[0]}"); if (!int.TryParse(maximumText, out var maximum) || maximum is < 1 or > 1_000_000) return Fail("--maximum must be from 1 through 1,000,000.");
        var progress = new Progress<(string Stage, string? Table, int Completed, int Total)>(value => Console.Error.WriteLine(value.Table is null ? value.Stage : $"{value.Stage}\t{value.Completed:N0}/{value.Total:N0}\t{value.Table}"));
        var result = await new DatabaseSynchronizationService().BuildTranslationTemplateAsync(args[5], profile, args[6], includes, maximum, overwrite, progress, cancellationToken);
        var unresolvedTables = result.Profile.Tables.Count(table => string.IsNullOrWhiteSpace(table.TargetTable));
        var unresolvedColumns = result.Profile.Tables.Sum(table => table.ColumnMappings.Count(mapping => string.IsNullOrWhiteSpace(mapping.TargetColumn)) + table.TargetDefaults.Count(value => value.Value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing));
        foreach (var table in result.Profile.Tables) Console.WriteLine($"TABLE\t{table.SourceTable}\t{(string.IsNullOrWhiteSpace(table.TargetTable) ? "UNRESOLVED" : table.TargetTable)}\tcolumn-gaps={table.ColumnMappings.Count(mapping => string.IsNullOrWhiteSpace(mapping.TargetColumn))}\trequired-defaults={table.TargetDefaults.Count(value => value.Value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing)}");
        Console.Error.WriteLine($"Schema bridge template: {result.Profile.Tables.Count:N0} table rule(s), {unresolvedTables:N0} unresolved table(s), {unresolvedColumns:N0} unresolved column/default value(s), {result.Operations:N0} audited operation(s). Artifact: {result.Path}");
        return unresolvedTables == 0 && unresolvedColumns == 0 ? 0 : 3;
    }
    if (operation.Equals("sync-plan", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db sync-plan requires <verified-audit> <output-plan.json>.");
        var options = args[7..]; var includes = options.Where(option => option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray(); var removals = options.Any(option => option.Equals("--include-removals", StringComparison.OrdinalIgnoreCase)); var dependencyClosure = options.Any(option => option.Equals("--dependency-closure", StringComparison.OrdinalIgnoreCase)); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)); var autoRemap = options.Any(option => option.Equals("--auto-remap", StringComparison.OrdinalIgnoreCase)); var maximumText = Option(options, "--maximum=") ?? "100000"; var remapStartText = Option(options, "--remap-start="); var translation = Option(options, "--translation=");
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--maximum=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--remap-start=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--translation=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--include-removals", StringComparison.OrdinalIgnoreCase) && !option.Equals("--dependency-closure", StringComparison.OrdinalIgnoreCase) && !option.Equals("--auto-remap", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-plan option: {unknown[0]}"); if (!int.TryParse(maximumText, out var maximum) || maximum is < 1 or > 1_000_000) return Fail("--maximum must be from 1 through 1,000,000."); if (remapStartText is not null && (!uint.TryParse(remapStartText, out _) || remapStartText == "0")) return Fail("--remap-start must be a positive unsigned integer."); uint? remapStart = remapStartText is null ? null : uint.Parse(remapStartText);
        var progress = new Progress<(string Stage, string? Table, int Completed, int Total)>(value => Console.Error.WriteLine(value.Table is null ? value.Stage : $"{value.Stage}\t{value.Completed:N0}/{value.Total:N0}\t{value.Table}"));
        var result = await new DatabaseSynchronizationService().BuildPlanAsync(args[5], profile, args[6], new(includes, removals, maximum, overwrite, autoRemap, remapStart, dependencyClosure, translation), progress, cancellationToken); var plan = result.Plan;
        foreach (var item in plan.SchemaTranslations ?? []) Console.WriteLine($"TRANSLATION\t{item.Action}\t{item.SourceTable}\t{item.SourceColumn}\t{item.TargetTable}\t{item.TargetColumn}\t{item.Operations}\t{item.Description}");
        foreach (var inclusion in plan.DependencyInclusions ?? []) Console.WriteLine($"DEPENDENCY\t{inclusion.Relation}\t{(inclusion.Declared ? "declared" : "named-core")}\t{inclusion.IncludedIdentity}\tfrom={inclusion.SelectedIdentity}\t{inclusion.SelectedEndpoint}={inclusion.IncludedEndpoint}={inclusion.MatchedValue}");
        foreach (var remap in plan.IdRemaps) Console.WriteLine($"REMAP\t{remap.Table}\t{remap.Column}\t{remap.SourceId}\t{remap.TargetId}\t{remap.RewrittenReferences}");
        foreach (var rowOperation in plan.Operations) Console.WriteLine($"ROW\t{rowOperation.Status}\t{rowOperation.Kind}\t{rowOperation.Domain}\t{rowOperation.Identity}\t{rowOperation.Finding}");
        Console.Error.WriteLine($"Target comparison plan: {plan.Ready:N0} ready, {plan.AlreadyApplied:N0} already applied, {plan.Conflicts:N0} conflict(s), {plan.Blocked:N0} blocked. Artifact: {result.Path}"); return plan.Conflicts == 0 && plan.Blocked == 0 ? 0 : 3;
    }
    if (operation.Equals("sync-apply", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db sync-apply requires <plan.json> <receipt.json>."); var options = args[7..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-apply option: {unknown[0]}"); var service = new DatabaseSynchronizationService(); var plan = await service.LoadPlanAsync(args[5], cancellationToken);
        Console.WriteLine($"Target\t{plan.Target.User}@{plan.Target.Host}:{plan.Target.Port}/{plan.Target.Database}\nReady\t{plan.Ready}\nAlreadyApplied\t{plan.AlreadyApplied}\nConflicts\t{plan.Conflicts}\nBlocked\t{plan.Blocked}"); if (!apply) { Console.Error.WriteLine("Dry-run only. Re-run with --apply only after the target binding, conflicts, and receipt path are reviewed."); return plan.Conflicts == 0 && plan.Blocked == 0 ? 0 : 3; }
        var result = await service.ApplyAsync(args[5], profile, args[6], overwrite, cancellationToken); Console.Error.WriteLine($"Committed {result.Applied:N0} exact row operation(s); {result.AlreadyApplied:N0} were already applied. Rollback receipt: {result.ReceiptPath}"); return 0;
    }
    if (operation.Equals("sync-rollback", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db sync-rollback requires <receipt.json>."); var options = args[6..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown sync-rollback option: {unknown[0]}");
        if (!apply) { Console.Error.WriteLine("Dry-run only. This receipt is unchanged; re-run with --apply to revalidate every postimage and roll back transactionally."); return 0; } var result = await new DatabaseSynchronizationService().RollbackAsync(args[5], profile, cancellationToken); Console.Error.WriteLine($"Rolled back {result.Applied:N0} exact row operation(s); {result.AlreadyApplied:N0} were already at their pre-apply state. Receipt marked rolled back: {result.ReceiptPath}"); return 0;
    }
    if (operation.Equals("schemas", StringComparison.OrdinalIgnoreCase))
    {
        var unknown = args[5..].Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown schemas option: {unknown[0]}");
        var schemas = await new SqlWorkspaceService().ListDatabasesAsync(profile, cancellationToken); foreach (var schema in schemas) Console.WriteLine(schema); Console.Error.WriteLine($"{schemas.Count:N0} accessible database schema(s)."); return 0;
    }
    if (operation.Equals("rows", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db rows requires a table name.");
        var options = args[6..]; var search = Option(options, "--search="); var filterText = Option(options, "--filter="); var sort = Option(options, "--sort=");
        var limitText = Option(options, "--limit=") ?? "200"; var offsetText = Option(options, "--offset=") ?? "0"; var descending = options.Any(option => option.Equals("--descending", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--search=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--sort=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--offset=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--descending", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown rows option: {unknown[0]}"); if (!int.TryParse(limitText, out var limit) || limit is < 1 or > 500) return Fail("--limit must be from 1 to 500."); if (!int.TryParse(offsetText, out var offset) || offset < 0) return Fail("--offset must be zero or greater.");
        string? filterColumn = null; string? filterValue = null;
        if (filterText is not null) { var split = filterText.IndexOf('='); if (split <= 0) return Fail("--filter must use column=value."); filterColumn = filterText[..split]; filterValue = filterText[(split + 1)..]; }
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}");
        var page = await new SqlWorkspaceService().ReadPageAsync(profile, table, offset, limit, search, filterColumn, filterValue, sort, descending, cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(page, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else { Console.WriteLine(string.Join('\t', page.Columns.Select(column => column.Name))); foreach (var row in page.Rows) Console.WriteLine(string.Join('\t', page.Columns.Select(column => row.Values.TryGetValue(column.Name, out var value) ? SqlCell(value) : string.Empty))); }
        Console.Error.WriteLine($"Returned {page.Rows.Count:N0} of {page.TotalRows:N0} matching {page.Table} row(s) at offset {page.Offset:N0}; {page.Columns.Count:N0} complete column(s)."); return 0;
    }
    if (operation.Equals("table-admin", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db table-admin requires a table name."); var options = args[6..]; var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown table-admin option: {unknown[0]}");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}"); var administration = new SqlAdministrationService(); var ddl = await administration.ShowCreateTableAsync(profile, table, cancellationToken); var indexes = await administration.ReadIndexesAsync(profile, table, cancellationToken);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Database = profile.Database, Table = table.Name, Columns = table.Columns, Indexes = indexes, CreateTable = ddl }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else { Console.WriteLine($"DATABASE\t{profile.Database}\nTABLE\t{table.Name}\nCOLUMNS\t{table.Columns.Count}\nINDEXES\t{indexes.Count}"); foreach (var index in indexes) Console.WriteLine($"INDEX\t{index.Display}"); Console.WriteLine($"DDL\n{ddl}"); } return 0;
    }
    if (operation.Equals("objects", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[5..]; var typeText = Option(options, "--type="); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--type=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown objects option: {unknown[0]}");
        SqlDatabaseObjectType? type = typeText is null ? null : ParseDatabaseObjectType(typeText); var objects = await new SqlDatabaseObjectService().ListAsync(profile, cancellationToken); if (type is not null) objects = objects.Where(item => item.Type == type).ToArray();
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(objects, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var item in objects) Console.WriteLine($"{item.Type}\t{item.Name}\t{item.Definer}\t{item.State ?? "-"}\t{item.Details}"); Console.Error.WriteLine($"{objects.Count:N0} visible view/routine/trigger/event object(s) in {profile.Database}."); return 0;
    }
    if (operation.Equals("object-show", StringComparison.OrdinalIgnoreCase) || operation.Equals("object-drop", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail($"db {operation} requires <view|trigger|procedure|function|event> <name>.");
        var options = args[7..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown {operation} option: {unknown[0]}");
        if (operation.Equals("object-show", StringComparison.OrdinalIgnoreCase) && apply) return Fail("object-show is read-only and does not accept --apply.");
        var type = ParseDatabaseObjectType(args[5]); var service = new SqlDatabaseObjectService(); var objects = await service.ListAsync(profile, cancellationToken); var item = objects.FirstOrDefault(candidate => candidate.Type == type && candidate.Name.Equals(args[6], StringComparison.OrdinalIgnoreCase)); if (item is null) return Fail($"{type} not found: {args[6]}");
        if (operation.Equals("object-show", StringComparison.OrdinalIgnoreCase)) { var definition = await service.ShowCreateAsync(profile, item, cancellationToken); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(definition, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else Console.WriteLine(definition.CreateSql); return 0; }
        var sql = SqlDatabaseObjectService.BuildDropSql(item); Console.WriteLine(sql); if (!apply) { Console.Error.WriteLine("Dry-run DROP plan only. Re-run with --apply after exporting or reviewing the exact definition."); return 0; } await service.DropAsync(profile, item, cancellationToken); Console.Error.WriteLine($"Dropped exact {item.Type} {profile.Database}.{item.Name}."); return 0;
    }
    if (operation.Equals("object-export", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db object-export requires an output .sql path."); var options = args[6..]; var overwrite = options.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown object-export option: {unknown[0]}");
        var result = await new SqlDatabaseObjectService().ExportAsync(profile, args[5], overwrite, cancellationToken); Console.WriteLine(result.Path); Console.Error.WriteLine($"Atomically exported {result.Objects:N0} exact database-object definition(s), {result.Bytes:N0} bytes."); return 0;
    }
    if (operation.Equals("view-set", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db view-set requires <view-name> <select.sql>."); var options = args[7..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown view-set option: {unknown[0]}"); if (!File.Exists(args[6])) return Fail($"SELECT file not found: {args[6]}");
        var select = await File.ReadAllTextAsync(args[6], cancellationToken); var sql = SqlDatabaseObjectService.BuildCreateOrReplaceViewSql(profile.Database, args[5], select); Console.WriteLine(sql); if (!apply) { Console.Error.WriteLine("Dry-run CREATE OR REPLACE VIEW plan only. Re-run with --apply after reviewing the exact SELECT."); return 0; } await new SqlDatabaseObjectService().CreateOrReplaceViewAsync(profile, args[5], select, cancellationToken); Console.Error.WriteLine($"Created or replaced view {profile.Database}.{args[5]}."); return 0;
    }
    if (operation.Equals("event-state", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db event-state requires <event-name> <enable|disable>."); var options = args[7..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown event-state option: {unknown[0]}"); var enabled = args[6].Equals("enable", StringComparison.OrdinalIgnoreCase); if (!enabled && !args[6].Equals("disable", StringComparison.OrdinalIgnoreCase)) return Fail("Event state must be enable or disable.");
        var service = new SqlDatabaseObjectService(); var item = (await service.ListAsync(profile, cancellationToken)).FirstOrDefault(candidate => candidate.Type == SqlDatabaseObjectType.Event && candidate.Name.Equals(args[5], StringComparison.OrdinalIgnoreCase)); if (item is null) return Fail($"Event not found: {args[5]}"); var sql = SqlDatabaseObjectService.BuildEventStateSql(item, enabled); Console.WriteLine(sql); if (!apply) { Console.Error.WriteLine("Dry-run ALTER EVENT plan only. Re-run with --apply after review."); return 0; } await service.SetEventEnabledAsync(profile, item, enabled, cancellationToken); Console.Error.WriteLine($"Event {profile.Database}.{item.Name} is now {(enabled ? "enabled" : "disabled")}."); return 0;
    }
    if (operation.Equals("process-list", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[5..]; var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown process-list option: {unknown[0]}");
        var processes = await new SqlAdministrationService().ReadProcessesAsync(profile, cancellationToken); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(processes, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var process in processes) Console.WriteLine($"{process.Id}\t{process.User}\t{process.Host}\t{process.Database}\t{process.Command}\t{process.Seconds}\t{process.State}\t{process.Statement}"); Console.Error.WriteLine($"{processes.Count:N0} visible process(es)."); return 0;
    }
    if (operation.Equals("user-list", StringComparison.OrdinalIgnoreCase))
    {
        var options = args[5..]; var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown user-list option: {unknown[0]}");
        var users = await new SqlAdministrationService().ReadUsersAsync(profile, cancellationToken); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(users, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var account in users) Console.WriteLine(account.Display); Console.Error.WriteLine($"{users.Count:N0} visible database account(s); password hashes were not queried."); return 0;
    }
    if (operation.Equals("account", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 8) return Fail("db account requires <grants|create|password|lock|unlock|grant|revoke|drop> <account-user> <account-host>.");
        var action = args[5].ToLowerInvariant(); var accountUser = args[6]; var accountHost = args[7]; var privilegeAction = action is "grant" or "revoke"; if (privilegeAction && args.Length < 9) return Fail($"db account {action} requires a comma-separated privilege list.");
        var optionStart = privilegeAction ? 9 : 8; var options = args[optionStart..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var locked = options.Any(option => option.Equals("--locked", StringComparison.OrdinalIgnoreCase)); var global = options.Any(option => option.Equals("--global", StringComparison.OrdinalIgnoreCase)); var grantOption = options.Any(option => option.Equals("--grant-option", StringComparison.OrdinalIgnoreCase)); var table = Option(options, "--table="); var newPasswordEnvironment = Option(options, "--new-password-env=") ?? "WOW_CRUCIBLE_NEW_DB_PASSWORD";
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--new-password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--table=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--locked", StringComparison.OrdinalIgnoreCase) && !option.Equals("--global", StringComparison.OrdinalIgnoreCase) && !option.Equals("--grant-option", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown account option: {unknown[0]}"); if (string.IsNullOrWhiteSpace(newPasswordEnvironment)) return Fail("--new-password-env requires a non-empty environment-variable name.");
        if (action is not ("grants" or "create" or "password" or "lock" or "unlock" or "grant" or "revoke" or "drop")) return Fail($"Unknown account action: {action}");
        if (action != "create" && locked) return Fail("--locked applies only to account creation."); if (!privilegeAction && (global || grantOption || table is not null)) return Fail("--global, --table, and --grant-option apply only to grant/revoke."); if (action == "revoke" && grantOption) return Fail("--grant-option applies only to grant.");
        var administration = new SqlAdministrationService();
        if (action == "grants")
        {
            if (apply) return Fail("account grants is read-only and does not accept --apply."); var grants = await administration.ReadGrantsAsync(profile, accountUser, accountHost, cancellationToken);
            if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(grants, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var grant in grants) Console.WriteLine(grant); Console.Error.WriteLine($"{grants.Count:N0} grant statement(s) visible for '{accountUser}'@'{accountHost}'."); return 0;
        }
        IReadOnlyList<SqlPrivilegeInfo> supportedPrivileges = []; IReadOnlyList<string> privileges = [];
        if (privilegeAction) { supportedPrivileges = await administration.ReadPrivilegesAsync(profile, cancellationToken); privileges = args[8].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
        var sql = action switch
        {
            "create" => SqlAdministrationService.BuildCreateUserSql(accountUser, accountHost, locked),
            "password" => SqlAdministrationService.BuildChangePasswordSql(accountUser, accountHost),
            "lock" => SqlAdministrationService.BuildAccountLockSql(accountUser, accountHost, true),
            "unlock" => SqlAdministrationService.BuildAccountLockSql(accountUser, accountHost, false),
            "drop" => SqlAdministrationService.BuildDropUserSql(accountUser, accountHost),
            "grant" => SqlAdministrationService.BuildGrantSql(accountUser, accountHost, profile.Database, table, global, privileges, supportedPrivileges, grantOption),
            "revoke" => SqlAdministrationService.BuildRevokeSql(accountUser, accountHost, profile.Database, table, global, privileges, supportedPrivileges),
            _ => throw new UnreachableException()
        };
        Console.WriteLine(SqlAdministrationService.RedactPasswordSql(sql)); if (!apply) { Console.Error.WriteLine("Dry-run account plan only. Re-run with --apply after reviewing the exact account, host, scope, and privileges."); return 0; }
        if (action is "create" or "password")
        {
            var newPassword = Environment.GetEnvironmentVariable(newPasswordEnvironment); if (string.IsNullOrEmpty(newPassword)) return Fail($"Set {newPasswordEnvironment} for --apply. New passwords are never accepted as command arguments or printed.");
            if (action == "create") await administration.CreateUserAsync(profile, accountUser, accountHost, newPassword, locked, cancellationToken); else await administration.ChangePasswordAsync(profile, accountUser, accountHost, newPassword, cancellationToken);
        }
        else if (action == "lock") await administration.SetAccountLockAsync(profile, accountUser, accountHost, true, cancellationToken);
        else if (action == "unlock") await administration.SetAccountLockAsync(profile, accountUser, accountHost, false, cancellationToken);
        else if (action == "drop") await administration.DropUserAsync(profile, accountUser, accountHost, cancellationToken);
        else if (action == "grant") await administration.GrantAsync(profile, accountUser, accountHost, profile.Database, table, global, privileges, supportedPrivileges, grantOption, cancellationToken);
        else await administration.RevokeAsync(profile, accountUser, accountHost, profile.Database, table, global, privileges, supportedPrivileges, cancellationToken);
        Console.Error.WriteLine($"Applied account {action} for '{accountUser}'@'{accountHost}'. Verify the result with db account ... grants."); return 0;
    }
    if (operation.Equals("join", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db join requires a recognized relationship name. Use db inspect to review relationships."); var options = args[6..]; var joinType = Option(options, "--type=") ?? "LEFT"; var limitText = Option(options, "--limit=") ?? "200"; var run = options.Any(option => option.Equals("--run", StringComparison.OrdinalIgnoreCase)); var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--type=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--run", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown join option: {unknown[0]}"); if (!int.TryParse(limitText, out var joinLimit)) return Fail("--limit must be numeric.");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var relation = capabilities.Relationships.FirstOrDefault(candidate => candidate.Name.Equals(args[5], StringComparison.OrdinalIgnoreCase)); if (relation is null) return Fail($"Relationship not found: {args[5]}"); var source = capabilities.FindTable(relation.FromTable) ?? throw new InvalidOperationException("Relationship source table is unavailable."); var target = capabilities.FindTable(relation.ToTable) ?? throw new InvalidOperationException("Relationship target table is unavailable."); var sql = SqlAdministrationService.BuildJoinSql(relation, source, target, joinType, joinLimit); if (!run) { Console.WriteLine(sql); Console.Error.WriteLine("Dry-run join SQL only. Re-run with --run to execute the read-only SELECT."); return 0; }
        var result = await new SqlWorkspaceService().QueryAsync(profile, sql, 2000, cancellationToken); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else { Console.WriteLine(string.Join('\t', result.Columns)); foreach (var row in result.Rows) Console.WriteLine(string.Join('\t', row.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)))); } Console.Error.WriteLine($"Join returned {result.Rows.Count:N0} row(s) in {result.Duration.TotalMilliseconds:N0} ms."); return 0;
    }
    if (operation.Equals("index", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 8) return Fail("db index requires <table> <create|drop> <index-name>; create also requires a comma-separated column list."); var tableName = args[5]; var action = args[6]; var indexName = args[7]; var create = action.Equals("create", StringComparison.OrdinalIgnoreCase); var drop = action.Equals("drop", StringComparison.OrdinalIgnoreCase); if (!create && !drop) return Fail("Index action must be create or drop."); if (create && args.Length < 9) return Fail("Index create requires a comma-separated column list.");
        var optionStart = create ? 9 : 8; var options = args[optionStart..]; var apply = options.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var unique = options.Any(option => option.Equals("--unique", StringComparison.OrdinalIgnoreCase)); var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--unique", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown index option: {unknown[0]}"); if (drop && unique) return Fail("--unique applies only to index create.");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(tableName); if (table is null) return Fail($"Table not found: {tableName}"); var columns = create ? args[8].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : []; var sql = create ? SqlAdministrationService.BuildCreateIndexSql(table, indexName, columns, unique) : SqlAdministrationService.BuildDropIndexSql(table, indexName); Console.WriteLine(sql); if (!apply) { Console.Error.WriteLine("Dry-run DDL only. Re-run with --apply after review; MySQL may implicitly commit schema changes."); return 0; }
        var administration = new SqlAdministrationService(); if (create) await administration.CreateIndexAsync(profile, table, indexName, columns, unique, cancellationToken); else await administration.DropIndexAsync(profile, table, indexName, cancellationToken); Console.Error.WriteLine($"Applied index {action} on {profile.Database}.{table.Name}."); return 0;
    }
    if (operation.Equals("dependency-snapshot", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 8 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db dependency-snapshot requires a table name, output JSON path, and one --key=column=value option per primary-key column.");
        var snapshotOptions = args[7..]; var keyOptions = snapshotOptions.Where(option => option.StartsWith("--key=", StringComparison.OrdinalIgnoreCase)).Select(option => option[6..]).ToArray();
        var limitText = Option(snapshotOptions, "--limit=") ?? "200"; var overwrite = snapshotOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = snapshotOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--key=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown dependency-snapshot option: {unknown[0]}");
        if (!int.TryParse(limitText, out var dependencyLimit) || dependencyLimit is < 1 or > 500) return Fail("--limit must be from 1 to 500 rows per edge.");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}");
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray(); if (primary.Length == 0) return Fail($"{table.Name} has no primary key and cannot be snapshotted safely.");
        var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in keyOptions) { var split = option.IndexOf('='); if (split <= 0) return Fail($"Invalid --key value: {option}. Use --key=column=value."); key[option[..split]] = option[(split + 1)..]; }
        if (key.Count != primary.Length || primary.Any(column => !key.ContainsKey(column)) || key.Keys.Any(column => !primary.Contains(column, StringComparer.OrdinalIgnoreCase))) return Fail($"Supply the complete primary key exactly once: {string.Join(", ", primary.Select(column => $"--key={column}=VALUE"))}");
        var service = new SqlWorkspaceService(); var row = await service.ReadRowAsync(profile, table, key, cancellationToken); if (row is null) return Fail($"No exact {table.Name} row matches the supplied primary key.");
        var snapshot = await service.CaptureDependencySnapshotAsync(profile, capabilities, table.Name, row, dependencyLimit, cancellationToken); var output = Path.GetFullPath(args[6]); if (File.Exists(output) && !overwrite) return Fail($"Output already exists: {output}. Use --overwrite after reviewing it.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, cancellationToken);
        Console.Error.WriteLine($"Captured {table.Name} {row.Display}: {snapshot.Edges.Count:N0} edge(s), {snapshot.Edges.Sum(edge => edge.Rows.Count):N0} related row(s), {snapshot.Edges.Count(edge => edge.Truncated):N0} truncated edge(s), {snapshot.Edges.Count(edge => edge.TotalRows < 0):N0} file-DBC edge(s) with empty SQL mirrors. Snapshot: {output}"); return 0;
    }
    if (operation.Equals("export", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db export requires a table name and output path.");
        var exportOptions = args[7..]; var formatText = Option(exportOptions, "--format=") ?? "csv"; var overwrite = exportOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = exportOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown export option: {unknown[0]}");
        var format = formatText.ToLowerInvariant() switch { "csv" => SqlExportFormat.Csv, "jsonl" or "json-lines" => SqlExportFormat.JsonLines, _ => throw new ArgumentException($"Unknown export format: {formatText}") };
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}");
        var progress = await new SqlTransferService().ExportTableAsync(profile, table, args[6], format, overwrite, cancellationToken); Console.Error.WriteLine($"Exported {progress.Rows:N0} row(s) from {progress.Table} to {progress.Path} ({progress.Format})."); return 0;
    }
    if (operation.Equals("import", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db import requires a table name and CSV path.");
        var importOptions = args[7..]; var apply = importOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var unknown = importOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown import option: {unknown[0]}");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var table = capabilities.FindTable(args[5]); if (table is null) return Fail($"Table not found: {args[5]}");
        var transfer = new SqlTransferService(); var plan = transfer.AnalyzeCsv(args[6], table); Console.WriteLine($"Table\t{plan.Table}\nRows\t{plan.Rows}\nColumns\t{string.Join(',', plan.Columns)}\nCanApply\t{plan.CanApply}"); foreach (var finding in plan.Findings) Console.Error.WriteLine($"BLOCKED\t{finding}");
        if (!plan.CanApply) return 3; if (!apply) { Console.Error.WriteLine("Dry-run only. Re-run with --apply to insert all rows in one transaction; existing keys are never replaced."); return 0; }
        var inserted = await transfer.ImportCsvAsync(profile, table, plan.Path, cancellationToken); Console.Error.WriteLine($"Inserted {inserted:N0} row(s) transactionally into {table.Name}."); return 0;
    }
    if (operation.Equals("query", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || args[5].StartsWith("--", StringComparison.Ordinal)) return Fail("db query requires a UTF-8 .sql file after the database name.");
        var queryOptions = args[6..]; var write = queryOptions.Any(option => option.Equals("--write", StringComparison.OrdinalIgnoreCase)); var batch = queryOptions.Any(option => option.Equals("--batch", StringComparison.OrdinalIgnoreCase)); var batchFormat = Option(queryOptions, "--batch-format=") ?? "text"; var queryOutput = Option(queryOptions, "--output="); var queryFormat = Option(queryOptions, "--format="); var queryOverwrite = queryOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = queryOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--batch-format=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--write", StringComparison.OrdinalIgnoreCase) && !option.Equals("--batch", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown query option: {unknown[0]}");
        if (queryOutput is null && (queryFormat is not null || queryOverwrite)) return Fail("--format and --overwrite require --output for a read-only query result.");
        var sql = await File.ReadAllTextAsync(args[5], cancellationToken);
        if (write)
        {
            if (batch) return Fail("--write and --batch are mutually exclusive. Review mutations through the single confirmed write path.");
            if (queryOutput is not null || queryFormat is not null || queryOverwrite) return Fail("Query-result output options apply only to read-only queries, not --write.");
            var result = await new SqlWorkspaceService().ExecuteAsync(profile, sql, cancellationToken); Console.WriteLine($"AffectedRows\t{result.AffectedRows}\nDurationMs\t{result.Duration.TotalMilliseconds:0}"); return 0;
        }
        if (batch)
        {
            if (queryOutput is not null || queryFormat is not null || queryOverwrite) return Fail("Batch results may have different shapes. Export a selected result in desktop SQL Studio, or omit --batch for single-result --output.");
            var result = await new SqlWorkspaceService().QueryBatchAsync(profile, sql, 10000, cancellationToken);
            if (batchFormat.Equals("json", StringComparison.OrdinalIgnoreCase)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            else if (batchFormat.Equals("text", StringComparison.OrdinalIgnoreCase))
                foreach (var set in result.Results) { Console.WriteLine($"RESULT\t{set.Index}\t{set.Result.Rows.Count}\t{set.Result.Columns.Count}\t{(set.Truncated ? "TRUNCATED" : "COMPLETE")}"); Console.WriteLine(string.Join('\t', set.Result.Columns)); foreach (var row in set.Result.Rows) Console.WriteLine(string.Join('\t', row.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)))); }
            else return Fail("--batch-format must be text or json.");
            Console.Error.WriteLine($"Returned {result.TotalRows:N0} row(s) across {result.Results.Count:N0} independently validated read result(s) in {result.Duration.TotalMilliseconds:N0} ms."); return 0;
        }
        var query = await new SqlWorkspaceService().QueryAsync(profile, sql, 10000, cancellationToken); Console.WriteLine(string.Join('\t', query.Columns)); foreach (var row in query.Rows) Console.WriteLine(string.Join('\t', row.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))));
        if (queryOutput is not null)
        {
            var format = (queryFormat ?? Path.GetExtension(queryOutput).TrimStart('.')).ToLowerInvariant() switch { "csv" => SqlExportFormat.Csv, "jsonl" or "ndjson" => SqlExportFormat.JsonLines, var value => throw new ArgumentException($"Unsupported query-result format '{value}'. Use csv or jsonl.") };
            var exported = await new SqlTransferService().ExportQueryResultAsync(query, queryOutput, format, queryOverwrite, cancellationToken); Console.Error.WriteLine($"Exported structured query result: {exported.Rows:N0} row(s) × {exported.Columns:N0} column(s) → {exported.Path}");
        }
        Console.Error.WriteLine($"Returned {query.Rows.Count:N0} row(s) in {query.Duration.TotalMilliseconds:N0} ms. Use --write only for an intentionally reviewed non-query statement."); return 0;
    }
    if (operation.Equals("content-plan", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 7 || args[5].StartsWith("--", StringComparison.Ordinal) || args[6].StartsWith("--", StringComparison.Ordinal)) return Fail("db content-plan requires a supported authoring domain and UTF-8 draft JSON path.");
        var planOptions = args[7..]; var apply = planOptions.Any(option => option.Equals("--apply", StringComparison.OrdinalIgnoreCase)); var update = planOptions.Any(option => option.Equals("--update", StringComparison.OrdinalIgnoreCase)); var output = Option(planOptions, "--output="); var overwrite = planOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = planOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--update", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown content-plan option: {unknown[0]}"); if (update && !apply) return Fail("--update changes live data and therefore also requires --apply.");
        var json = await File.ReadAllTextAsync(args[6], cancellationToken); var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        WorldContentWritePlan plan = args[5].ToLowerInvariant() switch
        {
            "creature" => CreatureTemplateAdapter.CreatePlan(System.Text.Json.JsonSerializer.Deserialize<CreatureTemplateDraft>(json, jsonOptions) ?? throw new InvalidDataException("Creature draft JSON decoded to null."), capabilities),
            "gameobject" or "go" => GameObjectTemplateAdapter.CreatePlan(System.Text.Json.JsonSerializer.Deserialize<GameObjectTemplateDraft>(json, jsonOptions) ?? throw new InvalidDataException("Gameobject draft JSON decoded to null."), capabilities),
            "quest" => CreateQuestPlan(json, jsonOptions, capabilities),
            _ => CreateBehaviorPlan(args[5], json, jsonOptions, capabilities)
        };
        var sql = plan.PreviewSql() + Environment.NewLine; Console.Write(sql); foreach (var omitted in plan.OmittedFields) Console.Error.WriteLine($"OMITTED\t{omitted}");
        if (output is not null) { var fullPath = Path.GetFullPath(output); if (File.Exists(fullPath) && !overwrite) return Fail($"Output already exists: {fullPath}. Use --overwrite after reviewing it."); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!); await File.WriteAllTextAsync(fullPath, sql, cancellationToken); Console.Error.WriteLine($"SQL plan: {fullPath}"); }
        if (!apply) { Console.Error.WriteLine($"Dry-run only: {plan.Rows.Count:N0} row(s). Re-run with --apply to insert, or --apply --update to update the primary row and insert only new children."); return 0; }
        var content = new WorldContentTemplateService(); if (update) await content.UpdateFirstAndInsertChildrenAsync(profile, plan, cancellationToken); else await content.InsertAsync(profile, plan, cancellationToken); Console.Error.WriteLine($"Committed {plan.Domain}: primary {(update ? "updated" : "inserted")}, {plan.Rows.Count - 1:N0} child row(s) inserted transactionally."); return 0;
    }
    if (operation.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
    {
        var includes = rawOptions.Where(option => option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray();
        var excludes = rawOptions.Where(option => option.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase)).Select(option => option[10..]).ToArray();
        var includeSensitive = rawOptions.Any(option => option.Equals("--include-sensitive", StringComparison.OrdinalIgnoreCase));
        var overwrite = rawOptions.Any(option => option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var unknown = rawOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) &&
            !option.StartsWith("--include=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase) &&
            !option.Equals("--include-sensitive", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown snapshot option: {unknown[0]}");
        var progress = new Progress<LegacyDatabaseSnapshotProgress>(value =>
            Console.Error.WriteLine(value.Table is null ? $"{value.Stage}" : $"{value.Stage}\t{value.CompletedTables:N0}/{value.TotalTables:N0}\t{value.Table}\t{value.Rows:N0} rows"));
        var result = await new LegacyDatabaseSnapshotService().CaptureAsync(profile, args[5], new(includes, excludes, includeSensitive, overwrite), progress, cancellationToken);
        Console.Error.WriteLine($"Read-only legacy world snapshot complete: {result.Manifest.Tables.Count:N0} table(s), {result.Manifest.TotalRows:N0} row(s), {result.ArtifactBytes / (1024d * 1024):0.##} MiB.\nArtifact: {result.Path}\nSchema: {result.Manifest.SchemaSha256}\nContent: {result.Manifest.ContentSha256}\nConsistent snapshot: {result.Manifest.ConsistentSnapshotStarted}; database-enforced read-only: {result.Manifest.ReadOnlyTransactionEnforced}.\nExcluded by safety/filters: {result.Manifest.Policy.ExcludedTables.Count:N0} table(s).");
        return 0;
    }
    if (operation.Equals("inspect", StringComparison.OrdinalIgnoreCase))
    {
        var unknown = rawOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown database option: {unknown[0]}");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile);
        Console.WriteLine($"Server\t{capabilities.ServerVersion}"); Console.WriteLine($"Database\t{capabilities.Database}");
        foreach (var table in capabilities.Tables.Values.OrderBy(table => table.Name)) Console.WriteLine($"TABLE\t{table.Name}\t{table.Columns.Count} columns");
        foreach (var relation in capabilities.Relationships.OrderBy(value => value.FromTable).ThenBy(value => value.Name)) Console.WriteLine($"RELATION\t{relation.Name}\t{(relation.Declared ? "declared" : "inferred")}\t{relation.FromTable}.{relation.FromColumn}\t{relation.ToTable}.{relation.ToColumn}\t{relation.Description}");
        return capabilities.Tables.Count > 0 ? 0 : 1;
    }
    if (operation.Equals("item-audit", StringComparison.OrdinalIgnoreCase))
    {
        var output = Option(rawOptions, "--output="); var dbc = Option(rawOptions, "--dbc="); var coreSource = Option(rawOptions, "--core-source="); var json = rawOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var requestedIds = new List<uint>();
        foreach (var option in rawOptions.Where(option => option.StartsWith("--id=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("--ids=", StringComparison.OrdinalIgnoreCase)))
        {
            var value = option[(option.IndexOf('=') + 1)..];
            IReadOnlyList<uint> parsed;
            if (option.StartsWith("--ids=", StringComparison.OrdinalIgnoreCase) && value.Contains(','))
            {
                var commaSeparated = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var values = commaSeparated.Select(piece => ItemIdQueryParser.TryParseSingle(piece, out var entry) ? entry : 0).ToArray();
                if (values.Length == 0 || values.Any(entry => entry == 0)) return Fail($"Invalid positive item ID list: {value}");
                parsed = values;
            }
            else parsed = ItemIdQueryParser.Parse(value);
            if (parsed.Count == 0) return Fail($"Invalid positive item ID list: {value}");
            foreach (var entry in parsed) if (!requestedIds.Contains(entry)) requestedIds.Add(entry);
        }
        var unknown = rawOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--core-source=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--id=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ids=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-audit option: {unknown[0]}");
        var audit = await new ItemCatalogService().AuditAsync(profile, dbc, coreSource);
        var byId = audit.AllItems.ToDictionary(item => item.Entry);
        var selected = requestedIds.Count == 0 ? audit.NoKnownAcquisitionPath : requestedIds.Where(byId.ContainsKey).Select(entry => byId[entry]).ToArray();
        var missingRequested = requestedIds.Where(entry => !byId.ContainsKey(entry)).ToArray();
        var auditJsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        if (json && (output is null || requestedIds.Count > 0))
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { audit.Database, audit.AuditedUtc, audit.TotalItems, audit.ObtainableItems, NoKnownAcquisitionPath = audit.NoKnownAcquisitionPath.Count, audit.CheckedSources, audit.MissingSources, RequestedIds = requestedIds, MissingRequestedIds = missingRequested, Items = selected }, auditJsonOptions));
        else if (output is null || requestedIds.Count > 0)
            foreach (var item in selected) Console.WriteLine($"{item.Entry}\t{(item.HasKnownAcquisitionPath ? "KNOWN" : "NO_PATH")}\t{item.Quality}\t{item.ItemLevel}\t{item.ItemSetId}\t{item.ReviewGroup}\t{item.Name}\t{string.Join(" | ", item.HasKnownAcquisitionPath ? item.AcquisitionSources : item.NoPathReview)}");
        if (output is not null) WriteTextAtomic(output, System.Text.Json.JsonSerializer.Serialize(audit, auditJsonOptions) + Environment.NewLine);
        Console.Error.WriteLine($"Item acquisition audit: {audit.NoKnownAcquisitionPath.Count:N0} of {audit.TotalItems:N0} item(s) have no known path across {audit.CheckedSources.Count:N0} available source table(s).{(requestedIds.Count == 0 ? string.Empty : $" Exact selection: {selected.Count:N0} found, {missingRequested.Length:N0} missing.")} Missing source families: {string.Join(", ", audit.MissingSources)}{(output is null ? string.Empty : $". Report: {Path.GetFullPath(output)}")}");
        foreach (var missing in missingRequested) Console.Error.WriteLine($"MISSING_ITEM\t{missing}");
        return missingRequested.Length == 0 ? 0 : 3;
    }
    if (operation.Equals("item-inspect", StringComparison.OrdinalIgnoreCase) && args.Length >= 6 && uint.TryParse(args[5], out var inspectedEntry))
    {
        var inspectOptions = args[6..]; var dbc = Option(inspectOptions, "--dbc="); var coreSource = Option(inspectOptions, "--core-source="); var unknown = inspectOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--core-source=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-inspect option: {unknown[0]}");
        var inspection = await new ItemCatalogService().InspectAsync(profile, inspectedEntry, dbc, coreSource);
        if (inspection.Item is null) return Fail($"Item {inspectedEntry} does not exist in item_template.");
        Console.WriteLine($"ITEM\t{inspection.Item.Entry}\t{inspection.Item.Name}");
        Console.WriteLine($"CLASSIFICATION\t{(inspection.HasKnownAcquisitionPath ? "KNOWN ACQUISITION PATH" : "NO KNOWN ACQUISITION PATH")}");
        Console.WriteLine($"REVIEW_GROUP\t{inspection.Item.ReviewGroup}");
        foreach (var evidence in inspection.AcceptedEvidence) Console.WriteLine($"ACCEPTED\t{evidence}");
        foreach (var evidence in inspection.RejectedEvidence) Console.WriteLine($"REJECTED\t{evidence}");
        Console.WriteLine($"COVERAGE\t{inspection.CheckedSources.Count} checked\t{inspection.MissingSources.Count} missing");
        foreach (var missing in inspection.MissingSources) Console.WriteLine($"MISSING\t{missing}");
        return 0;
    }
    if (operation.Equals("spell-inspect", StringComparison.OrdinalIgnoreCase) && args.Length >= 6 && uint.TryParse(args[5], out var inspectedSpell) && inspectedSpell > 0)
    {
        var inspectOptions = args[6..]; var dbcOption = Option(inspectOptions, "--dbc="); var json = inspectOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = inspectOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown spell-inspect option: {unknown[0]}");
        WdbcFile? spellDbc = null; IReadOnlyList<DbcColumn>? spellColumns = null;
        if (!string.IsNullOrWhiteSpace(dbcOption))
        {
            var dbcPath = Directory.Exists(dbcOption) ? Path.Combine(dbcOption, "Spell.dbc") : dbcOption;
            if (!File.Exists(dbcPath)) return Fail($"Spell.dbc was not found: {Path.GetFullPath(dbcPath)}");
            spellDbc = WdbcFile.Load(dbcPath); var resolution = DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns("Spell", spellDbc.FieldCount);
            if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch) return Fail($"{dbcPath} has {spellDbc.FieldCount} fields; the WotLK build-12340 Spell.dbc layout requires 234.");
            spellColumns = resolution.Columns;
        }
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var audit = await new SpellSqlAuditService().AuditAsync(profile, capabilities, inspectedSpell, spellDbc, spellColumns, cancellationToken: cancellationToken);
        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(audit, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        Console.WriteLine($"SPELL\t{audit.SpellId}");
        Console.WriteLine($"EFFECTIVE\t{audit.EffectiveSource}");
        Console.WriteLine($"DBC_RECORD\t{(audit.DbcRecordFound ? "present" : spellDbc is null ? "not supplied" : "missing")}");
        Console.WriteLine($"SPELL_DBC_OVERRIDE\t{(audit.HasFullOverride ? $"present · {audit.OverrideDifferences.Count} effective difference(s)" : "absent")}");
        if (!string.IsNullOrWhiteSpace(audit.OverrideComparisonWarning)) Console.WriteLine($"WARNING\t{audit.OverrideComparisonWarning}");
        foreach (var difference in audit.OverrideDifferences)
            Console.WriteLine($"DIFF\t{difference.FieldIndex}\t{difference.DbcField}\t{difference.SqlColumn}\tDBC={Convert.ToString(difference.DbcValue, System.Globalization.CultureInfo.InvariantCulture)}\tSQL={Convert.ToString(difference.SqlValue, System.Globalization.CultureInfo.InvariantCulture)}");
        foreach (var row in audit.RelatedRows)
            Console.WriteLine($"RELATED\t{row.Table}\t{row.Relationship}\t{string.Join(',', row.MatchedColumns)}\t{row.Display}");
        Console.WriteLine($"COVERAGE\t{audit.CheckedTables.Count} checked\t{audit.MissingTables.Count} unavailable\t{audit.RelatedRows.Count} related row(s)");
        foreach (var missing in audit.MissingTables) Console.WriteLine($"MISSING\t{missing}");
        return 0;
    }
    if (operation.Equals("reference-search", StringComparison.OrdinalIgnoreCase) && args.Length >= 7 && Enum.TryParse<ReferenceDomain>(args[5], true, out var referenceDomain))
    {
        if (referenceDomain is not (ReferenceDomain.Spell or ReferenceDomain.Item or ReferenceDomain.Creature or ReferenceDomain.Quest or ReferenceDomain.GameObject))
            return Fail("CLI reference-search domains are spell, item, creature, quest, and gameobject. DBC-only lookup domains are available through the guided desktop picker.");
        var searchOptions = args[7..]; var dbcOption = Option(searchOptions, "--dbc="); var json = searchOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var limitText = Option(searchOptions, "--limit=") ?? "250";
        var unknown = searchOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--dbc=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown reference-search option: {unknown[0]}");
        if (!int.TryParse(limitText, out var referenceLimit) || referenceLimit is < 1 or > 1000) return Fail("--limit must be from 1 to 1000.");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var pages = new List<ReferenceLookupPage>();
        pages.Add(await new ReferenceLookupService().SearchSqlAsync(profile, capabilities, referenceDomain, args[6], referenceLimit, cancellationToken));
        if (referenceDomain == ReferenceDomain.Spell && !string.IsNullOrWhiteSpace(dbcOption))
        {
            var dbcPath = Directory.Exists(dbcOption) ? Path.Combine(dbcOption, "Spell.dbc") : dbcOption;
            if (!File.Exists(dbcPath)) return Fail($"Spell.dbc was not found: {Path.GetFullPath(dbcPath)}");
            var dbc = WdbcFile.Load(dbcPath); var resolution = DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns("Spell", dbc.FieldCount);
            if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch) return Fail($"{dbcPath} is not the 234-field WotLK build-12340 Spell.dbc layout.");
            pages.Add(ReferenceLookupService.SearchDbc(referenceDomain, dbc, resolution.Columns, 0, 136, args[6], referenceLimit, 39, 3));
        }
        var result = ReferenceLookupService.Merge(referenceDomain, args[6], referenceLimit, pages.ToArray());
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            foreach (var entry in result.Entries) Console.WriteLine($"{entry.Id}\t{entry.Name}\t{entry.Source}\t{entry.Details}");
            Console.Error.WriteLine($"Reference search: {result.Entries.Count:N0} {referenceDomain} result(s) from {string.Join(" + ", result.Sources)}.{(result.HasMore ? " Refine the query or raise --limit." : string.Empty)}");
        }
        return 0;
    }
    if (operation.Equals("item-clone", StringComparison.OrdinalIgnoreCase) && args.Length >= 7 && uint.TryParse(args[5], out var sourceEntry) && uint.TryParse(args[6], out var newEntry))
    {
        var cloneOptions = args[7..]; var suffix = Option(cloneOptions, "--suffix=") ?? " Variant"; var setText = Option(cloneOptions, "--itemset=");
        var unknown = cloneOptions.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--suffix=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--itemset=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-clone option: {unknown[0]}");
        uint? itemSet = setText is null ? null : uint.Parse(setText, System.Globalization.CultureInfo.InvariantCulture);
        var result = await new ItemCatalogService().CloneAsync(profile, sourceEntry, newEntry, suffix, itemSet);
        Console.WriteLine($"Source\t{result.SourceEntry}\t{result.SourceName}\nClone\t{result.NewEntry}\t{result.NewName}\nItemSet\t{result.ItemSetId}\nColumns\t{result.CopiedColumns}\nLocaleRows\t{result.CopiedLocaleRows}"); return 0;
    }
    return DatabaseHelp(2);
}

static WorldContentWritePlan CreateQuestPlan(string json, System.Text.Json.JsonSerializerOptions options, DatabaseCapabilities capabilities)
{
    var draft = System.Text.Json.JsonSerializer.Deserialize<QuestPortableDraft>(json, options) ?? throw new InvalidDataException("Quest draft JSON decoded to null."); var table = capabilities.FindTable("quest_template") ?? throw new NotSupportedException("The connected schema has no quest_template table."); var values = draft.Values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase); return QuestTemplateAdapter.CreatePlan(table, values, capabilities, draft.Links);
}

static BehaviorPortableDraft CreateBehaviorDraft(string id)
{
    var domain = BehaviorDomainCatalog.Find(id); var table = BehaviorAuthoringAdapter.PortableTable(domain.TableName); var values = BehaviorAuthoringAdapter.Defaults(table).ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase); return new(domain.Id, values);
}

static WorldContentWritePlan CreateBehaviorPlan(string id, string json, System.Text.Json.JsonSerializerOptions options, DatabaseCapabilities capabilities)
{
    var draft = System.Text.Json.JsonSerializer.Deserialize<BehaviorPortableDraft>(json, options) ?? throw new InvalidDataException("Behavior draft JSON decoded to null."); var requested = BehaviorDomainCatalog.Find(id); var embedded = BehaviorDomainCatalog.Find(draft.Domain); if (!requested.Id.Equals(embedded.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Draft domain '{embedded.Id}' does not match requested domain '{requested.Id}'."); var table = capabilities.FindTable(requested.TableName) ?? throw new NotSupportedException($"The connected schema has no {requested.TableName} table."); var values = draft.Values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase); return BehaviorAuthoringAdapter.CreatePlan(requested, table, values);
}

static int DatabaseHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible db draft-template <domain> <output.json> [--overwrite]\n  wowcrucible db schemas <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db inspect <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db favorites <host> <port> <user> <database> [--search=text] [--verify] [--format=text|json]\n  wowcrucible db rows <host> <port> <user> <database> <table> [--search=text] [--filter=column=value] [--sort=column] [--descending] [--offset=N] [--limit=N] [--format=text|json]\n  wowcrucible db pet-curve <host> <port> <user> <database> <source-creature> <target-creature> [--levels=1-80] [--health=1] [--mana=1] [--armor=1] [--attributes=1] [--damage=1] [--output=curve.sql] [--overwrite] [--format=text|json] [--apply] [--update-existing]\n  wowcrucible db table-admin <host> <port> <user> <database> <table> [--format=text|json]\n  wowcrucible db process-list <host> <port> <user> <database> [--format=text|json]\n  wowcrucible db user-list <host> <port> <user> <database> [--format=text|json]\n  wowcrucible db account <host> <port> <login> <database> grants <account-user> <account-host> [--format=text|json]\n  wowcrucible db account <host> <port> <login> <database> <create|password|lock|unlock|drop> <account-user> <account-host> [--locked] [--apply] [--new-password-env=NAME]\n  wowcrucible db account <host> <port> <login> <database> <grant|revoke> <account-user> <account-host> <privilege[,privilege]> [--global|--table=NAME] [--grant-option] [--apply]\n  wowcrucible db join <host> <port> <user> <database> <relationship-name> [--type=INNER|LEFT|RIGHT] [--limit=N] [--run] [--format=text|json]\n  wowcrucible db index <host> <port> <user> <database> <table> create <name> <column[,column]> [--unique] [--apply]\n  wowcrucible db index <host> <port> <user> <database> <table> drop <name> [--apply]\n  wowcrucible db query <host> <port> <user> <database> <statement.sql> [--output=result.csv|jsonl] [--format=csv|jsonl] [--overwrite] [--write] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db export <host> <port> <user> <database> <table> <output> [--format=csv|jsonl] [--overwrite] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db import <host> <port> <user> <database> <table> <input.csv> [--apply] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db dependency-snapshot <host> <port> <user> <database> <table> <output.json> --key=column=value [--key=column=value]... [--limit=N] [--overwrite]\n  wowcrucible db content-plan <host> <port> <user> <database> <domain> <draft.json> [--output=plan.sql] [--overwrite] [--apply] [--update] [--password-env=NAME] [--ssl=Preferred]\n  wowcrucible db snapshot <host> <port> <user> <database> <output.crucible-db-snapshot> [--password-env=NAME] [--ssl=Preferred] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]\n  wowcrucible db snapshot-inspect <snapshot-file> [--quick]\n  wowcrucible db recovery-audit <legacy-snapshot> <output.crucible-db-audit> [--baseline=stock-snapshot] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]\n  wowcrucible db recovery-inspect <audit-file> [--quick]\n  wowcrucible db item-audit <host> <port> <user> <database> [--id=N]... [--ids=N,N] [--format=text|json] [--password-env=NAME] [--dbc=folder] [--core-source=folder] [--output=report.json]\n  wowcrucible db item-inspect <host> <port> <user> <database> <item-id> [--password-env=NAME] [--dbc=folder] [--core-source=folder]\n  wowcrucible db item-clone <host> <port> <user> <database> <source-id> <new-id> [--suffix=\" Variant\"] [--itemset=ID]\n  wowcrucible db spell-inspect <host> <port> <user> <database> <spell-id> [--password-env=NAME] [--dbc=Spell.dbc|folder] [--format=text|json]\n  wowcrucible db reference-search <host> <port> <user> <database> <spell|item|creature|quest|gameobject> <id-or-name> [--password-env=NAME] [--dbc=Spell.dbc|folder] [--limit=N] [--format=text|json]\n\nDomains: creature, gameobject, quest, gossip-menu, gossip-option, npc-text, trainer, trainer-spell, trainer-creature, legacy-trainer-spell, pet-level-stats, pet-name-part, pet-name-locale, spell-pet-aura, condition, smartai.\n\nDraft templates and content-plan provide portable complete-field authoring automation. content-plan is dry-run by default; --apply inserts, while --apply --update updates exactly one primary row and inserts collision-free children in one transaction. pet-curve is dry-run by default, clones an existing complete level curve while scaling named stat families, preserves custom columns, inserts only missing levels with --apply, and requires the additional --update-existing acknowledgement to replace exact existing target levels. schemas lists every database accessible to the login without changing it. rows is a read-only complete-column table browser with broad search, exact filters, sorting, and bounded paging. table-admin and process-list are read-only. user-list and account grants are permission-aware metadata reads. account mutations and index changes are exact dry-run plans unless --apply is explicit; new account passwords come only from a separate environment variable and are redacted from previews. join is a dry-run SQL preview unless --run is explicit and remains SELECT-only. dependency-snapshot is SELECT-only and captures a complete primary row plus exact recognized incoming/outgoing rows; it is review data, never executable SQL. Snapshot capture is SELECT-only and excludes known auth/character runtime state by default. query reads SQL from a file so statements and secrets do not need to enter shell history; read results can be exported atomically as CSV/JSONL, while --write is explicit and cannot use result-output switches. import is a dry-run unless --apply is present, is INSERT-only, and rolls back the complete CSV on any duplicate/error. export streams the complete table. item-audit can bound output to exact IDs while retaining the complete causal scan; item-inspect explains accepted and rejected SQL/DBC evidence plus conservative exact C++/module grant call sites for one exact item ID; unproven source calls remain review-only; only revision-matched, live-bound, reachable callbacks are admitted. spell-inspect reports whether file Spell.dbc or a full spell_dbc row is server-effective, compares every field AzerothCore consumes, and locates recognized related SQL rows; JSON includes complete row values. reference-search provides the same merged ID/name lookup used by guided editors. recovery-audit is completely offline: with a baseline it records baseline-to-legacy deltas; without one it labels rows unattributed candidates. No recovery audit is executable SQL, no-PK tables are blocked from row inference, and removals are never implicitly approved. --include-sensitive is an explicit override. Connection passwords are read from WOW_CRUCIBLE_DB_PASSWORD by default and are never accepted as command arguments.", code);

static SqlDatabaseObjectType ParseDatabaseObjectType(string value) => value.Trim().ToLowerInvariant() switch
{
    "view" or "views" => SqlDatabaseObjectType.View,
    "trigger" or "triggers" => SqlDatabaseObjectType.Trigger,
    "procedure" or "procedures" => SqlDatabaseObjectType.Procedure,
    "function" or "functions" => SqlDatabaseObjectType.Function,
    "event" or "events" => SqlDatabaseObjectType.Event,
    _ => throw new ArgumentException($"Unknown database object type '{value}'. Use view, trigger, procedure, function, or event.")
};

static bool RecoveryAuditNeedsReview(LegacyDatabaseAuditManifest manifest) =>
    manifest.Mode == LegacyDatabaseAuditMode.Unattributed ||
    manifest.BaselineIdentity != LegacyDatabaseBaselineIdentity.MatchingCoreIdentity ||
    (manifest.Warnings?.Count ?? 0) > 1 ||
    (manifest.Tables ?? []).Any(table => table.Status is LegacyDatabaseTableAuditStatus.BlockedNoPrimaryKey or
        LegacyDatabaseTableAuditStatus.BlockedIncompatibleSchema or LegacyDatabaseTableAuditStatus.NotCaptured or
        LegacyDatabaseTableAuditStatus.SchemaChanged or LegacyDatabaseTableAuditStatus.BaselineTableOnly ||
        table.RemovedRows > 0 || (table.Findings?.Count ?? 0) > 0);

static int Manifest(string[] args)
{
    if (args.Length > 0 && args[0] is "help" or "--help" or "-h") return ManifestHelp();
    if (args is ["create", var manifestPath, var outputFile, .. var rawInputs] && rawInputs.Length > 0)
    {
        var executableOption = rawInputs.FirstOrDefault(value => value.StartsWith("--client-exe=", StringComparison.OrdinalIgnoreCase));
        var allowed = rawInputs.Where(value => value.StartsWith("--allow=", StringComparison.OrdinalIgnoreCase)).Select(value => value[8..]).ToArray();
        var forbidden = rawInputs.Where(value => value.StartsWith("--deny=", StringComparison.OrdinalIgnoreCase)).Select(value => value[7..]).ToArray();
        var required = rawInputs.Where(value => value.StartsWith("--require=", StringComparison.OrdinalIgnoreCase)).Select(value => value[10..]).ToArray();
        var countOption = rawInputs.FirstOrDefault(value => value.StartsWith("--count=", StringComparison.OrdinalIgnoreCase));
        var unknown = rawInputs.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.StartsWith("--client-exe=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--allow=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--deny=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--require=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--count=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown manifest option: {unknown[0]}");
        var inputs = rawInputs.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (inputs.Length == 0) return Fail("Add at least one file or folder to the manifest.");
        var entries = PatchInputMapper.Map(inputs);
        var hash = executableOption is null ? null : PatchManifestService.ComputeExecutableSha256(executableOption[13..]);
        var policy = allowed.Length == 0 && forbidden.Length == 0 && required.Length == 0 && countOption is null ? null : new PatchManifestPolicy(allowed, forbidden, countOption is null ? null : int.Parse(countOption[8..]), required);
        PatchManifestService.Save(manifestPath, Path.GetFileNameWithoutExtension(manifestPath), outputFile, entries, hash, policy);
        PrintCompatibility(entries, hash);
        return 0;
    }
    switch (args)
    {
        case ["build", var buildManifestPath, var outputDirectory]:
            var manifest = PatchManifestService.Load(buildManifestPath);
            PrintCompatibility(manifest.Entries, manifest.RequiredClientExecutableSha256);
            PatchManifestService.Build(buildManifestPath, outputDirectory); return 0;
        case ["list", var listManifestPath]:
            var listManifest = PatchManifestService.Load(listManifestPath);
            foreach (var entry in listManifest.Entries.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase)) Console.WriteLine($"{entry.SourcePath}\t{entry.ArchivePath}");
            Console.Error.WriteLine($"Dry run: {listManifest.Entries.Count:N0} source-to-archive mapping(s), output {listManifest.OutputFileName}.");
            return 0;
        case ["validate", var validateManifestPath]:
            return PrintManifestValidation(PatchManifestService.Validate(PatchManifestService.Load(validateManifestPath)));
        case ["validate", var validateArchiveManifestPath, var archivePath]:
            return PrintManifestValidation(PatchManifestService.Validate(PatchManifestService.Load(validateArchiveManifestPath), archivePath));
        default:
            return ManifestHelp(2);
    }
}

static int Dbc(string[] args)
{
    if (args.Length == 0) return DbcHelp(2);
    if (args[0] is "help" or "--help" or "-h" || args.Length > 1 && args[1] is "--help" or "-h") return DbcHelp();
    if (args is ["stage-create", var stageDbc, var stageSchema, var stageProject, .. var stageCreateOptions])
    {
        var unknown = stageCreateOptions.Where(option => !option.Equals("--replace", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown stage-create option: {unknown[0]}");
        var stageFile = WdbcFile.Load(stageDbc); var table = Path.GetFileNameWithoutExtension(stageDbc); var schema = ResolveClientTableSchema(stageFile, stageSchema, table);
        if (schema.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(table, stageFile.FieldCount, schema));
        var result = DbcStagingWorkspaceService.Create(stageProject, stageFile, schema, stageCreateOptions.Contains("--replace", StringComparer.OrdinalIgnoreCase), new Progress<(int Done, int Total)>(value => { if (value.Done == value.Total || value.Done % 5000 == 0) Console.Error.WriteLine($"Stage\t{value.Done:N0}/{value.Total:N0}"); }));
        Console.WriteLine($"WORKSPACE\t{result.WorkspacePath}\nTABLE\t{result.Table}\nSOURCE\t{result.SourcePath}\nSOURCE_SHA256\t{result.SourceContentSha256}\nROWS\t{result.SourceRows}\nFIELDS\t{result.Fields}\nKEY\t{result.KeyStrategy.Kind}"); return 0;
    }
    if (args is ["stage-info", var stageInfoPath, .. var stageInfoOptions])
    {
        var json = stageInfoOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var unknown = stageInfoOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown stage-info option: {unknown[0]}");
        var info = DbcStagingWorkspaceService.Inspect(stageInfoPath); var diff = DbcStagingWorkspaceService.Diff(stageInfoPath);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Info = info, Diff = diff }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else { Console.WriteLine($"WORKSPACE\t{info.WorkspacePath}\nTABLE\t{info.Table}\nSOURCE\t{info.SourcePath}\nSOURCE_SHA256\t{info.SourceContentSha256}\nROWS\t{info.SourceRows}\nFIELDS\t{info.Fields}\nSCHEMA\t{info.SchemaMatch}\nKEY\t{info.KeyStrategy.Kind}\nCREATED\t{info.CreatedUtc:O}"); PrintStageDiff(diff); }
        return diff.CanApply ? 0 : 3;
    }
    if (args is ["stage-query", var stageQueryPath, var stageQuerySqlFile, .. var stageQueryOptions])
    {
        var json = stageQueryOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var limitText = Option(stageQueryOptions, "--limit=") ?? "500"; if (!int.TryParse(limitText, out var limit) || limit is < 1 or > 100000) return Fail("--limit must be from 1 to 100000.");
        var unknown = stageQueryOptions.Where(option => !option.StartsWith("--bind=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown stage-query option: {unknown[0]}");
        var result = DbcStagingWorkspaceService.Query(stageQueryPath, File.ReadAllText(stageQuerySqlFile), ParseStageBindings(stageQueryOptions), limit);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { result.Columns, result.Rows, result.Truncated }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else { Console.WriteLine(string.Join('\t', result.Columns)); foreach (var row in result.Rows) Console.WriteLine(string.Join('\t', row.Select(SqlCell))); if (result.Truncated) Console.Error.WriteLine($"Query output stopped at {limit:N0} row(s); refine the query or raise --limit."); }
        return result.Rows.Count > 0 ? 0 : 3;
    }
    if (args is ["stage-mutate", var stageMutationPath, var stageMutationSqlFile, .. var stageMutationOptions])
    {
        var apply = stageMutationOptions.Contains("--apply", StringComparer.OrdinalIgnoreCase); var json = stageMutationOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = stageMutationOptions.Where(option => !option.StartsWith("--bind=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown stage-mutate option: {unknown[0]}");
        var result = DbcStagingWorkspaceService.Mutate(stageMutationPath, File.ReadAllText(stageMutationSqlFile), ParseStageBindings(stageMutationOptions), apply);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else { Console.WriteLine($"MUTATION\taffected={result.AffectedRows:N0}\tapplied={result.Applied}"); PrintStageDiff(result.Diff); }
        if (!apply) Console.Error.WriteLine("Dry-run staging mutation only. Re-run with --apply after reviewing the exact row/cell diff."); return result.Diff.CanApply ? 0 : 3;
    }
    if (args is ["stage-diff", var stageDiffPath, .. var stageDiffOptions])
    {
        var json = stageDiffOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var unknown = stageDiffOptions.Where(option => !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown stage-diff option: {unknown[0]}");
        var diff = DbcStagingWorkspaceService.Diff(stageDiffPath); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(diff, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else PrintStageDiff(diff); return diff.CanApply ? 0 : 3;
    }
    if (args is ["stage-apply", var stageApplyPath, var stageSourceDbc, var stageApplySchema, var stageOutputDbc, .. var stageApplyOptions])
    {
        var apply = stageApplyOptions.Contains("--apply", StringComparer.OrdinalIgnoreCase); var overwrite = stageApplyOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var json = stageApplyOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = stageApplyOptions.Where(option => !option.Equals("--apply", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown stage-apply option: {unknown[0]}");
        var stageSourceFile = WdbcFile.Load(stageSourceDbc); var table = Path.GetFileNameWithoutExtension(stageSourceDbc); var schema = ResolveClientTableSchema(stageSourceFile, stageApplySchema, table); if (schema.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(table, stageSourceFile.FieldCount, schema));
        var plan = DbcStagingWorkspaceService.PreviewApply(stageApplyPath, stageSourceFile, schema); var diff = DbcStagingWorkspaceService.Diff(stageApplyPath);
        if (!apply) { if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Diff = diff, plan.UpdatedRows, plan.AppendedRows, plan.ChangedCells, plan.Warnings }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else { PrintStageDiff(diff); Console.WriteLine($"IMPORT_PREVIEW\tupdated={plan.UpdatedRows:N0}\tappended={plan.AppendedRows:N0}\tcells={plan.ChangedCells:N0}"); } Console.Error.WriteLine("Dry-run DBC publication only. Re-run with --apply to write the explicit output path."); return 0; }
        var result = DbcStagingWorkspaceService.ApplyToOutput(stageApplyPath, stageSourceFile, schema, stageOutputDbc, overwrite); if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else Console.Error.WriteLine($"Published staged DBC atomically: {result.Import.UpdatedRows:N0} updated, {result.Import.AppendedRows:N0} appended, {result.Import.ChangedCells:N0} cells. Output: {result.OutputPath} · SHA256 {result.OutputSha256}"); return 0;
    }
    if(args is ["dbd-info",var dbdPath,var dbdBuildText,..var dbdOptions]&&int.TryParse(dbdBuildText,out var dbdBuild))
    {
        var json=dbdOptions.Contains("--format=json",StringComparer.OrdinalIgnoreCase);var unknown=dbdOptions.Where(value=>!value.Equals("--format=json",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--format=text",StringComparison.OrdinalIgnoreCase)).ToArray();if(unknown.Length>0)return Fail($"Unknown dbd-info option: {unknown[0]}");var definition=DbdSchemaService.Load(dbdPath);var layout=definition.ForBuild(dbdBuild)??throw new KeyNotFoundException($"No layout covers build {dbdBuild:N0}.");var columns=DbdSchemaService.ResolveColumns(definition,dbdBuild);
        if(json)Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{definition.TableName,Build=dbdBuild,LogicalColumns=definition.Columns.Values,layout.Builds,layout.LayoutHashes,layout.Comments,layout.Fields,PhysicalColumns=columns},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));else{Console.WriteLine($"TABLE\t{definition.TableName}\nBUILD\t{dbdBuild}\nLAYOUTS\t{definition.Layouts.Count}\nLOGICAL_COLUMNS\t{definition.Columns.Count}\nPHYSICAL_COLUMNS\t{columns.Count}\nBUILD_RANGES\t{string.Join(" | ",layout.Builds.Select(range=>range.Raw))}\nLAYOUT_HASHES\t{string.Join(",",layout.LayoutHashes)}");foreach(var column in columns)Console.WriteLine($"FIELD\t{column.Index}\t{column.Offset}\t{column.Size}\t{column.Type}\t{column.Name}\t{(column.IsIndex?"ID":"")}");}return 0;
    }
    if(args is ["schema-audit",var definitionsRoot,var auditDbcRoot,var auditBuildText,..var auditOptions]&&int.TryParse(auditBuildText,out var auditBuild))
    {
        var xml=Option(auditOptions,"--xml=");var json=auditOptions.Contains("--format=json",StringComparer.OrdinalIgnoreCase);var roundTrip=auditOptions.Contains("--roundtrip",StringComparer.OrdinalIgnoreCase);var unknown=auditOptions.Where(value=>!value.StartsWith("--xml=",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--roundtrip",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--format=json",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--format=text",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--only-problems",StringComparison.OrdinalIgnoreCase)).ToArray();if(unknown.Length>0)return Fail($"Unknown schema-audit option: {unknown[0]}");var summary=DbdSchemaService.Audit(definitionsRoot,auditDbcRoot,auditBuild,xml,roundTrip);
        if(json)Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));else{Console.WriteLine($"BUILD\t{summary.Build}\nTABLES\t{summary.Rows.Count}\nMATCHES\t{summary.Matches}\nEMPTY_PLACEHOLDERS\t{summary.EmptyPlaceholders}\nROUNDTRIP_VERIFIED\t{summary.RoundTripVerified}\nPROBLEMS\t{summary.Failures}");foreach(var row in summary.Rows.Where(row=>!auditOptions.Contains("--only-problems",StringComparer.OrdinalIgnoreCase)||row.Status is not DbdAuditStatus.Match and not DbdAuditStatus.EmptyPlaceholder))Console.WriteLine($"{row.Status.ToString().ToUpperInvariant()}\t{row.Table}\tCONTAINER={row.Container??"-"}\tFIELDS={row.ActualFields}\tBYTES={row.RecordSize?.ToString()??"-"}\tDBD={row.DbdFields?.ToString()??"-"}\tXML={row.XmlFields?.ToString()??"-"}\tROUNDTRIP={(row.ByteIdenticalRoundTrip is null?"-":row.ByteIdenticalRoundTrip==true?"EXACT":"FAILED")}\t{row.Message}");}return summary.Failures==0?0:3;
    }
    if (args is ["lighting", var lightingRoot, .. var lightingOptions])
    {
        var map = ParseIntOption(lightingOptions, "--map=", -1); var lightId = ParseIntOption(lightingOptions, "--light=", -1); var slot = ParseIntOption(lightingOptions, "--slot=", 1); var time = ParseIntOption(lightingOptions, "--time=", 1440); var json = lightingOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = lightingOptions.Where(option => !option.StartsWith("--map=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--light=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--slot=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--time=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown lighting option: {unknown[0]}"); if (map < -1 || lightId < -1 || slot is < 1 or > 8 || time is < 0 or > WorldLightingService.DayUnits) return Fail("--map and --light must be non-negative; --slot must be 1..8; --time must be 0..2880.");
        var catalog = WorldLightingService.Load(lightingRoot); IEnumerable<WorldLightRecord> selected = catalog.Lights; if (map >= 0) selected = selected.Where(light => light.ContinentId == (uint)map); if (lightId >= 0) selected = selected.Where(light => light.Id == (uint)lightId); var lights = selected.ToArray();
        var profiles = lights.Select(light => (Light: light, Profile: WorldLightingService.Resolve(catalog, light, slot - 1))).ToArray();
        if (json)
        {
            var payload = new { catalog.DbcDirectory, Counts = new { Lights = catalog.Lights.Count, Parameters = catalog.Parameters.Count, ColorBands = catalog.ColorBands.Count, FloatBands = catalog.FloatBands.Count, Skyboxes = catalog.Skyboxes.Count }, catalog.Findings, Slot = slot, Time = time, Lights = profiles.Select(value => new { value.Light, value.Profile.ParamsId, value.Profile.Parameters, value.Profile.Skybox, value.Profile.Findings, Colors = value.Profile.ColorBands.Select(band => new { band.Id, band.Index, band.Name, Value = band.Keys.Count == 0 ? (WorldLightColor?)null : WorldLightingService.Sample(band, time), Keys = band.Keys.Count }), Floats = value.Profile.FloatBands.Select(band => new { band.Id, band.Index, band.Name, Value = band.Keys.Count == 0 ? (float?)null : WorldLightingService.Sample(band, time), Keys = band.Keys.Count }) }) };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"LIGHTING\tlights={catalog.Lights.Count:N0}\tparams={catalog.Parameters.Count:N0}\tcolors={catalog.ColorBands.Count:N0}\tfloats={catalog.FloatBands.Count:N0}\tskyboxes={catalog.Skyboxes.Count:N0}\tfindings={catalog.Findings.Count:N0}");
            foreach (var value in profiles) { var light = value.Light; Console.WriteLine($"LIGHT\t{light.Id}\tmap={light.ContinentId}\tworld={light.WorldX:0.###},{light.WorldY:0.###},{light.WorldZ:0.###}\tfalloff={light.FalloffStart:0.###}..{light.FalloffEnd:0.###}\tglobal={light.IsGlobal}\tparams={value.Profile.ParamsId}\tskybox={value.Profile.Skybox?.Id.ToString() ?? "-"}"); if (lightId >= 0) { foreach (var band in value.Profile.ColorBands) Console.WriteLine($"COLOR\t{band.Id}\t{band.Name}\t{(band.Keys.Count == 0 ? "NO_KEYS" : WorldLightingService.Sample(band, time).Hex)}\tkeys={band.Keys.Count}"); foreach (var band in value.Profile.FloatBands) Console.WriteLine($"FLOAT\t{band.Id}\t{band.Name}\t{(band.Keys.Count == 0 ? "NO_KEYS" : WorldLightingService.Sample(band, time).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))}\tkeys={band.Keys.Count}"); } }
            foreach (var finding in catalog.Findings) Console.WriteLine($"FINDING\t{finding}");
        }
        return lights.Length > 0 ? 0 : 3;
    }
    if (args is ["lighting-scene", var sceneRoot, var sceneLightText, .. var sceneOptions] && uint.TryParse(sceneLightText, out var sceneLightId))
    {
        var slot = ParseIntOption(sceneOptions, "--slot=", 1); var time = ParseIntOption(sceneOptions, "--time=", 1440); var json = sceneOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var unknown = sceneOptions.Where(option => !option.StartsWith("--slot=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--time=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown lighting-scene option: {unknown[0]}"); if (slot is < 1 or > 8 || time is < 0 or > WorldLightingService.DayUnits) return Fail("--slot must be 1..8 and --time must be 0..2880.");
        var catalog = WorldLightingService.Load(sceneRoot); var light = catalog.Lights.FirstOrDefault(value => value.Id == sceneLightId) ?? throw new KeyNotFoundException($"Light {sceneLightId:N0} was not found."); var profile = WorldLightingService.Resolve(catalog, light, slot - 1); var scene = WorldLightingEnvironmentService.Compose(profile, time);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Light = light, Profile = profile, Scene = scene }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"LIGHTING_SCENE\tlight={light.Id}\tmap={light.ContinentId}\tparams={profile.ParamsId}\tslot={slot}\ttime={scene.Time}\tclock={scene.Clock}\tskybox={profile.Skybox?.ClientModelPath ?? "-"}\tfindings={scene.Findings.Count}");
            foreach (var stop in scene.Sky) Console.WriteLine($"SKY\t{stop.Position:0.##}\t{stop.Role}\t{stop.Color.Hex}"); Console.WriteLine($"ENV\tambient={scene.GlobalAmbient.Hex}\tdiffuse={scene.GlobalDiffuse.Hex}\tfog={scene.Fog.Hex}\tsun={scene.Sun.Hex}\tcloud={scene.CloudBase.Hex},{scene.CloudEdge.Hex},{scene.CloudAccent.Hex}\tsun_position={scene.SunX:0.###},{scene.SunY:0.###}\tabove_horizon={scene.SunAboveHorizon}"); Console.WriteLine($"OCEAN\tshallow={scene.OceanShallow.Hex}\tdeep={scene.OceanDeep.Hex}"); Console.WriteLine($"FRESH_WATER\tshallow={scene.FreshWaterShallow.Hex}\tdeep={scene.FreshWaterDeep.Hex}"); foreach (var finding in scene.Findings) Console.WriteLine($"FINDING\t{finding}");
        }
        return scene.Findings.Count == 0 ? 0 : 3;
    }
    if (args is ["lighting-band-set", var bandInput, var bandIdText, var bandOutput, .. var bandOptions] && uint.TryParse(bandIdText, out var bandId))
    {
        var keyOptions = bandOptions.Where(option => option.StartsWith("--key=", StringComparison.OrdinalIgnoreCase)).Select(option => option[6..]).ToArray(); var overwrite = bandOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase); var inPlace = bandOptions.Contains("--in-place", StringComparer.OrdinalIgnoreCase); var planPath = Option(bandOptions, "--plan="); var json = bandOptions.Contains("--format=json", StringComparer.OrdinalIgnoreCase);
        var unknown = bandOptions.Where(option => !option.StartsWith("--key=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--plan=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--in-place", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown lighting-band-set option: {unknown[0]}"); if (keyOptions.Length == 0) return Fail("lighting-band-set requires one or more --key=time:value options.");
        WorldLightingBandEditPlan plan; var name = Path.GetFileName(bandInput);
        if (name.Equals("LightIntBand.dbc", StringComparison.OrdinalIgnoreCase))
        {
            var keys = keyOptions.Select(ParseLightingKey).Select(key => (key.Time, Color: ParseLightingColor(key.Value))).ToArray(); plan = WorldLightingEditService.PlanColor(bandInput, bandId, keys);
        }
        else if (name.Equals("LightFloatBand.dbc", StringComparison.OrdinalIgnoreCase))
        {
            var keys = keyOptions.Select(ParseLightingKey).Select(key => (key.Time, Value: float.TryParse(key.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && float.IsFinite(value) ? value : throw new ArgumentException($"Invalid finite float lighting value: {key.Value}"))).ToArray(); plan = WorldLightingEditService.PlanFloat(bandInput, bandId, keys);
        }
        else return Fail("lighting-band-set input must be named LightIntBand.dbc or LightFloatBand.dbc so its record type is unambiguous.");
        if (planPath is not null) WorldLightingEditService.SavePlan(plan, planPath, overwrite); var result = WorldLightingEditService.Apply(plan, bandOutput, overwrite, allowSourceReplacement: inPlace);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Plan = plan, Result = result }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else Console.WriteLine($"LIGHTING_BAND_WRITTEN\tkind={result.Kind}\tband={result.BandId}\tkeys={result.Keys}\toutput={result.OutputPath}\tsha256={result.OutputSha256}\tbackup={result.BackupPath ?? "-"}\treceipt={result.ReceiptPath}"); return 0;
    }
    if (args is ["spell-tooltip", var tooltipDbc, .. var tooltipIds] && tooltipIds.Length > 0)
    {
        var json=tooltipIds.Contains("--format=json",StringComparer.OrdinalIgnoreCase);var tooltipIdTexts=tooltipIds.Where(value=>!value.StartsWith("--",StringComparison.Ordinal)).ToArray();var unknown=tooltipIds.Where(value=>value.StartsWith("--",StringComparison.Ordinal)&&!value.Equals("--format=json",StringComparison.OrdinalIgnoreCase)&&!value.Equals("--format=text",StringComparison.OrdinalIgnoreCase)).ToArray();if(unknown.Length>0)return Fail($"Unknown spell-tooltip option: {unknown[0]}");
        var catalog=SpellTooltipService.Load(tooltipDbc);var records=tooltipIdTexts.Select(value=>uint.TryParse(value,out var id)?catalog.Records.GetValueOrDefault(id):throw new ArgumentException($"Invalid spell ID: {value}")).ToArray();if(json)Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(records,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));else foreach(var record in records)Console.WriteLine(record is null?"MISSING":$"SPELL\t{record.Id}\t{record.Name}\t{record.Subtext}\nDESCRIPTION\t{record.Description}\nAURA\t{record.AuraDescription}");return records.All(record=>record is not null)?0:3;
    }
    if (args is ["item-display", var displayDbc, var displaySchema, var displayIdText, .. var displayOptions] && uint.TryParse(displayIdText, out var displayId))
    {
        var itemClass = ParseIntOption(displayOptions, "--class=", 0); var subclass = ParseIntOption(displayOptions, "--subclass=", 0); var inventory = ParseIntOption(displayOptions, "--inventory=", 0);
        var assets = Option(displayOptions, "--assets="); var json = displayOptions.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
        var unknown = displayOptions.Where(option => !option.StartsWith("--class=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--subclass=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--inventory=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--assets=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-display option: {unknown[0]}");
        var result = ItemDisplayInfoService.Resolve(displayDbc, displaySchema == "-" ? null : displaySchema, displayId, itemClass, subclass, inventory, assets);
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"DISPLAY\t{result.Id}\nMODEL\t{string.Join(" | ", result.ModelNames.Where(value => value.Length > 0))}\nICON\t{string.Join(" | ", result.InventoryIcons.Where(value => value.Length > 0))}\nGEOSETS\t{string.Join(",", result.GeosetGroups)}\nHELMET_VIS\t{string.Join(",", result.HelmetGeosetVisibility)}\nFLAGS\t0x{result.Flags:X8}\nSPELL_VISUAL\t{result.SpellVisualId}\nITEM_VISUAL\t{result.ItemVisualId}\nPARTICLE_COLOR\t{result.ParticleColorId}\nSOUND_GROUP\t{result.GroupSoundIndex}");
            foreach (var asset in result.Assets) Console.WriteLine($"ASSET\t{asset.Kind}\t{asset.Slot}\t{asset.Name}\t{string.Join(" | ", asset.ClientPaths)}\t{(asset.ExistingPaths.Count == 0 ? "MISSING" : string.Join(" | ", asset.ExistingPaths))}");
        }
        return 0;
    }
    if (args is ["item-equipped", var equipmentDbc, var equipmentSchema, var equipmentIdText, var baseSkin, var outputAtlas, .. var equipmentOptions] && uint.TryParse(equipmentIdText, out var equipmentId))
    {
        var itemClass = ParseIntOption(equipmentOptions, "--class=", 4); var subclass = ParseIntOption(equipmentOptions, "--subclass=", 0); var inventory = ParseIntOption(equipmentOptions, "--inventory=", 0);
        var assets = Option(equipmentOptions, "--assets=") ?? throw new ArgumentException("item-equipped requires --assets=processed-library."); var requestedSource = Option(equipmentOptions, "--source=");
        var unknown = equipmentOptions.Where(option => !option.StartsWith("--class=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--subclass=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--inventory=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--assets=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown item-equipped option: {unknown[0]}");
        var display = ItemDisplayInfoService.Resolve(equipmentDbc, equipmentSchema == "-" ? null : equipmentSchema, equipmentId, itemClass, subclass, inventory, assets);
        var sources = ItemEquipmentPreviewService.FindWearSources(display); if (sources.Count == 0) throw new FileNotFoundException("No extracted wear textures for this display were found in the processed asset library.");
        var source = requestedSource is null ? sources[0] : sources.FirstOrDefault(value => value.Source.Equals(requestedSource, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"Wear source '{requestedSource}' was not found. Available: {string.Join(", ", sources.Select(value => value.Source))}");
        var preview = ItemEquipmentPreviewService.Compose(baseSkin, display, inventory, source); BlpTextureService.WritePng(outputAtlas, preview.Atlas, equipmentOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase));
        Console.WriteLine($"DISPLAY\t{display.Id}\nSOURCE\t{source.Source}\nATLAS\t{Path.GetFullPath(outputAtlas)}\nWEAR_SLOTS\t{string.Join(",", preview.AppliedSlots)}\nMISSING_SLOTS\t{string.Join(",", preview.MissingSlots)}\nGEOSETS\t{string.Join(",", preview.Geosets.GroupVariants.Select(pair => $"{pair.Key}:{pair.Value}"))}"); return 0;
    }
    if (args is ["itemset", "inspect", var itemSetPath, var itemSetSchema, var itemSetIdText, .. var itemSetOptions] && uint.TryParse(itemSetIdText, out var itemSetId))
    {
        var spellPath = Option(itemSetOptions, "--spell="); var unknown = itemSetOptions.Where(option => !option.StartsWith("--spell=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown itemset inspect option: {unknown[0]}");
        var set = ItemSetDbcService.Inspect(itemSetPath, itemSetSchema, itemSetId, spellPath);
        Console.WriteLine($"ID\t{set.Id}\nName\t{set.Name}\nRequiredSkill\t{set.RequiredSkill}\nRequiredSkillRank\t{set.RequiredSkillRank}\nItems\t{string.Join(",", set.ItemIds)}");
        foreach (var effect in set.Effects) Console.WriteLine($"EFFECT\t{effect.Slot}\t{effect.RequiredItems}\t{effect.SpellId}\t{effect.SpellName ?? "unknown spell"}"); return 0;
    }
    if (args is ["itemset", "clone", var cloneItemSetPath, var cloneItemSetSchema, var cloneItemSetOutput, var sourceSetText, var newSetText, .. var itemSetCloneOptions] && uint.TryParse(sourceSetText, out var sourceSet) && uint.TryParse(newSetText, out var newSet))
    {
        var mapText = Option(itemSetCloneOptions, "--map=") ?? throw new ArgumentException("Item-set cloning requires --map=old:new,old:new for every member."); var suffix = Option(itemSetCloneOptions, "--suffix=") ?? " Variant";
        var unknown = itemSetCloneOptions.Where(option => !option.StartsWith("--map=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--suffix=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown itemset clone option: {unknown[0]}");
        var map = mapText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(pair => pair.Split(':')).ToDictionary(pair => uint.Parse(pair[0]), pair => uint.Parse(pair[1]));
        var result = ItemSetDbcService.Clone(cloneItemSetPath, cloneItemSetSchema, cloneItemSetOutput, sourceSet, newSet, map, suffix);
        Console.Error.WriteLine($"Cloned item set {result.SourceSetId} to {result.NewSetId} '{result.Name}' with {result.ItemIdMap.Count:N0} remapped member(s): {result.OutputPath}"); return 0;
    }
    if (args is ["itemset", "effects", var effectItemSetPath, var effectItemSetSchema, var effectItemSetOutput, var effectSetText, .. var effectOptions] && uint.TryParse(effectSetText, out var effectSet))
    {
        var unknown = effectOptions.Where(option => !option.StartsWith("--effect=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown itemset effects option: {unknown[0]}");
        var effects = effectOptions.Where(option => option.StartsWith("--effect=", StringComparison.OrdinalIgnoreCase)).Select((option, index) =>
        {
            var pair = option[9..].Split(':'); if (pair.Length != 2) throw new ArgumentException("Each --effect must be required-items:spell-id."); return new ItemSetEffect(index + 1, uint.Parse(pair[0]), uint.Parse(pair[1]), null);
        }).ToArray();
        ItemSetDbcService.SetEffects(effectItemSetPath, effectItemSetSchema, effectItemSetOutput, effectSet, effects); Console.Error.WriteLine($"Wrote {effects.Length:N0} item-set effect slot(s) for set {effectSet}: {Path.GetFullPath(effectItemSetOutput)}"); return 0;
    }
    if (args is ["rows", var rowsPath, var rowsSchemaPath, .. var rawIds] && rawIds.Length > 0)
    {
        var rowsFile = WdbcFile.Load(rowsPath); var tableName = Path.GetFileNameWithoutExtension(rowsPath);
        var resolution = ResolveClientTableSchema(rowsFile, rowsSchemaPath, tableName);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, rowsFile.FieldCount, resolution));
        var indexed = DbcRecordIdentity.IndexRows(rowsFile, resolution.Columns, resolution.KeyStrategy); var rows = new List<object>();
        foreach (var rawId in rawIds)
        {
            if (!uint.TryParse(rawId, out var id)) return Fail($"Invalid row ID: {rawId}");
            if (!indexed.TryGetValue(id, out var row)) { rows.Add(new { Id = id, Missing = true, Values = new Dictionary<string, object?>() }); continue; }
            rows.Add(new { Id = id, Missing = false, Values = resolution.Columns.ToDictionary(column => column.Name, column => rowsFile.GetDisplayValue(row, column)) });
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return rows.Any() ? 0 : 3;
    }
    if (args is ["export", var exportPath, var exportSchemaPath, var exportOutput, .. var exportOptions])
    {
        var formatText = Option(exportOptions, "--format=") ?? Path.GetExtension(exportOutput).ToLowerInvariant() switch { ".csv" => "csv", ".json" => "json", _ => "jsonl" };
        var format = formatText.ToLowerInvariant() switch { "csv" => DbcRowExportFormat.Csv, "json" => DbcRowExportFormat.Json, "jsonl" or "json-lines" => DbcRowExportFormat.JsonLines, _ => throw new ArgumentException("--format must be csv, json, or jsonl.") };
        var columns = exportOptions.Where(option => option.StartsWith("--column=", StringComparison.OrdinalIgnoreCase)).Select(option => option[9..])
            .Concat((Option(exportOptions, "--columns=") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        var keys = exportOptions.Where(option => option.StartsWith("--id=", StringComparison.OrdinalIgnoreCase)).Select(option => option[5..])
            .Concat((Option(exportOptions, "--ids=") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => uint.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"Invalid DBC export ID: {value}")).ToArray();
        var rawStrings = exportOptions.Contains("--raw-string-offsets", StringComparer.OrdinalIgnoreCase); var overwrite = exportOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var unknown = exportOptions.Where(option => !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--column=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--columns=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--id=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ids=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--raw-string-offsets", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown DBC export option: {unknown[0]}");
        var exportFile = WdbcFile.Load(exportPath); var table = Path.GetFileNameWithoutExtension(exportPath); var resolution = ResolveClientTableSchema(exportFile, exportSchemaPath, table);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(table, exportFile.FieldCount, resolution));
        var result = DbcRowExportService.Export(exportFile, resolution, exportOutput, new(format, columns, keys, rawStrings, overwrite));
        Console.Error.WriteLine($"Exported {result.ExportedRows:N0}/{result.SourceRows:N0} {table} row(s), {result.Columns.Count:N0} output columns, decoded strings={!rawStrings}: {result.OutputPath}"); return 0;
    }
    if (args is ["import", var importPath, var importSchemaPath, var importDataPath, .. var importOptions])
    {
        var formatText = Option(importOptions, "--format=") ?? DbcRowImportService.InferFormat(importDataPath).ToString();
        var format = formatText.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "csv" => DbcRowImportFormat.Csv,
            "json" => DbcRowImportFormat.Json,
            "jsonl" or "jsonlines" or "ndjson" => DbcRowImportFormat.JsonLines,
            _ => throw new ArgumentException("--format must be csv, json, or jsonl.")
        };
        var output = Option(importOptions, "--output="); var append = importOptions.Contains("--append", StringComparer.OrdinalIgnoreCase);
        var rawStrings = importOptions.Contains("--raw-string-offsets", StringComparer.OrdinalIgnoreCase); var overwrite = importOptions.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var jsonReport = importOptions.Contains("--report=json", StringComparer.OrdinalIgnoreCase);
        var unknown = importOptions.Where(option => !option.StartsWith("--format=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) &&
            !option.Equals("--append", StringComparison.OrdinalIgnoreCase) && !option.Equals("--raw-string-offsets", StringComparison.OrdinalIgnoreCase) &&
            !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && !option.Equals("--report=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--report=text", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown DBC import option: {unknown[0]}");
        var importFile = WdbcFile.Load(importPath); var table = Path.GetFileNameWithoutExtension(importPath); var resolution = ResolveClientTableSchema(importFile, importSchemaPath, table);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(table, importFile.FieldCount, resolution));
        if (output is not null)
        {
            var fullOutput = Path.GetFullPath(output);
            if (fullOutput.Equals(Path.GetFullPath(importDataPath), StringComparison.OrdinalIgnoreCase) || fullOutput.Equals(Path.GetFullPath(importSchemaPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("DBC import output cannot replace its structured input or schema definition.");
            if (File.Exists(fullOutput) && !overwrite) throw new IOException($"Import output already exists: {fullOutput}. Use --overwrite explicitly to replace it with a .bak backup.");
        }
        var plan = DbcRowImportService.Preview(importFile, resolution, importDataPath, new(format, append, rawStrings));
        if (jsonReport) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Table = table, plan.InputPath, plan.InputSha256, plan.SourceContentSha256, plan.Format, plan.InputRows, plan.UpdatedRows, plan.AppendedRows, plan.ChangedCells, plan.HasChanges, plan.Warnings, plan.Changes }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        else
        {
            Console.WriteLine($"PLAN\t{table}\tinput={plan.InputRows}\tupdated={plan.UpdatedRows}\tappended={plan.AppendedRows}\tcells={plan.ChangedCells}\tformat={plan.Format}");
            foreach (var warning in plan.Warnings) Console.WriteLine($"WARN\t{warning}");
            foreach (var change in plan.Changes.Take(200)) Console.WriteLine($"CHANGE\tinput={change.InputRow}\tkey={change.RecordKey?.ToString() ?? "-"}\trow={change.TargetRow}\t{change.Column}\t{change.Before}\t=>\t{change.After}");
            if (plan.Changes.Count > 200) Console.WriteLine($"MORE\t{plan.Changes.Count - 200:N0} additional cell change(s); use --report=json for the complete plan.");
        }
        if (output is null) { Console.Error.WriteLine($"Dry-run import preview only. No client table changed; add --output=changed{Path.GetExtension(importPath)} to apply this exact plan to a new/explicitly overwritten output."); return 0; }
        var result = DbcRowImportService.Apply(importFile, plan); output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!); importFile.Save(output, overwrite);
        Console.Error.WriteLine($"Applied structured import atomically: {result.UpdatedRows:N0} updated row(s), {result.AppendedRows:N0} appended row(s), {result.ChangedCells:N0} changed cell(s), {result.ResultRows:N0} result rows. Output: {output}{(overwrite ? $" · previous output backed up to {output}.bak" : string.Empty)}"); return 0;
    }
    if (args is ["find", var findPath, var findSchemaPath, var findColumnName, .. var findValues] && findValues.Length > 0)
    {
        var countOnly = findValues.Contains("--count", StringComparer.OrdinalIgnoreCase);
        var limitOption = findValues.FirstOrDefault(value => value.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase));
        var unknown = findValues.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.Equals("--count", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown DBC find option: {unknown[0]}");
        var requestedValues = findValues.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (requestedValues.Length == 0) return Fail("DBC find requires at least one value.");
        var limit = limitOption is null ? int.MaxValue : int.Parse(limitOption[8..]);
        if (limit < 1) return Fail("DBC find limit must be positive.");
        var findFile = WdbcFile.Load(findPath); var tableName = Path.GetFileNameWithoutExtension(findPath);
        var resolution = ResolveClientTableSchema(findFile, findSchemaPath, tableName);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, findFile.FieldCount, resolution));
        var column = resolution.Columns.FirstOrDefault(value => value.Name.Equals(findColumnName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{tableName} has no named column '{findColumnName}'.");
        var wanted = requestedValues.ToHashSet(StringComparer.OrdinalIgnoreCase); var matches = new List<uint>();
        foreach (var (id, row) in DbcRecordIdentity.IndexRows(findFile, resolution.Columns, resolution.KeyStrategy).OrderBy(pair => pair.Key))
        {
            var value = Convert.ToString(findFile.GetDisplayValue(row, column), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (wanted.Contains(value)) matches.Add(id);
        }
        if (countOnly) Console.WriteLine(matches.Count);
        else Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(matches.Take(limit), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"Found {matches.Count:N0} {tableName} record(s) whose {column.Name} matches {wanted.Count:N0} requested value(s).");
        return matches.Count > 0 ? 0 : 3;
    }
    if (args.Length is 4 or 5 && args[0] == "compare" && (args.Length == 4 || args[4] == "--summary"))
    {
        var basePath = args[1]; var overridePath = args[2]; var schemaPath = args[3];
        var tableName = Path.GetFileNameWithoutExtension(basePath);
        var sample = WdbcFile.Load(basePath);
        var resolution = ResolveClientTableSchema(sample, schemaPath, tableName);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        var differences = DbcPromotionService.GetDifferences(basePath, overridePath, resolution.Columns, resolution.KeyStrategy);
        if (args.Length == 5)
        {
            var overrideFile = WdbcFile.Load(overridePath);
            var baseIds = DbcRecordIdentity.IndexRows(sample, resolution.Columns, resolution.KeyStrategy).Keys;
            var overrideIds = DbcRecordIdentity.IndexRows(overrideFile, resolution.Columns, resolution.KeyStrategy).Keys.ToHashSet();
            var removedRows = baseIds.Count(id => !overrideIds.Contains(id));
            Console.WriteLine($"{tableName}\t{sample.RowCount}\t{overrideFile.RowCount}\t{differences.Select(difference => difference.Id).Distinct().Count()}\t{differences.Count}\t{differences.Count(difference => difference.ColumnIndex < 0)}\t{removedRows}");
        }
        else foreach (var difference in differences) Console.WriteLine($"{difference.Id}\t{difference.ColumnName}\t{difference.BaseValue}\t{difference.OverrideValue}");
        Console.Error.WriteLine($"Found {differences.Count:N0} semantic field difference(s)/new row marker(s).");
        return 0;
    }
    if (args is ["promote", "apply", var promotionBasePath, var promotionOverridePath, var promotionSchemaPath, var manifestPath, var outputPath])
    {
        var tableName = Path.GetFileNameWithoutExtension(promotionBasePath);
        var sample = WdbcFile.Load(promotionBasePath);
        var resolution = ResolveClientTableSchema(sample, promotionSchemaPath, tableName);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        DbcPromotionService.Apply(promotionBasePath, promotionOverridePath, outputPath, resolution.Columns, resolution.KeyStrategy, DbcPromotionService.LoadManifest(manifestPath));
        Console.Error.WriteLine($"Created promoted client table: {Path.GetFullPath(outputPath)}");
        return 0;
    }
    if (args is ["promote", "additions", var additionsBasePath, var additionsOverridePath, var additionsSchemaPath, var additionsManifestPath, var additionsOutputPath])
    {
        var tableName = Path.GetFileNameWithoutExtension(additionsBasePath); var sample = WdbcFile.Load(additionsBasePath);
        var resolution = ResolveClientTableSchema(sample, additionsSchemaPath, tableName);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        var manifest = DbcPromotionService.CreateAdditionsManifest(additionsBasePath, additionsOverridePath, resolution.Columns, resolution.KeyStrategy);
        if (manifest.Operations.Count == 0) { Console.Error.WriteLine($"{tableName} contains no IDs absent from the base; no output was written."); return 3; }
        DbcPromotionService.SaveManifest(additionsManifestPath, manifest);
        DbcPromotionService.Apply(additionsBasePath, additionsOverridePath, additionsOutputPath, resolution.Columns, resolution.KeyStrategy, manifest);
        Console.Error.WriteLine($"Added {manifest.Operations.Count:N0} previously absent {tableName} record(s) without modifying existing IDs. Manifest: {Path.GetFullPath(additionsManifestPath)}. Output: {Path.GetFullPath(additionsOutputPath)}");
        return 0;
    }
    if (args is ["clone-remap", "where", var cloneBasePath, var cloneSourcePath, var cloneSchemaPath, var cloneColumnName, .. var cloneArguments])
    {
        var cloneManifestPath = Option(cloneArguments, "--manifest="); var cloneOutputPath = Option(cloneArguments, "--output="); var startText = Option(cloneArguments, "--start-id=");
        if (cloneManifestPath is null || cloneOutputPath is null) return Fail("Clone/remap requires --manifest=plan.json and --output=merged.dbc.");
        uint? startId = startText is null ? null : uint.TryParse(startText, out var parsedStart) ? parsedStart : throw new ArgumentException("Clone/remap start ID must be an unsigned integer.");
        var values = cloneArguments.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = cloneArguments.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.StartsWith("--manifest=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--output=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--start-id=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown clone/remap option: {unknown[0]}");
        if (values.Count == 0) return Fail("Clone/remap where requires at least one field value.");
        var baseFile = WdbcFile.Load(cloneBasePath); var sourceFile = WdbcFile.Load(cloneSourcePath); var tableName = baseFile.LogicalTableName;
        if (!baseFile.LogicalTableName.Equals(sourceFile.LogicalTableName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Base and source client-table identities differ.");
        var resolution = ResolveClientTableSchema(sourceFile, cloneSchemaPath, tableName);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sourceFile.FieldCount, resolution));
        var column = resolution.Columns.FirstOrDefault(value => value.Name.Equals(cloneColumnName, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{tableName} has no named column '{cloneColumnName}'.");
        var sourceIds = DbcRecordIdentity.IndexRows(sourceFile, resolution.Columns, resolution.KeyStrategy).Where(pair => values.Contains(Convert.ToString(sourceFile.GetDisplayValue(pair.Value, column), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)).Select(pair => pair.Key).ToArray();
        var manifest = DbcCloneRemapService.CreateManifest(cloneBasePath, cloneSourcePath, resolution.Columns, resolution.KeyStrategy, sourceIds, startId);
        DbcCloneRemapService.Save(cloneManifestPath, manifest); DbcCloneRemapService.Apply(cloneBasePath, cloneSourcePath, cloneOutputPath, resolution.Columns, resolution.KeyStrategy, manifest);
        var cloned = manifest.Entries.Count(entry => !entry.ReusesExisting); var reused = manifest.Entries.Count - cloned; var identical = sourceIds.Distinct().Count() - manifest.Entries.Count;
        Console.Error.WriteLine($"{tableName}: added/cloned {cloned:N0}, reused {reused:N0} equivalent existing record(s), skipped {identical:N0} identical same-ID record(s). Existing records were not modified. Mapping: {Path.GetFullPath(cloneManifestPath)}");
        return 0;
    }
    if (args is ["clone-dependency", var parentSourcePath, var parentMergedPath, var parentSchemaPath, var parentMapPath, var foreignColumnName, var childBasePath, var childSourcePath, var childSchemaPath, .. var dependencyOptions])
    {
        var childMapPath = Option(dependencyOptions, "--child-map="); var childOutputPath = Option(dependencyOptions, "--child-output="); var parentOutputPath = Option(dependencyOptions, "--parent-output=");
        if (childMapPath is null || childOutputPath is null || parentOutputPath is null) return Fail("Clone dependency requires --child-map=, --child-output=, and --parent-output= paths.");
        var unknown = dependencyOptions.Where(value => !value.StartsWith("--child-map=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--child-output=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--parent-output=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown clone-dependency option: {unknown[0]}");
        var parentMap = DbcCloneRemapService.Load(parentMapPath); var parentSource = WdbcFile.Load(parentSourcePath); var parentTable = Path.GetFileNameWithoutExtension(parentSourcePath);
        var parentResolution = ResolveClientTableSchema(parentSource, parentSchemaPath, parentTable);
        if (parentResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(parentTable, parentSource.FieldCount, parentResolution));
        var childBase = WdbcFile.Load(childBasePath); var childTable = Path.GetFileNameWithoutExtension(childBasePath);
        var childResolution = ResolveClientTableSchema(childBase, childSchemaPath, childTable);
        if (childResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(childTable, childBase.FieldCount, childResolution));
        var referencedIds = DbcCloneRemapService.FindReferencedIds(parentSourcePath, parentResolution.Columns, parentResolution.KeyStrategy, parentMap.Entries.Select(entry => entry.SourceId), foreignColumnName);
        var childSource = WdbcFile.Load(childSourcePath); var childBaseRows = DbcRecordIdentity.IndexRows(childBase, childResolution.Columns, childResolution.KeyStrategy); var childSourceRows = DbcRecordIdentity.IndexRows(childSource, childResolution.Columns, childResolution.KeyStrategy);
        var changedReferencedIds = referencedIds.Where(id => childSourceRows.TryGetValue(id, out var sourceRow) && (!childBaseRows.TryGetValue(id, out var baseRow) || !DbcRowsEqual(childBase, baseRow, childSource, sourceRow, childResolution.Columns))).ToArray();
        if (changedReferencedIds.Length == 0) throw new InvalidOperationException($"No referenced {childTable} records differ from the baseline; the cloned parent can keep its existing references.");
        var childMap = DbcCloneRemapService.CreateManifest(childBasePath, childSourcePath, childResolution.Columns, childResolution.KeyStrategy, changedReferencedIds);
        DbcCloneRemapService.Save(childMapPath, childMap); DbcCloneRemapService.Apply(childBasePath, childSourcePath, childOutputPath, childResolution.Columns, childResolution.KeyStrategy, childMap);
        var changed = DbcCloneRemapService.ApplyReferenceMap(parentMergedPath, parentOutputPath, parentResolution.Columns, parentResolution.KeyStrategy, parentMap.Entries.Where(entry => !entry.ReusesExisting).Select(entry => entry.TargetId), foreignColumnName, childMap);
        Console.Error.WriteLine($"Added/cloned {childMap.Entries.Count(entry => !entry.ReusesExisting):N0} and reused {childMap.Entries.Count(entry => entry.ReusesExisting):N0} referenced {childTable} record(s); rewrote {changed:N0} newly added {parentTable}.{foreignColumnName} reference(s). Baseline records were not modified.");
        return 0;
    }
    if (args is ["copy-row", var copyBasePath, var copySourcePath, var copySchemaPath, var copySourceIdText, var copyTargetIdText, var copyOutputPath, .. var copyOptions])
    {
        if (!uint.TryParse(copySourceIdText, out var copySourceId) || !uint.TryParse(copyTargetIdText, out var copyTargetId)) return Fail("Source and target IDs must be unsigned integers.");
        var copyValues = ParseSetOptions(copyOptions); var copySample = WdbcFile.Load(copyBasePath); var copyTable = Path.GetFileNameWithoutExtension(copyBasePath);
        var copyResolution = ResolveClientTableSchema(copySample, copySchemaPath, copyTable);
        if (copyResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(copyTable, copySample.FieldCount, copyResolution));
        DbcRowMutationService.CopyRow(copyBasePath, copySourcePath, copyOutputPath, copyResolution.Columns, copyResolution.KeyStrategy, copySourceId, copyTargetId, copyValues);
        Console.Error.WriteLine($"Copied {copyTable} ID {copySourceId} to additive ID {copyTargetId} with {copyValues.Count:N0} field override(s): {Path.GetFullPath(copyOutputPath)}");
        return 0;
    }
    if (args is ["set-row", var setInputPath, var setSchemaPath, var setIdText, var setOutputPath, .. var setOptions])
    {
        if (!uint.TryParse(setIdText, out var setId)) return Fail("Record ID must be an unsigned integer.");
        var setValues = ParseSetOptions(setOptions); var setSample = WdbcFile.Load(setInputPath); var setTable = Path.GetFileNameWithoutExtension(setInputPath);
        var setResolution = ResolveClientTableSchema(setSample, setSchemaPath, setTable);
        if (setResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(setTable, setSample.FieldCount, setResolution));
        DbcRowMutationService.SetRow(setInputPath, setOutputPath, setResolution.Columns, setResolution.KeyStrategy, setId, setValues);
        Console.Error.WriteLine($"Updated {setTable} ID {setId} in an output copy with {setValues.Count:N0} field value(s): {Path.GetFullPath(setOutputPath)}");
        return 0;
    }
    if (args.Length >= 3 && args[0] == "validate")
    {
        var options = args[3..];
        var unknown = options.Where(option => !option.Equals("--strict", StringComparison.OrdinalIgnoreCase) && !option.Equals("--recursive", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown validate option: {unknown[0]}. Supported: --strict, --recursive");
        var strict = options.Any(option => option.Equals("--strict", StringComparison.OrdinalIgnoreCase));
        var recursive = options.Any(option => option.Equals("--recursive", StringComparison.OrdinalIgnoreCase));
        var results = DbcCorpusValidator.Validate(args[1], args[2], recursive: recursive);
        foreach (var result in results) Console.WriteLine($"{(result.Skipped ? "SKIP" : !result.Passed ? "FAIL" : result.Warning ? "WARN" : "PASS")}\t{result.Rows}\t{result.Fields}\t{result.Path}\t{result.Message}");
        Console.Error.WriteLine($"Validated {results.Count:N0} DBC paths: {results.Count(result => result.Passed && !result.Skipped && !result.Warning):N0} named-schema passes, {results.Count(result => result.Warning):N0} fallbacks, {results.Count(result => result.Skipped):N0} skipped, {results.Count(result => !result.Passed):N0} failed.");
        return results.All(result => result.Passed) && (!strict || results.All(result => !result.Warning)) ? 0 : 1;
    }
    if (args.Length != 2 || args[0] != "info") return DbcHelp(2);
    var file = WdbcFile.Load(args[1]);
    Console.WriteLine($"Path\t{Path.GetFullPath(args[1])}");
    Console.WriteLine($"Container\t{file.ContainerKind.ToString().ToUpperInvariant()}"); Console.WriteLine($"Rows\t{file.RowCount}"); Console.WriteLine($"Fields\t{file.FieldCount}"); Console.WriteLine($"RecordBytes\t{file.RecordSize}"); Console.WriteLine($"StringBytes\t{file.StringTableSize}");
    if (file.Db2Metadata is { } db2) Console.WriteLine($"Build\t{db2.Build}\nTableHash\t0x{db2.TableHash:X8}\nTimestamp\t{db2.Timestamp}\nIdRange\t{db2.MinId}..{db2.MaxId}\nLocale\t0x{db2.Locale:X8}\nIndexEntries\t{db2.IndexMap.Count}\nCopyRows\t{db2.CopyRows}\nStructuralMutation\t{file.AllowsStructuralMutation}");
    return 0;
}

static int Casc(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return CascHelp();
    var operation = args[0].ToLowerInvariant();
    if (args.Length < 2) return CascHelp(2);
    var service = new CascArchiveService();
    switch (operation)
    {
        case "list":
            {
                var options = args[2..]; var query = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty; var json = options.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var localOnly = options.Contains("--local-only", StringComparer.OrdinalIgnoreCase); var listFile = Option(options, "--listfile=");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--local-only", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown CASC list option: {unknown[0]}");
                var all = service.ListFiles(args[1], "*", listFile, cancellationToken); var files = all.Where(file => (!localOnly || file.IsAvailableLocally) && MpqPathFilter.Matches(file.ArchivePath, query)).ToArray();
                if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(files, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
                else foreach (var file in files) Console.WriteLine($"{file.Size}\t{(file.IsAvailableLocally ? "LOCAL" : "REMOTE")}\t{file.FileDataId}\t{file.Locale:X8}\t{file.NameType}\t{file.ArchivePath}");
                PrintCascSummary(all); return 0;
            }
        case "tree":
            {
                var options = args[2..]; var folder = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty; var json = options.Contains("--format=json", StringComparer.OrdinalIgnoreCase); var localOnly = options.Contains("--local-only", StringComparer.OrdinalIgnoreCase); var listFile = Option(options, "--listfile=");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.Equals("--local-only", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown CASC tree option: {unknown[0]}");
                var all = service.ListFiles(args[1], "*", listFile, cancellationToken); var source = all.Where(file => !localOnly || file.IsAvailableLocally).GroupBy(file => file.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(file => file.IsAvailableLocally).ThenBy(file => file.NameType).First()).Select(file => new MpqFileEntry(file.ArchivePath, file.Size, file.Size, file.ContentFlags, file.Locale)).ToArray(); var page = MpqArchiveBrowser.Browse(source, folder);
                if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(page, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var node in page.Nodes) Console.WriteLine($"{(node.IsFolder ? "DIR" : "FILE")}\t{node.FileCount}\t{node.Size}\t{node.ArchivePath}");
                Console.Error.WriteLine($"{(page.CurrentFolder.Length == 0 ? "CASC root" : page.CurrentFolder)}: {page.Nodes.Count:N0} direct node(s), {page.RecursiveFiles:N0} recursive file(s)."); PrintCascSummary(all); return 0;
            }
        case "extract" when args.Length >= 3:
            {
                var options = args[3..]; var query = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty; var listFile = Option(options, "--listfile="); var quiet = options.Contains("--quiet", StringComparer.OrdinalIgnoreCase); var progressText = Option(options, "--progress="); var progressStep = progressText is null ? 5 : int.Parse(progressText); if (progressStep is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(progressStep), "Progress percentage must be from 1 to 100.");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown CASC extract option: {unknown[0]}");
                var all = service.ListFiles(args[1], "*", listFile, cancellationToken); var matched = all.Where(file => MpqPathFilter.Matches(file.ArchivePath, query)).ToArray(); var files = matched.Where(file => file.IsAvailableLocally).GroupBy(file => file.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderBy(file => file.NameType).First()).ToArray(); if (files.Length == 0) return Fail("No matching CASC files are stored locally; Crucible will not download CDN data implicitly.");
                var timer = Stopwatch.StartNew(); service.Extract(args[1], args[2], files, quiet ? null : new ConsoleProgress(progressStep), cancellationToken); Console.Error.WriteLine($"Extracted {files.Length:N0} CASC file(s) to {Path.GetFullPath(args[2])} in {timer.Elapsed.TotalSeconds:0.##}s.{(matched.Length == files.Length ? string.Empty : $" Skipped {matched.Length - files.Length:N0} unavailable or duplicate locale row(s).")}"); return 0;
            }
        case "extract-folder" when args.Length >= 4:
            {
                var options = args[4..]; var listFile = Option(options, "--listfile="); var quiet = options.Contains("--quiet", StringComparer.OrdinalIgnoreCase); var progressText = Option(options, "--progress="); var progressStep = progressText is null ? 5 : int.Parse(progressText); if (progressStep is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(progressStep), "Progress percentage must be from 1 to 100.");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown CASC extract-folder option: {unknown[0]}");
                var all = service.ListFiles(args[1], "*", listFile, cancellationToken); var mapped = all.Where(file => file.IsAvailableLocally).GroupBy(file => file.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderBy(file => file.NameType).First()).ToArray(); var tree = mapped.Select(file => new MpqFileEntry(file.ArchivePath, file.Size, file.Size, file.ContentFlags, file.Locale)).ToArray(); var selectedPaths = MpqArchiveBrowser.SelectFolder(tree, args[2]).Select(file => file.ArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase); var files = mapped.Where(file => selectedPaths.Contains(file.ArchivePath)).ToArray(); if (files.Length == 0) return Fail($"CASC folder not found, empty, or not stored locally: {args[2]}");
                service.Extract(args[1], args[3], files, quiet ? null : new ConsoleProgress(progressStep), cancellationToken); Console.Error.WriteLine($"Extracted {files.Length:N0} recursive CASC file(s) from {args[2]} to {Path.GetFullPath(args[3])}."); return 0;
            }
        default: return CascHelp(2);
    }
}

static int Mpq(string[] args)
{
    if (args.Length == 0) return MpqHelp(2);
    if (args[0] is "help" or "--help" or "-h" || args.Length > 1 && args[1] is "--help" or "-h") return MpqHelp();
    var service = new PatchArchiveService();
    switch (args[0])
    {
        case "list" when args.Length >= 2:
            {
                var options = args[2..]; var query = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
                var contentOnly = options.Any(option => option.Equals("--content-only", StringComparison.OrdinalIgnoreCase));
                var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase));
                var listFile = Option(options, "--listfile=");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--content-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0) return Fail($"Unknown list option: {unknown[0]}");
                var allFiles = LoadMpqIndex(service, args[1], listFile);
                var files = allFiles.Where(file => (!contentOnly || !file.IsMetadata) && MpqPathFilter.Matches(file.ArchivePath, query)).ToArray();
                if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(files, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                else foreach (var file in files) Console.WriteLine($"{file.Size}\t{file.CompressedSize}\t{file.ArchivePath}");
                PrintAnonymousMpqWarning(allFiles, listFile);
                return 0;
            }
        case "tree" when args.Length >= 2:
            {
                var options = args[2..]; var folder = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty; var json = options.Any(option => option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)); var listFile = Option(options, "--listfile=");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=text", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown tree option: {unknown[0]}");
                var allFiles = LoadMpqIndex(service, args[1], listFile); var page = MpqArchiveBrowser.Browse(allFiles, folder);
                if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(page, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); else foreach (var node in page.Nodes) Console.WriteLine($"{(node.IsFolder ? "DIR" : "FILE")}\t{node.Kind}\t{node.FileCount}\t{node.Size}\t{node.CompressedSize}\t{node.ArchivePath}");
                Console.Error.WriteLine($"{(page.CurrentFolder.Length == 0 ? "MPQ root" : page.CurrentFolder)}: {page.Nodes.Count:N0} direct node(s), {page.RecursiveFiles:N0} recursive file(s), {page.AnonymousFiles:N0} anonymous name(s)."); PrintAnonymousMpqWarning(allFiles, listFile); return 0;
            }
        case "extract-folder" when args.Length >= 4:
            {
                var options = args[4..]; var quiet = options.Any(option => option.Equals("--quiet", StringComparison.OrdinalIgnoreCase)); var listFile = Option(options, "--listfile="); var progressOption = options.FirstOrDefault(option => option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase)); var progressStep = progressOption is null ? 5 : int.Parse(progressOption[11..]); var workerText = Option(options, "--workers="); var workers = workerText is null ? 0 : int.Parse(workerText);
                if (progressStep is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(progressStep), "Progress percentage must be from 1 to 100."); if (workerText is not null && workers is (< 1 or > PatchArchiveService.MaximumExtractionWorkers)) throw new ArgumentOutOfRangeException(nameof(workers), $"Extraction workers must be from 1 to {PatchArchiveService.MaximumExtractionWorkers}."); var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray(); if (unknown.Length > 0) return Fail($"Unknown extract-folder option: {unknown[0]}");
                var allFiles = LoadMpqIndex(service, args[1], listFile); var files = MpqArchiveBrowser.SelectFolder(allFiles, args[2]); if (files.Count == 0) return Fail($"MPQ folder not found or empty: {args[2]}"); PrintAnonymousMpqWarning(allFiles, listFile); var timer = Stopwatch.StartNew(); service.Extract(args[1], args[3], files, quiet ? null : new ConsoleProgress(progressStep), workers: workers); Console.Error.WriteLine($"Extracted {files.Count:N0} recursive file(s) from {args[2]} to {Path.GetFullPath(args[3])} in {timer.Elapsed.TotalSeconds:0.##}s using {(workers == 0 ? $"auto (up to {PatchArchiveService.RecommendedExtractionWorkers})" : workers.ToString())} worker(s)."); return 0;
            }
        case "extract" when args.Length >= 3:
            {
                var options = args[3..];
                var query = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
                var quiet = options.Any(option => option.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
                var listFile = Option(options, "--listfile=");
                var progressOption = options.FirstOrDefault(option => option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase));
                var progressStep = progressOption is null ? 5 : int.Parse(progressOption[11..]);
                if (progressStep is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(progressStep), "Progress percentage must be from 1 to 100.");
                var workerText = Option(options, "--workers="); var workers = workerText is null ? 0 : int.Parse(workerText);
                if (workerText is not null && workers is (< 1 or > PatchArchiveService.MaximumExtractionWorkers)) throw new ArgumentOutOfRangeException(nameof(workers), $"Extraction workers must be from 1 to {PatchArchiveService.MaximumExtractionWorkers}.");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--workers=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0) return Fail($"Unknown extract option: {unknown[0]}");
                var allFiles = LoadMpqIndex(service, args[1], listFile);
                var files = allFiles.Where(file => MpqPathFilter.Matches(file.ArchivePath, query)).ToArray();
                PrintAnonymousMpqWarning(allFiles, listFile);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                service.Extract(args[1], args[2], files, quiet ? null : new ConsoleProgress(progressStep), workers: workers);
                Console.Error.WriteLine($"Extracted {files.Length:N0} file(s) to {Path.GetFullPath(args[2])} in {timer.Elapsed.TotalSeconds:0.##}s using {(workers == 0 ? $"auto (up to {PatchArchiveService.RecommendedExtractionWorkers})" : workers.ToString())} worker(s).");
                return 0;
            }
        case "merge" when args.Length >= 4:
            {
                var options = args[2..]; var inputArchives = options.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray();
                var listFile = Option(options, "--listfile="); var conflictText = Option(options, "--conflicts=") ?? "block";
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.StartsWith("--listfile=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--conflicts=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0) return Fail($"Unknown merge option: {unknown[0]}");
                var policy = conflictText.ToLowerInvariant() switch { "block" => MpqMergeConflictPolicy.BlockDifferentEntries, "earlier" => MpqMergeConflictPolicy.PreferEarlierArchive, "later" => MpqMergeConflictPolicy.PreferLaterArchive, _ => throw new ArgumentException("--conflicts must be block, earlier, or later.") };
                var result = new MpqMergeService().Merge(inputArchives, args[1], policy, listFile, new Progress<(int Done, int Total, string Path)>(value => Console.Error.WriteLine($"Merge\t{value.Done:N0}/{value.Total:N0}\t{value.Path}")));
                foreach (var conflict in result.Conflicts) Console.WriteLine($"CONFLICT\t{conflict.ArchivePath}\t{string.Join('|', conflict.Sources)}\t{string.Join('|', conflict.Sha256)}");
                if (result.OutputFiles == 0 && result.Conflicts.Count > 0) { Console.Error.WriteLine($"Merge blocked by {result.Conflicts.Count:N0} different-byte internal path conflict(s); source archives and output were not modified."); return 3; }
                Console.Error.WriteLine($"Merged {result.InputArchives:N0} source patches into {result.OutputPath}: {result.OutputFiles:N0} files, {result.ExactDuplicates:N0} exact duplicate(s), {result.Conflicts.Count:N0} explicitly resolved conflict(s) using {result.Policy}."); return 0;
            }
        case "create" when args.Length >= 3:
            var createEntries = PatchInputMapper.Map(args[2..]); PrintCompatibility(createEntries, null); service.Create(args[1], createEntries); return 0;
        case "update" when args.Length >= 3:
            var updateEntries = PatchInputMapper.Map(args[2..]); PrintCompatibility(updateEntries, null); service.Update(args[1], updateEntries); return 0;
        default:
            return MpqHelp(2);
    }
}

static int ManifestHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible manifest create <manifest.json> <output.mpq> <files/folders...> [--allow=glob] [--deny=glob] [--require=glob] [--count=N] [--client-exe=Wow.exe]\n  wowcrucible manifest list <manifest.json>\n  wowcrucible manifest validate <manifest.json> [archive.mpq]\n  wowcrucible manifest build <manifest.json> <output-folder>", code);
static int DbcHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible dbc info <file.dbc|file.db2>\n  wowcrucible dbc dbd-info <file.dbd> <build> [--format=text|json]\n  wowcrucible dbc schema-audit <definitions-root|-> <table-folder> <build> [--xml=schema.xml] [--roundtrip] [--only-problems] [--format=text|json]\n  wowcrucible dbc lighting <dbc-folder> [--map=N] [--light=N] [--slot=1..8] [--time=0..2880] [--format=text|json]\n  wowcrucible dbc lighting-band-set <LightIntBand.dbc|LightFloatBand.dbc> <band-id> <output.dbc> --key=time:value [...] [--plan=file.json] [--overwrite] [--in-place] [--format=text|json]\n  wowcrucible dbc rows <file.dbc|file.db2> <schema.xml|file.dbd|definitions-folder> <id>...\n  wowcrucible dbc export <file.dbc|file.db2> <schema> <output.csv|json|jsonl> [--format=csv|json|jsonl] [--columns=A,B|--column=Name] [--ids=1,2|--id=N] [--raw-string-offsets] [--overwrite]\n  wowcrucible dbc import <file.dbc|file.db2> <schema> <input.csv|json|jsonl> [--format=csv|json|jsonl] [--append] [--raw-string-offsets] [--output=changed.dbc|db2] [--overwrite] [--report=text|json]\n  wowcrucible dbc stage-create <file.dbc> <schema> <project> [--replace]\n  wowcrucible dbc stage-info <workspace.sqlite> [--format=text|json]\n  wowcrucible dbc stage-query <workspace.sqlite> <select.sql> [--bind=name=value] [--limit=500] [--format=text|json]\n  wowcrucible dbc stage-mutate <workspace.sqlite> <update-or-insert.sql> [--bind=name=value] [--apply] [--format=text|json]\n  wowcrucible dbc stage-diff <workspace.sqlite> [--format=text|json]\n  wowcrucible dbc stage-apply <workspace.sqlite> <source.dbc> <schema> <output.dbc> [--apply] [--overwrite] [--format=text|json]\n  wowcrucible dbc find <file.dbc|file.db2> <schema> <column> <value>... [--count|--limit=N]\n  wowcrucible dbc validate <schema.xml> <dbc-folder> [--strict] [--recursive]\n  wowcrucible dbc compare <base> <override> <schema> [--summary]\n  wowcrucible dbc promote apply <base> <override> <schema> <manifest.json> <output>\n  wowcrucible dbc promote additions <base> <override> <schema> <manifest.json> <output>\n  wowcrucible dbc clone-remap where <base> <source> <schema> <column> <value>... --manifest=map.json --output=merged.dbc|db2 [--start-id=N]\n  wowcrucible dbc clone-dependency <parent-source> <parent-merged> <parent-schema> <parent-map.json> <foreign-column> <child-base> <child-source> <child-schema> --child-map=map.json --child-output=child --parent-output=parent\n  wowcrucible dbc copy-row <base> <source> <schema> <source-id> <target-id> <output> [--set=Column=Value]...\n  wowcrucible dbc set-row <input> <schema> <id> <output> --set=Column=Value [...]\n  wowcrucible dbc spell-tooltip <Spell.dbc> <spell-id>... [--format=text|json]\n  wowcrucible dbc item-display <ItemDisplayInfo.dbc> <schema.xml|-> <display-id> [--assets=processed-library]\n  wowcrucible dbc item-equipped <ItemDisplayInfo.dbc> <schema.xml|-> <display-id> <base-skin> <output.png> --inventory=N --assets=processed-library [--source=name]\n  wowcrucible dbc itemset inspect <ItemSet.dbc> <schema.xml> <set-id> [--spell=Spell.dbc]\n  wowcrucible dbc itemset clone <ItemSet.dbc> <schema.xml> <output.dbc> <source-set> <new-set> --map=old:new,... [--suffix=\" Variant\"]\n  wowcrucible dbc itemset effects <ItemSet.dbc> <schema.xml> <output.dbc> <set-id> --effect=required-items:spell-id [...]\n\nSchema audit accepts an exact build-specific WDBX XML layout, a matching WoWDBDefs DBD layout, or both; pass - when no DBD corpus is selected. One exact provider is sufficient and the other remains visible coverage evidence. --roundtrip writes every full WDBC/WDB2 to isolated temporary storage and requires byte-identical SHA-256 output. `dbc lighting` validates the exact five-table build-12340 Light graph and samples its 18 color plus 6 float time bands without changing any DBC. `lighting-band-set` creates a complete hash/preimage-bound key edit, writes only the explicit output, and requires `--in-place` before replacing its loaded source; every replacement keeps `.bak` and a receipt. PTCH update-layer deltas are identified explicitly and require effective-chain reconstruction before table editing. Staging workspaces are project-local SQLite files with immutable baselines, named schema columns, dry-run mutations, and source/schema hash binding. DBC remains authoritative: publication always passes through Crucible's stale-safe structured importer and writes only an explicit output. For WDB2, <schema> may be the matching XML, .dbd file, or WoWDBDefs definitions folder. WDB5/WDB6/WDC are not yet supported.", code);
static int MpqHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible mpq list <archive.mpq> [filter] [--content-only] [--format=json] [--listfile=paths.txt]\n  wowcrucible mpq tree <archive.mpq> [folder] [--format=text|json] [--listfile=paths.txt]\n  wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N] [--workers=N] [--listfile=paths.txt]\n  wowcrucible mpq extract-folder <archive.mpq> <internal-folder> <destination> [--quiet|--progress=N] [--workers=N] [--listfile=paths.txt]\n  wowcrucible mpq create <archive.mpq> <files/folders...>\n  wowcrucible mpq update <archive.mpq> <files/folders...>\n  wowcrucible mpq merge <output.mpq> <source-a.mpq> <source-b.mpq> [...] [--conflicts=block|earlier|later] [--listfile=paths.txt]", code);
static int CascHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible casc list <storage-folder> [filter] [--local-only] [--format=text|json] [--listfile=paths.txt]\n  wowcrucible casc tree <storage-folder> [folder] [--local-only] [--format=text|json] [--listfile=paths.txt]\n  wowcrucible casc extract <storage-folder> <destination> [filter] [--quiet|--progress=N] [--listfile=paths.txt]\n  wowcrucible casc extract-folder <storage-folder> <internal-folder> <destination> [--quiet|--progress=N] [--listfile=paths.txt]\n\nCASC operations are read-only and local-only. Crucible never mutates the storage and never downloads missing CDN payloads implicitly.", code);
static int ToolingHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible tools commands [search words...] [--format=text|json]\n  wowcrucible tools inventory [workspace-root] [--format=text|json] [--unassigned-only] [--no-missing]\n\nThe command catalog is shared with the desktop Ctrl+K palette, so scripts and the UI use the same searchable vocabulary. A command search with no matches returns exit code 3.\n\nWithout an inventory path, Crucible searches upward from the executable for the shared wow-edits workspace. Any new unassigned directory returns exit code 3 so automation cannot silently claim complete tool coverage.", code);
static int CacheHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible cache info <file.wdb|file.adb> [--definitions=definitions.xml] [--definition=name] [--format=text|json]\n  wowcrucible cache rows <file.wdb|file.adb> [--definitions=definitions.xml] [--definition=name] [--search=text] [--limit=100] [--format=text|json]\n  wowcrucible cache export <file.wdb|file.adb> <output.csv|jsonl> [--definitions=definitions.xml] [--definition=name] [--format=csv|jsonl] [--overwrite]\n  wowcrucible cache server-plan <file.wdb> <host> <port> <user> <database> [--definitions=WDB.xml] [--ids=1,2] [--output=plan.json] [--sql=preview.sql] [--overwrite]\n  wowcrucible cache server-apply <plan.json> <host> <port> <user> <database> <receipt.json> [--apply] [--overwrite]\n  wowcrucible cache server-rollback <receipt.json> <host> <port> <user> <database> [--apply]\n\nWDB and Cataclysm WCH2 ADB reads are bounded and read-only. Version-aware headers and record framing are always inspected; when no matching schema is available, Crucible reports raw record metadata instead of guessing field types. Selected WDBX or Adb_Wdb_Parser schema XML is parsed as data by Crucible's own provider. Later WCH5/WCH7/WCH8 ADB is rejected rather than guessed because it requires matching DB2 layout metadata. Export is atomic and never overwrites without --overwrite. server-plan binds selected decoded WDB rows to exact live modern-core preimages and never invents missing rows or obsolete ArcEmu targets. Apply and rollback are dry-run unless --apply is explicit; apply rechecks source/schema/preimages under row locks and writes a receipt before commit, while rollback refuses later-edited fields. Database passwords come from WOW_CRUCIBLE_DB_PASSWORD by default.", code);
static int KnowledgeHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible knowledge search <terms...> [--root=wiki-folder] [--locale=en] [--limit=100] [--format=text|json]\n  wowcrucible knowledge show <relative-markdown-path> [--root=wiki-folder] [--section=N]\n\nSearch builds a local in-memory index over Markdown only; it never executes the wiki site generator, scripts, HTML, or remote links. Without --root, Crucible searches upward from the executable for the shared wiki folder. The desktop exposes the same provider under Offline knowledge & field reference, and F1 opens it using the selected DBC table and field as context.", code);
static void PrintAnonymousMpqWarning(IReadOnlyList<MpqFileEntry> files, string? listFile)
{
    var anonymous = files.Count(file => ClientArchiveIndexService.IsAnonymous(file.ArchivePath));
    if (anonymous == 0) return;
    Console.Error.WriteLine($"WARNING: Archive opened successfully, but {anonymous:N0} file name(s) are unresolved StormLib placeholders.{(string.IsNullOrWhiteSpace(listFile) ? " Supply --listfile=paths.txt to recover known paths." : " The supplied listfile did not resolve every path.")}");
}
static void PrintCascSummary(IReadOnlyList<CascFileEntry> files)
{
    Console.Error.WriteLine($"CASC index: {files.Count:N0} row(s) · {files.Count(file => file.IsAvailableLocally):N0} stored locally · {files.Count(file => file.NameType != CascEntryNameType.FullPath):N0} synthetic FileDataId/key name(s). Storage was opened read-only.");
}
static IReadOnlyList<MpqFileEntry> LoadMpqIndex(PatchArchiveService service, string archive, string? listFile)
{
    var result = MpqArchiveIndexCache.LoadOrCreate(archive, listFile, () => service.ListFiles(archive, "*", listFile)); Console.Error.WriteLine($"MPQ index: {(result.Cached ? "cache hit" : "read archive and cached")} · {result.Entries.Count:N0} entries."); return result.Entries;
}
static int GroupHelp(string message, int code)
{
    if (message.Contains("wowcrucible dbc lighting <dbc-folder>", StringComparison.Ordinal))
        message = message.Replace("  wowcrucible dbc lighting <dbc-folder> [--map=N] [--light=N] [--slot=1..8] [--time=0..2880] [--format=text|json]\n", "  wowcrucible dbc lighting <dbc-folder> [--map=N] [--light=N] [--slot=1..8] [--time=0..2880] [--format=text|json]\n  wowcrucible dbc lighting-scene <dbc-folder> <light-id> [--slot=1..8] [--time=0..2880] [--format=text|json]\n", StringComparison.Ordinal);
    if (message.Contains("wowcrucible asset texture-info", StringComparison.Ordinal))
        message = message.Replace("Usage:\n", "Usage:\n  wowcrucible asset map-info <file.adt|wdt|wdl> [--cells] [--format=text|json]\n  wowcrucible asset adt-height-plan <input.adt> <delta> <x:y,x:y|all> <plan.json> [--overwrite]\n  wowcrucible asset adt-height-apply <plan.json> <output.adt> [--overwrite]\n  wowcrucible asset adt-brush-plan <input.adt> <center-x:center-y> <radius> <strength> <plan.json> [--mode=raise-lower|flatten|smooth|noise] [--target-height=N] [--seed=N] [--falloff=linear|smooth|constant] [--overwrite]\n  wowcrucible asset adt-brush-apply <plan.json> <output.adt> [--overwrite]\n  wowcrucible asset adt-texture-info <input.adt> [--cells] [--format=text|json]\n  wowcrucible asset adt-texture-plan <input.adt> <layer-slot> <texture-id> <x:y,x:y|all> <plan.json> [--overwrite]\n  wowcrucible asset adt-texture-apply <plan.json> <output.adt> [--overwrite]\n  wowcrucible asset adt-texture-add-plan <input.adt> <client-texture.blp> <x:y,x:y> <plan.json> [--encoding=auto|packed-4-bit|big-8-bit|rle-8-bit] [--initial-alpha=0] [--overwrite]\n  wowcrucible asset adt-texture-add-apply <plan.json> <output.adt> [--overwrite]\n  wowcrucible asset adt-alpha-info <input.adt> [--cells] [--format=text|json]\n  wowcrucible asset adt-alpha-plan <input.adt> <layer-slot> <center-x:center-y> <radius> <target-alpha> <opacity> <x:y,x:y|all> <plan.json> [--falloff=linear|smooth|constant] [--overwrite]\n  wowcrucible asset adt-alpha-apply <plan.json> <output.adt> [--overwrite]\n", StringComparison.Ordinal);
    message = message.Replace("map-info <file.adt|wdt|wdl> [--cells] [--format=text|json]", "map-info <file.adt|wdt|wdl> [--cells] [--placements] [--format=text|json]", StringComparison.Ordinal);
    if (message.Contains("wowcrucible db query", StringComparison.Ordinal))
        message += "\n\nDatabase objects:\n  wowcrucible db objects <host> <port> <user> <database> [--type=view|trigger|procedure|function|event] [--format=text|json]\n  wowcrucible db object-show <host> <port> <user> <database> <type> <name> [--format=text|json]\n  wowcrucible db object-export <host> <port> <user> <database> <output.sql> [--overwrite]\n  wowcrucible db object-drop <host> <port> <user> <database> <type> <name> [--apply]\n  wowcrucible db view-set <host> <port> <user> <database> <name> <select.sql> [--apply]\n  wowcrucible db event-state <host> <port> <user> <database> <name> <enable|disable> [--apply]\n\nTarget-bound synchronization:\n  wowcrucible db sync-bridge <host> <port> <user> <database> <verified-audit> <bridge.json> [--include=glob]... [--maximum=N] [--overwrite]\n  wowcrucible db sync-bridge-inspect <bridge.json>\n  wowcrucible db sync-plan <host> <port> <user> <database> <verified-audit> <plan.json> [--include=glob]... [--translation=bridge.json] [--dependency-closure] [--include-removals] [--auto-remap] [--remap-start=ID] [--maximum=N] [--overwrite]\n  wowcrucible db sync-inspect <plan.json> [--sql=preview.sql] [--overwrite]\n  wowcrucible db sync-apply <host> <port> <user> <database> <plan.json> <receipt.json> [--apply] [--overwrite]\n  wowcrucible db sync-rollback <host> <port> <user> <database> <receipt.json> [--apply]\n\nObject mutations and synchronization apply/rollback are dry-run unless --apply is explicit. Guided views accept exactly one independently validated SELECT; synchronization requires a verified baseline comparison, exact primary keys, target preimage matches, and a rollback receipt. `sync-bridge` generates an editable profile bound to the audit and live target schema; blank mappings block rather than guess. `sync-bridge-inspect` verifies its offline structure and prints every decision. `--translation` supports explicit primary-row table/column renames, reviewed non-key drops, typed target defaults, and reviewed structural row expansions. Each expansion binds the complete target key and every required preimage/postimage value to a named source value or typed constant; incomplete or colliding outputs block. Its profile bytes, evidence, and target schema hash are bound into the plan. `--dependency-closure` then follows exact translated target relationships and keeps all outputs from a selected source row together. Automatic collision remapping is opt-in and every mapping/rewrite is printed for review.\n\nRead-only query batches: add --batch to execute up to 32 semicolon-delimited SELECT/SHOW/DESCRIBE/EXPLAIN statements from one SQL file. Use --batch-format=text|json for independently shaped, labeled result sets. Batches reject --write and single-result --output switches; SELECT file output is always blocked.";
    if (code == 0) Console.WriteLine(message); else Console.Error.WriteLine(message); return code;
}

static int Help()
{
    Console.WriteLine("WoW Crucible CLI\n\nGlobal options:\n  --devbug   mirror terminal output and diagnostics to Logs\\Debug (newest 3 CLI sessions retained)\n\nCommand groups (run wowcrucible <group> --help for full syntax):\n  asset     inspect/preview models and build resumable extracted/PNG asset libraries\n  project   create portable content projects and reserve collision-checked IDs\n  tools     search native commands and inventory the local legacy-tool corpus\n  knowledge search the local wiki for fields, flags, commands, and systems\n  cache     inspect and export WDB/WCH2 ADB client cache tables read-only\n  client    install patches, clear cache, index/extract clients, and plan fusion\n  server    detect installed cores, audit DBC/SQL bindings, and stage client changes\n  db        inspect schemas, recover legacy SQL changes offline, audit items, and clone complete items\n  dbc       inspect/edit/validate/compare/promote DBCs and author item sets\n  mpq       list, extract, create, merge, and safely update small patch archives\n  casc      browse and extract later-client CASC storage read-only\n  manifest  define, verify, and build tiny reviewable patch MPQs\n\nExamples:\n  wowcrucible --devbug mpq list patch-H.MPQ\n  wowcrucible cache info creaturecache.wdb --definitions=WDB.xml\n  wowcrucible cache info Item-sparse.adb --definitions=adb-definitions.xml\n  wowcrucible casc list \"D:\\World of Warcraft\" \"**\\*.m2\" --local-only\n  wowcrucible tools commands \"cut items\"\n  wowcrucible knowledge search item_template flags\n  wowcrucible tools inventory --unassigned-only\n  wowcrucible project --help\n  wowcrucible db --help\n  wowcrucible dbc --help\n  wowcrucible asset --help\n\nThe full copy-paste guide ships as docs\\CLI-REFERENCE.md beside the application.");
    return 0;
}

static int Fail(string message) { Console.Error.WriteLine(message); return 2; }
static void WriteTextAtomic(string path, string content)
{
    path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp";
    try { File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false)); File.Move(temporary, path, true); }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}

static string? Option(IEnumerable<string> options, string prefix) => options.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
static double? OptionalFinite(IEnumerable<string> options, string prefix)
{
    var text = Option(options, prefix); if (text is null) return null;
    if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0) throw new ArgumentException($"{prefix.TrimEnd('=')} must be a finite non-negative number.");
    return value;
}
static string? NormalizeProofPng(string? path, string option)
{
    if (path is null) return null; path = Path.GetFullPath(path);
    if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{option} output must end in .png.");
    return path;
}
static void WriteTextureProof(TextureEncodingProof proof, string source, double amplification, string? differenceOutput, string? previewOutput)
{
    var report = proof.Comparison; var changedPercent = report.PixelCount == 0 ? 0 : report.ChangedPixels * 100d / report.PixelCount;
    Console.WriteLine($"Source\t{source}\nDimensions\t{report.Width}x{report.Height}\nRequestedCodec\t{proof.RequestedFormat}\nActualEncoding\t{proof.ActualEncoding}\nQuality\t{proof.Quality}\nMipmaps\t{proof.MipLevels} (generated={proof.GeneratedMipmaps})\nEncodedBytes\t{proof.EncodedBytes:N0}\nChangedPixels\t{report.ChangedPixels:N0}/{report.PixelCount:N0} ({changedPercent:0.####}%)\nExactPixels\t{report.ExactPixels:N0}");
    WriteChannel("RGB", report.RgbCombined); WriteChannel("R", report.Red); WriteChannel("G", report.Green); WriteChannel("B", report.Blue); WriteChannel("A", report.Alpha); WriteChannel("RGBA", report.RgbaCombined);
    Console.WriteLine($"AlphaTransparentBoundaryChanges\t{report.TransparentBoundaryChanges:N0}\nAlphaOpaqueBoundaryChanges\t{report.OpaqueBoundaryChanges:N0}\nAlphaThresholdCrossings128\t{report.AlphaThresholdCrossings:N0}\nBinaryAlphaBecameTranslucent\t{report.BinaryAlphaBecameTranslucent:N0}\nDifferenceAmplification\t{amplification:0.###}x");
    if (differenceOutput is not null) Console.WriteLine($"DifferenceMap\t{Path.GetFullPath(differenceOutput)}"); if (previewOutput is not null) Console.WriteLine($"DecodedPreview\t{Path.GetFullPath(previewOutput)}");
    static void WriteChannel(string name, TextureChannelError value) => Console.WriteLine($"{name}\tchanged={value.ChangedSamples:N0}\tMAE={value.MeanAbsoluteError:0.######}\tRMSE={value.RootMeanSquareError:0.######}\tmax={value.MaximumAbsoluteError}\tPSNR={(value.PeakSignalToNoiseDb is { } psnr ? $"{psnr:0.###} dB" : "exact")}");
}
static TextureLayerArgument ParseTextureLayerArgument(string text)
{
    var parts = text.Split('|'); if (parts.Length is < 1 or > 6 || string.IsNullOrWhiteSpace(parts[0])) throw new ArgumentException("Each --layer must be path|blend|opacity|x|y|visible; trailing values may be omitted.");
    var blend = parts.Length > 1 && parts[1].Length > 0 ? ParseTextureBlendMode(parts[1]) : TextureBlendMode.Normal;
    var opacity = parts.Length > 2 && parts[2].Length > 0 ? double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedOpacity) && double.IsFinite(parsedOpacity) && parsedOpacity is >= 0 and <= 1 ? parsedOpacity : throw new ArgumentException("Layer opacity must be from 0 through 1.") : 1;
    var x = parts.Length > 3 && parts[3].Length > 0 ? int.TryParse(parts[3], out var parsedX) ? parsedX : throw new ArgumentException("Layer X offset must be a whole pixel value.") : 0;
    var y = parts.Length > 4 && parts[4].Length > 0 ? int.TryParse(parts[4], out var parsedY) ? parsedY : throw new ArgumentException("Layer Y offset must be a whole pixel value.") : 0;
    var visible = parts.Length <= 5 || parts[5].Length == 0 || parts[5].Equals("visible", StringComparison.OrdinalIgnoreCase) || parts[5].Equals("true", StringComparison.OrdinalIgnoreCase) || parts[5] == "1" ? true : parts[5].Equals("hidden", StringComparison.OrdinalIgnoreCase) || parts[5].Equals("false", StringComparison.OrdinalIgnoreCase) || parts[5] == "0" ? false : throw new ArgumentException("Layer visibility must be visible/hidden, true/false, or 1/0.");
    return new(parts[0], blend, opacity, x, y, visible);
}
static TextureBlendMode ParseTextureBlendMode(string text) => text.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
{
    "normal" => TextureBlendMode.Normal,
    "multiply" => TextureBlendMode.Multiply,
    "screen" => TextureBlendMode.Screen,
    "overlay" => TextureBlendMode.Overlay,
    "add" or "additive" => TextureBlendMode.Add,
    "subtract" or "subtractive" => TextureBlendMode.Subtract,
    "darken" => TextureBlendMode.Darken,
    "lighten" => TextureBlendMode.Lighten,
    _ => throw new ArgumentException("Layer blend must be normal, multiply, screen, overlay, add, subtract, darken, or lighten.")
};
static double[] ParseTextureVector(string text, string option)
{
    var parts = text.Split(':'); if (parts.Length != 4) throw new ArgumentException($"{option} must contain four colon-separated finite RGBA numbers."); var values = new double[4];
    for (var index = 0; index < values.Length; index++) if (!double.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[index]) || !double.IsFinite(values[index])) throw new ArgumentException($"{option} must contain four colon-separated finite RGBA numbers.");
    return values;
}
static RgbaTexture DecodeTextureInput(string path, int mipLevel = 0) => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase) ? BlpTextureService.Decode(path, mipLevel) : mipLevel == 0 ? BlpTextureService.DecodeImage(path) : throw new ArgumentException("Non-BLP image inputs only have mip 0.");
static (int Time, string Value) ParseLightingKey(string text)
{
    var separator = text.IndexOf(':'); if (separator <= 0 || !int.TryParse(text[..separator], out var time)) throw new ArgumentException($"Invalid lighting key '{text}'. Expected --key=time:value.");
    return (time, text[(separator + 1)..]);
}
static WorldLightColor ParseLightingColor(string text)
{
    text = text.Trim().TrimStart('#'); if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb)) throw new ArgumentException($"Invalid RGB lighting color '{text}'. Expected #RRGGBB.");
    return new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
static IReadOnlyDictionary<string, object?> ParseStageBindings(IEnumerable<string> options)
{
    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    foreach (var option in options.Where(option => option.StartsWith("--bind=", StringComparison.OrdinalIgnoreCase)))
    {
        var assignment = option[7..]; var separator = assignment.IndexOf('='); if (separator <= 0) throw new ArgumentException($"Invalid binding '{assignment}'. Expected --bind=name=value."); result[assignment[..separator]] = assignment[(separator + 1)..];
    }
    return result;
}
static void PrintStageDiff(DbcStagingDiff diff)
{
    Console.WriteLine($"DIFF\tupdated={diff.UpdatedRows:N0}\tappended={diff.AppendedRows:N0}\tdeleted={diff.DeletedRows:N0}\tcells={diff.ChangedCells:N0}\tapplyable={diff.CanApply}");
    foreach (var finding in diff.Findings) Console.WriteLine($"BLOCK\t{finding}");
    foreach (var change in diff.Changes) Console.WriteLine($"CHANGE\tstage={change.StageId}\trow={change.SourceRow?.ToString() ?? "new"}\tkey={change.RecordKey?.ToString() ?? "-"}\t{change.Column}\t{change.Before}\t=>\t{change.After}");
    if (diff.DetailsTruncated) Console.WriteLine("MORE\tAdditional cell changes are omitted from text output; use --format=json for the bounded structured detail plus exact totals.");
}
static IReadOnlyList<string> ParseList(string? value) => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
static int ParseIntOption(IEnumerable<string> options, string prefix, int fallback)
{
    var value = options.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return value is null ? fallback : int.TryParse(value[prefix.Length..], out var parsed) ? parsed : throw new ArgumentException($"{prefix[..^1]} must be an integer.");
}
static string FormatValues(IReadOnlyDictionary<string, object?> values) => values.Count == 0 ? "<missing>" : string.Join(", ", values.Select(pair => $"{pair.Key}={SqlCell(pair.Value)}"));
static string SqlCell(object? value) => value switch
{
    null => string.Empty,
    byte[] bytes => "0x" + Convert.ToHexString(bytes),
    DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture),
    IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
};
static void PrintBinding(ServerTableBinding binding) => Console.Error.WriteLine($"Binding: {binding.DbcFileName} · {binding.Consumption} · SQL {binding.SqlTableName ?? "none"} · key {binding.KeyStrategy.Kind} · deploy {binding.Destinations} · restart {binding.Restart} · {binding.Profile}{(binding.SourceBacked ? " (source-backed)" : " (built-in profile)")} · {binding.SupportedRevision}");

static void PrintCompatibility(IEnumerable<PatchEntry> entries, string? requiredClientExecutableSha256)
{
    foreach (var issue in PatchManifestService.GetCompatibilityIssues(entries, requiredClientExecutableSha256))
        Console.Error.WriteLine($"{(issue.Code == "ProtectedGlueXmlUnbound" ? "WARNING" : "COMPAT")}: {issue.Message}");
}

static int PrintManifestValidation(PatchManifestValidationResult validation)
{
    foreach (var warning in validation.Warnings) Console.WriteLine($"WARN\t{warning.Code}\t{warning.ArchivePath}\t{warning.Message}");
    foreach (var error in validation.Errors) Console.WriteLine($"FAIL\t{error.Code}\t{error.ArchivePath}\t{error.Message}");
    Console.Error.WriteLine($"Manifest validation {(validation.Passed ? "passed" : "failed")}: {validation.Errors.Count:N0} error(s), {validation.Warnings.Count:N0} warning(s).");
    return validation.Passed ? 0 : 1;
}

static string SchemaRequirementMessage(string tableName, int fields, DbcSchemaResolution resolution) => resolution.MatchKind == DbcSchemaMatchKind.MissingTableFallback
    ? $"A matching named schema is required; '{tableName}' is absent from the selected schema."
    : $"A matching named schema is required; '{tableName}' defines {resolution.DefinedFieldCount} fields but the DBC contains {fields}.";

static DbcSchemaResolution ResolveClientTableSchema(WdbcFile file, string schemaPath, string tableName)
{
    var isDbd = Directory.Exists(schemaPath) || Path.GetExtension(schemaPath).Equals(".dbd", StringComparison.OrdinalIgnoreCase);
    if (!isDbd) return DbcSchemaCatalog.Load(schemaPath).ResolveColumns(tableName, file.FieldCount);
    tableName = file.LogicalTableName;
    var build = file.Db2Metadata?.Build ?? throw new InvalidOperationException("A DBD schema path currently requires a WDB2 file carrying its client build. Use the matching XML definition for WDBC.");
    var definition = Directory.Exists(schemaPath) ? Path.Combine(Path.GetFullPath(schemaPath), tableName + ".dbd") : Path.GetFullPath(schemaPath);
    if (!File.Exists(definition)) throw new FileNotFoundException($"No DBD definition exists for {tableName}.", definition);
    return DbdSchemaService.ResolveFile(definition, build, file.FieldCount, file.RecordSize);
}

static bool DbcRowsEqual(WdbcFile left, int leftRow, WdbcFile right, int rightRow, IReadOnlyList<DbcColumn> columns)
{
    foreach (var column in columns)
    {
        if (column.Type == DbcValueType.StringOffset)
        {
            if (!left.GetString(left.GetRaw(leftRow, column)).Equals(right.GetString(right.GetRaw(rightRow, column)), StringComparison.Ordinal)) return false;
        }
        else if (left.GetRaw(leftRow, column) != right.GetRaw(rightRow, column)) return false;
    }
    return true;
}

static IReadOnlyDictionary<string, string> ParseSetOptions(IEnumerable<string> options)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var option in options)
    {
        if (!option.StartsWith("--set=", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Unknown row mutation option: {option}");
        var assignment = option[6..]; var separator = assignment.IndexOf('=');
        if (separator <= 0) throw new ArgumentException($"Invalid field assignment '{assignment}'. Expected Column=Value.");
        values[assignment[..separator]] = assignment[(separator + 1)..];
    }
    return values;
}

sealed record TextureLayerArgument(string Path, TextureBlendMode BlendMode, double Opacity, int OffsetX, int OffsetY, bool Visible);

sealed class ConsoleProgress : IProgress<(int Done, int Total, string Path)>
{
    private readonly int _step;
    private int _lastPercentage = -1;
    public ConsoleProgress(int step) => _step = step;
    public void Report((int Done, int Total, string Path) value)
    {
        var percentage = value.Total == 0 ? 100 : value.Done * 100 / value.Total;
        if (value.Done != value.Total && percentage / _step == _lastPercentage / _step) return;
        _lastPercentage = percentage;
        Console.Error.WriteLine($"[{percentage,3}%] {value.Done:N0}/{value.Total:N0} · {value.Path}");
    }
}

sealed class ClientIndexConsoleProgress : IProgress<ClientIndexProgress>
{
    public void Report(ClientIndexProgress value) => Console.Error.WriteLine($"[{value.CompletedArchives:N0}/{value.TotalArchives:N0}] {value.Stage}{(value.Cached ? " (cached)" : string.Empty)} · {value.ArchivePath}");
}
