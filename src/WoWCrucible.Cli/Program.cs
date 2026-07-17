using System.Diagnostics;
using WoWCrucible.Core;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return Help();

try
{
    return args[0].ToLowerInvariant() switch
    {
        "dbc" => Dbc(args[1..]),
        "db" => Database(args[1..]).GetAwaiter().GetResult(),
        "server" => Server(args[1..]).GetAwaiter().GetResult(),
        "client" => Client(args[1..]),
        "asset" => Asset(args[1..]),
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

static int Asset(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return AssetHelp();
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
    if (args is ["workspace", var outputRoot, .. var workspaceInputs] && workspaceInputs.Length > 0)
    {
        var workspace = NativeAssetConversionService.CreateWorkspace(workspaceInputs, outputRoot);
        Console.Error.WriteLine($"Created native conversion workspace: {workspace.RootPath}\nAlready compatible: {workspace.CompatibleAssets:N0}\nRequire conversion: {workspace.ConversionRequired:N0}\nBlocked/invalid: {workspace.BlockedAssets:N0}\nReport: {Path.Combine(workspace.RootPath, "conversion-report.json")}");
        return workspace.BlockedAssets == 0 ? 0 : 3;
    }
    return AssetHelp(2);
}

static int AssetHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible asset inspect <model.m2|building.wmo>...\n  wowcrucible asset workspace <new-output-folder> <files/folders...>", code);

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
        var unknown = extractOptions.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--quiet", StringComparison.OrdinalIgnoreCase) && !option.Equals("--resolved-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--anonymous-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0) return Fail($"Unknown client extract option: {unknown[0]}");
        var filters = extractOptions.Where(option => !option.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (filters.Length > 1) return Fail("Client extract accepts at most one path filter.");
        var started = Stopwatch.StartNew();
        var result = ClientArchiveIndexService.ExtractIndexed(extractIndexDirectory, archiveRelativePath, destination, filters.FirstOrDefault(), resolvedOnly, anonymousOnly, overwrite, quiet ? null : new ConsoleProgress(100));
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

static int ClientHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible client install-patch <patch.mpq> <client-root> [--name=patch-X.MPQ]\n  wowcrucible client clear-cache <client-root>\n  wowcrucible client index <client-root> <index-directory> [--no-hash] [--listfile=paths.txt] [--client-exe=Wow.exe]\n  wowcrucible client corpus <output-listfile> <index-directory>...\n  wowcrucible client extract <index-directory> <archive-relative-path> <folder> [path-glob-or-text] [--resolved-only|--anonymous-only] [--overwrite] [--quiet]\n  wowcrucible client show <index-directory>\n  wowcrucible client fusion <base-root> <override-root>... [--output=plan.json] [--stage=review-folder] [--all]", code);

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
        var source = Option(auditOptions, "--source="); var migration = Option(auditOptions, "--migration=");
        var showAll = auditOptions.Any(option => option.Equals("--all", StringComparison.OrdinalIgnoreCase)); var summaryOnly = auditOptions.Any(option => option.Equals("--summary", StringComparison.OrdinalIgnoreCase));
        var unknown = auditOptions.Where(option => !option.StartsWith("--source=", StringComparison.OrdinalIgnoreCase) && !option.StartsWith("--migration=", StringComparison.OrdinalIgnoreCase) && !option.Equals("--all", StringComparison.OrdinalIgnoreCase) && !option.Equals("--summary", StringComparison.OrdinalIgnoreCase)).ToArray();
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
    var text = "Usage:\n  wowcrucible server detect <installed-server-folder>\n  wowcrucible server inspect <installed-server-folder>\n  wowcrucible server bindings <installed-server-folder> [--source=core-source]\n  wowcrucible server dbc-audit <installed-server-folder> <dbc-file-or-name> <schema.xml> [--source=core-source] [--all|--summary] [--migration=output.sql]\n  wowcrucible server client-plan <installed-server-folder> <extracted-dbc-root> [--source=core-source] [--output=plan.json] [--stage=review-folder]";
    if (code == 0) Console.WriteLine(text); else Console.Error.WriteLine(text); return code;
}

static async Task<int> Database(string[] args)
{
    if (args.Length > 0 && args[0] is "help" or "--help" or "-h") { Console.WriteLine("Usage: wowcrucible db inspect <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]"); return 0; }
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
    if (args.Length > 0 && args[0] is "help" or "--help" or "-h") return DbcHelp();
    if (args is ["rows", var rowsPath, var rowsSchemaPath, .. var rawIds] && rawIds.Length > 0)
    {
        var rowsFile = WdbcFile.Load(rowsPath); var tableName = Path.GetFileNameWithoutExtension(rowsPath);
        var resolution = DbcSchemaCatalog.Load(rowsSchemaPath).ResolveColumns(tableName, rowsFile.FieldCount);
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
        var resolution = DbcSchemaCatalog.Load(findSchemaPath).ResolveColumns(tableName, findFile.FieldCount);
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
        var resolution = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(tableName, sample.FieldCount);
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
        var resolution = DbcSchemaCatalog.Load(promotionSchemaPath).ResolveColumns(tableName, sample.FieldCount);
        if (resolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(tableName, sample.FieldCount, resolution));
        DbcPromotionService.Apply(promotionBasePath, promotionOverridePath, outputPath, resolution.Columns, resolution.KeyStrategy, DbcPromotionService.LoadManifest(manifestPath));
        Console.Error.WriteLine($"Created promoted DBC: {Path.GetFullPath(outputPath)}");
        return 0;
    }
    if (args is ["promote", "additions", var additionsBasePath, var additionsOverridePath, var additionsSchemaPath, var additionsManifestPath, var additionsOutputPath])
    {
        var tableName = Path.GetFileNameWithoutExtension(additionsBasePath); var sample = WdbcFile.Load(additionsBasePath);
        var resolution = DbcSchemaCatalog.Load(additionsSchemaPath).ResolveColumns(tableName, sample.FieldCount);
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
        var sourceFile = WdbcFile.Load(cloneSourcePath); var tableName = Path.GetFileNameWithoutExtension(cloneBasePath);
        if (!tableName.Equals(Path.GetFileNameWithoutExtension(cloneSourcePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Base and source DBC table names differ.");
        var resolution = DbcSchemaCatalog.Load(cloneSchemaPath).ResolveColumns(tableName, sourceFile.FieldCount);
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
        var parentResolution = DbcSchemaCatalog.Load(parentSchemaPath).ResolveColumns(parentTable, parentSource.FieldCount);
        if (parentResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(parentTable, parentSource.FieldCount, parentResolution));
        var childBase = WdbcFile.Load(childBasePath); var childTable = Path.GetFileNameWithoutExtension(childBasePath);
        var childResolution = DbcSchemaCatalog.Load(childSchemaPath).ResolveColumns(childTable, childBase.FieldCount);
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
        var copyResolution = DbcSchemaCatalog.Load(copySchemaPath).ResolveColumns(copyTable, copySample.FieldCount);
        if (copyResolution.UsedFallback) throw new InvalidDataException(SchemaRequirementMessage(copyTable, copySample.FieldCount, copyResolution));
        DbcRowMutationService.CopyRow(copyBasePath, copySourcePath, copyOutputPath, copyResolution.Columns, copyResolution.KeyStrategy, copySourceId, copyTargetId, copyValues);
        Console.Error.WriteLine($"Copied {copyTable} ID {copySourceId} to additive ID {copyTargetId} with {copyValues.Count:N0} field override(s): {Path.GetFullPath(copyOutputPath)}");
        return 0;
    }
    if (args is ["set-row", var setInputPath, var setSchemaPath, var setIdText, var setOutputPath, .. var setOptions])
    {
        if (!uint.TryParse(setIdText, out var setId)) return Fail("Record ID must be an unsigned integer.");
        var setValues = ParseSetOptions(setOptions); var setSample = WdbcFile.Load(setInputPath); var setTable = Path.GetFileNameWithoutExtension(setInputPath);
        var setResolution = DbcSchemaCatalog.Load(setSchemaPath).ResolveColumns(setTable, setSample.FieldCount);
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
    Console.WriteLine($"Rows\t{file.RowCount}"); Console.WriteLine($"Fields\t{file.FieldCount}"); Console.WriteLine($"StringBytes\t{file.StringTableSize}");
    return 0;
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
                var unknown = options.Where(option => option.StartsWith("--", StringComparison.Ordinal) && !option.Equals("--content-only", StringComparison.OrdinalIgnoreCase) && !option.Equals("--format=json", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0) return Fail($"Unknown list option: {unknown[0]}");
                var files = service.ListFiles(args[1]).Where(file => (!contentOnly || !file.IsMetadata) && MpqPathFilter.Matches(file.ArchivePath, query)).ToArray();
                if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(files, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                else foreach (var file in files) Console.WriteLine($"{file.Size}\t{file.CompressedSize}\t{file.ArchivePath}");
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
                var files = service.ListFiles(args[1]).Where(file => MpqPathFilter.Matches(file.ArchivePath, query)).ToArray();
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

static int ManifestHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible manifest create <manifest.json> <output.mpq> <files/folders...> [--allow=glob] [--deny=glob] [--require=glob] [--count=N] [--client-exe=Wow.exe]\n  wowcrucible manifest list <manifest.json>\n  wowcrucible manifest validate <manifest.json> [archive.mpq]\n  wowcrucible manifest build <manifest.json> <output-folder>", code);
static int DbcHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible dbc info <file.dbc>\n  wowcrucible dbc rows <file.dbc> <schema.xml> <id>...\n  wowcrucible dbc find <file.dbc> <schema.xml> <column> <value>... [--count|--limit=N]\n  wowcrucible dbc validate <schema.xml> <dbc-folder> [--strict] [--recursive]\n  wowcrucible dbc compare <base.dbc> <override.dbc> <schema.xml> [--summary]\n  wowcrucible dbc promote apply <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>\n  wowcrucible dbc promote additions <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>\n  wowcrucible dbc clone-remap where <base.dbc> <source.dbc> <schema.xml> <column> <value>... --manifest=map.json --output=merged.dbc [--start-id=N]\n  wowcrucible dbc clone-dependency <parent-source.dbc> <parent-merged.dbc> <parent-schema.xml> <parent-map.json> <foreign-column> <child-base.dbc> <child-source.dbc> <child-schema.xml> --child-map=map.json --child-output=child.dbc --parent-output=parent.dbc\n  wowcrucible dbc copy-row <base.dbc> <source.dbc> <schema.xml> <source-id> <target-id> <output.dbc> [--set=Column=Value]...\n  wowcrucible dbc set-row <input.dbc> <schema.xml> <id> <output.dbc> --set=Column=Value [...]", code);
static int MpqHelp(int code = 0) => GroupHelp("Usage:\n  wowcrucible mpq list <archive.mpq> [filter] [--content-only] [--format=json]\n  wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N]\n  wowcrucible mpq create <archive.mpq> <files/folders...>\n  wowcrucible mpq update <archive.mpq> <files/folders...>", code);
static int GroupHelp(string message, int code) { if (code == 0) Console.WriteLine(message); else Console.Error.WriteLine(message); return code; }

static int Help()
{
    Console.WriteLine("WoW Crucible CLI\n\n  asset inspect <model.m2|building.wmo>...\n  asset workspace <new-output-folder> <files/folders...>\n  client install-patch <patch.mpq> <client-root> [--name=patch-X.MPQ]\n  client clear-cache <client-root>\n  client index <client-root> <index-directory> [--no-hash] [--listfile=paths.txt] [--client-exe=Wow.exe]\n  client corpus <output-listfile> <index-directory>...\n  client extract <index-directory> <archive-relative-path> <folder> [path-glob-or-text] [--resolved-only|--anonymous-only] [--overwrite] [--quiet]\n  client show <index-directory>\n  client fusion <base-root> <override-root>... [--stage=review-folder]\n  server detect <installed-server-folder>\n  server inspect <installed-server-folder>\n  server bindings <installed-server-folder> [--source=core-source]\n  server dbc-audit <installed-server-folder> <dbc-file-or-name> <schema.xml> [--source=core-source] [--all] [--migration=sync.sql]\n  server client-plan <installed-server-folder> <extracted-dbc-root> [--source=core-source] [--output=plan.json] [--stage=server-review]\n  dbc info <file.dbc>\n  dbc validate <schema.xml> <dbc-folder> [--strict] [--recursive]\n  dbc compare <base.dbc> <override.dbc> <schema.xml> [--summary]\n  dbc promote apply <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>\n  db inspect <host> <port> <user> <database> [--password-env=NAME] [--ssl=Preferred]\n  mpq list <archive.mpq> [filter] [--content-only] [--format=json]\n  mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N]\n  mpq create <archive.mpq> <files/folders...>\n  mpq update <small-patch.mpq> <files/folders...>\n  manifest create <manifest.json> <output.mpq> <files/folders...> [--allow=glob] [--deny=glob] [--count=N] [--client-exe=Wow.exe]\n  manifest list <manifest.json>\n  manifest validate <manifest.json> [archive.mpq]\n  manifest build <manifest.json> output-folder");
    return 0;
}

static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

static string? Option(IEnumerable<string> options, string prefix) => options.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
static string FormatValues(IReadOnlyDictionary<string, object?> values) => values.Count == 0 ? "<missing>" : string.Join(", ", values.Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)}"));
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
