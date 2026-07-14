using WoWCrucible.Core;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return Help();

try
{
    return args[0].ToLowerInvariant() switch
    {
        "dbc" => Dbc(args[1..]),
        "db" => Database(args[1..]).GetAwaiter().GetResult(),
        "server" => Server(args[1..]).GetAwaiter().GetResult(),
        "mpq" => Mpq(args[1..]),
        "manifest" => Manifest(args[1..]),
        _ => Fail($"Unknown command: {args[0]}")
    };
}

catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static async Task<int> Server(string[] args)
{
    if (args is not [var action, var folder] || action is not ("detect" or "inspect")) return Fail("Usage: wowcrucible server <detect|inspect> <installed-server-folder>");
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
    }
    return 0;
}

static async Task<int> Database(string[] args)
{
    if (args is not ["inspect", var host, var portText, var user, var database, .. var options])
        return Fail("Usage: wowcrucible db inspect <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]");
    var unknown = options.Where(option => !option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (unknown.Length > 0) return Fail($"Unknown database option: {unknown[0]}");
    if (!uint.TryParse(portText, out var port) || port is 0 or > 65535) return Fail("Database port must be from 1 to 65535.");
    var passwordEnvironment = options.FirstOrDefault(option => option.StartsWith("--password-env=", StringComparison.OrdinalIgnoreCase))?[15..] ?? "WOW_CRUCIBLE_DB_PASSWORD";
    var password = Environment.GetEnvironmentVariable(passwordEnvironment);
    if (password is null) return Fail($"Set the {passwordEnvironment} environment variable for this process. Passwords are not accepted on the command line.");
    var sslText = options.FirstOrDefault(option => option.StartsWith("--ssl=", StringComparison.OrdinalIgnoreCase))?[6..] ?? "Preferred";
    if (!Enum.TryParse<MySqlConnector.MySqlSslMode>(sslText, true, out var ssl)) return Fail($"Unknown SSL mode: {sslText}");
    var capabilities = await new DatabaseCapabilityService().InspectAsync(new(host, port, user, password, database, ssl));
    Console.WriteLine($"Server\t{capabilities.ServerVersion}"); Console.WriteLine($"Database\t{capabilities.Database}");
    foreach (var table in capabilities.Tables.Values.OrderBy(table => table.Name)) Console.WriteLine($"TABLE\t{table.Name}\t{table.Columns.Count} columns");
    return capabilities.Tables.Count > 0 ? 0 : 1;
}

static int Manifest(string[] args)
{
    if (args is ["create", var manifestPath, var outputFile, .. var rawInputs] && rawInputs.Length > 0)
    {
        var executableOption = rawInputs.FirstOrDefault(value => value.StartsWith("--client-exe=", StringComparison.OrdinalIgnoreCase));
        var allowed = rawInputs.Where(value => value.StartsWith("--allow=", StringComparison.OrdinalIgnoreCase)).Select(value => value[8..]).ToArray();
        var forbidden = rawInputs.Where(value => value.StartsWith("--deny=", StringComparison.OrdinalIgnoreCase)).Select(value => value[7..]).ToArray();
        var countOption = rawInputs.FirstOrDefault(value => value.StartsWith("--count=", StringComparison.OrdinalIgnoreCase));
        var unknown = rawInputs.Where(value => value.StartsWith("--", StringComparison.Ordinal) && !value.StartsWith("--client-exe=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--allow=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--deny=", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("--count=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown manifest option: {unknown[0]}");
        var inputs = rawInputs.Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (inputs.Length == 0) return Fail("Add at least one file or folder to the manifest.");
        var entries = PatchInputMapper.Map(inputs);
        var hash = executableOption is null ? null : PatchManifestService.ComputeExecutableSha256(executableOption[13..]);
        var policy = allowed.Length == 0 && forbidden.Length == 0 && countOption is null ? null : new PatchManifestPolicy(allowed, forbidden, countOption is null ? null : int.Parse(countOption[8..]));
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
            return Fail("Usage:\n  wowcrucible manifest create <manifest.json> <output.mpq> <files/folders...> [--allow=glob] [--deny=glob] [--count=N] [--client-exe=Wow.exe]\n  wowcrucible manifest list <manifest.json>\n  wowcrucible manifest validate <manifest.json> [archive.mpq]\n  wowcrucible manifest build <manifest.json> <output-folder>");
    }
}

static int Dbc(string[] args)
{
    if (args is ["compare", var basePath, var overridePath, var schemaPath])
    {
        var tableName = Path.GetFileNameWithoutExtension(basePath);
        var sample = WdbcFile.Load(basePath);
        var resolution = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(tableName, sample.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        var differences = DbcPromotionService.GetDifferences(basePath, overridePath, resolution.Columns, resolution.KeyStrategy);
        foreach (var difference in differences) Console.WriteLine($"{difference.Id}\t{difference.ColumnName}\t{difference.BaseValue}\t{difference.OverrideValue}");
        Console.Error.WriteLine($"Found {differences.Count:N0} semantic field difference(s)/new row marker(s).");
        return 0;
    }
    if (args is ["promote", "apply", var promotionBasePath, var promotionOverridePath, var promotionSchemaPath, var manifestPath, var outputPath])
    {
        var tableName = Path.GetFileNameWithoutExtension(promotionBasePath);
        var sample = WdbcFile.Load(promotionBasePath);
        var resolution = DbcSchemaCatalog.Load(promotionSchemaPath).ResolveColumns(tableName, sample.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        DbcPromotionService.Apply(promotionBasePath, promotionOverridePath, outputPath, resolution.Columns, resolution.KeyStrategy, DbcPromotionService.LoadManifest(manifestPath));
        Console.Error.WriteLine($"Created promoted DBC: {Path.GetFullPath(outputPath)}");
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
    if (args.Length != 2 || args[0] != "info") return Fail("Usage:\n  wowcrucible dbc info <file.dbc>\n  wowcrucible dbc validate <schema.xml> <dbc-folder> [--strict] [--recursive]\n  wowcrucible dbc compare <base.dbc> <override.dbc> <schema.xml>\n  wowcrucible dbc promote apply <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>");
    var file = WdbcFile.Load(args[1]);
    Console.WriteLine($"Path\t{Path.GetFullPath(args[1])}");
    Console.WriteLine($"Rows\t{file.RowCount}"); Console.WriteLine($"Fields\t{file.FieldCount}"); Console.WriteLine($"StringBytes\t{file.StringTableSize}");
    return 0;
}

static int Mpq(string[] args)
{
    if (args.Length == 0) return Fail("Usage: wowcrucible mpq <list|extract|create|update> ...");
    var service = new PatchArchiveService();
    switch (args[0])
    {
        case "list" when args.Length is 2 or 3:
            {
                var query = args.Length == 3 ? args[2] : string.Empty;
                foreach (var file in service.ListFiles(args[1]).Where(file => file.ArchivePath.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"{file.Size}\t{file.CompressedSize}\t{file.ArchivePath}");
                return 0;
            }
        case "extract" when args.Length >= 3:
            {
                var options = args[3..];
                var query = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal)) ?? string.Empty;
                var quiet = options.Any(option => option.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
                var progressOption = options.FirstOrDefault(option => option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase));
                var progressStep = progressOption is null ? 5 : int.Parse(progressOption[11..]);
                if (progressStep is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(progressStep), "Progress percentage must be from 1 to 100.");
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0) return Fail($"Unknown extract option: {unknown[0]}");
                var files = service.ListFiles(args[1]).Where(file => file.ArchivePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                service.Extract(args[1], args[2], files, quiet ? null : new ConsoleProgress(progressStep));
                Console.Error.WriteLine($"Extracted {files.Length:N0} file(s) to {Path.GetFullPath(args[2])} in {timer.Elapsed.TotalSeconds:0.##}s.");
                return 0;
            }
        case "create" when args.Length >= 3:
            var createEntries = PatchInputMapper.Map(args[2..]); PrintCompatibility(createEntries, null); service.Create(args[1], createEntries); return 0;
        case "update" when args.Length >= 3:
            var updateEntries = PatchInputMapper.Map(args[2..]); PrintCompatibility(updateEntries, null); service.Update(args[1], updateEntries); return 0;
        default:
            return Fail("Usage:\n  wowcrucible mpq list <archive.mpq> [filter]\n  wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N]\n  wowcrucible mpq create <archive.mpq> <files/folders...>\n  wowcrucible mpq update <archive.mpq> <files/folders...>");
    }
}

static int Help()
{
    Console.WriteLine("WoW Crucible CLI\n\n  server detect <installed-server-folder>\n  server inspect <installed-server-folder>\n  dbc info <file.dbc>\n  dbc validate <schema.xml> <dbc-folder> [--strict] [--recursive]\n  dbc compare <base.dbc> <override.dbc> <schema.xml>\n  dbc promote apply <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>\n  db inspect <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]\n  mpq list <archive.mpq> [filter]\n  mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N]\n  mpq create <archive.mpq> <files/folders...>\n  mpq update <small-patch.mpq> <files/folders...>\n  manifest create <manifest.json> <output.mpq> <files/folders...> [--allow=glob] [--deny=glob] [--count=N] [--client-exe=Wow.exe]\n  manifest list <manifest.json>\n  manifest validate <manifest.json> [archive.mpq]\n  manifest build <manifest.json> <output-folder>");
    return 0;
}

static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

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
