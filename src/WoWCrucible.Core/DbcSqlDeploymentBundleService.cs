using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record DbcSqlDeploymentColumn(string DbcName, string SqlName, DbcValueType Type);
public sealed record DbcSqlDeploymentRow(uint Key, string Dimensions, bool SqlExisted,
    IReadOnlyDictionary<string, string?> DbcValues, IReadOnlyDictionary<string, string?> ExpectedSqlValues);
public sealed record DbcSqlDeploymentDatabase(string Host, uint Port, string User, string Database);
public sealed record DbcSqlDeploymentPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    ServerTableBinding Binding,
    DbcSqlDeploymentDatabase Database,
    string SourceDbcFile,
    string SourceDbcSha256,
    string SchemaFile,
    string SchemaSha256,
    string ServerDbcPath,
    string? ExpectedServerDbcSha256,
    string KeyColumnName,
    IReadOnlyList<DbcSqlDeploymentColumn> Columns,
    IReadOnlyList<DbcSqlDeploymentRow> Rows,
    string AuditSqlFile,
    string AuditSqlSha256,
    string MigrationSqlFile,
    string MigrationSqlSha256,
    string RollbackSqlFile,
    string RollbackSqlSha256,
    string ClientManifestFile,
    RestartRequirement Restart,
    string Guidance);

public sealed record DbcSqlDeploymentBundle(string RootPath, string PlanPath, DbcSqlDeploymentPlan Plan);
public sealed record DbcSqlDeploymentReceipt(
    int FormatVersion,
    DateTimeOffset AppliedUtc,
    string BundleRoot,
    string PlanSha256,
    string ServerDbcPath,
    bool ServerDbcExisted,
    string? BackupFile,
    string BeforeServerSha256,
    string AfterServerSha256,
    int SqlRows,
    string Database,
    DateTimeOffset? RolledBackUtc = null);
public sealed record DbcSqlDeploymentApplyResult(string ReceiptPath, string? BackupPath, int SqlRows, string ServerSha256, RestartRequirement Restart);
public sealed record DbcSqlDeploymentRollbackResult(string ReceiptPath, int SqlRows, string? RestoredServerSha256);

public sealed class DbcSqlDeploymentBundleService
{
    public const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public DbcSqlDeploymentBundle Create(string outputRoot, DatabaseConnectionProfile profile, DbcSqlAuditResult audit,
        DbcSchemaResolution schema, string schemaPath, string serverDbcPath, IEnumerable<uint>? selectedKeys = null)
    {
        if (audit.Binding.Consumption != ServerTableConsumption.SqlOverlayed || string.IsNullOrWhiteSpace(audit.Binding.SqlTableName))
            throw new InvalidOperationException("A synchronized DBC/SQL deployment bundle requires a proven SQL-overlay binding.");
        if (schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey)
            throw new InvalidDataException("A deployment bundle cannot be created for a table without a proven stable record key.");
        schemaPath = Path.GetFullPath(schemaPath); serverDbcPath = Path.GetFullPath(serverDbcPath); outputRoot = Path.GetFullPath(outputRoot);
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("The selected schema file does not exist.", schemaPath);
        if (!File.Exists(audit.DbcPath)) throw new FileNotFoundException("The audited source DBC no longer exists.", audit.DbcPath);
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
            throw new IOException($"Deployment bundle output must be new or empty: {outputRoot}");

        var keySet = selectedKeys?.ToHashSet() ?? audit.Rows
            .Where(row => row.Status is DbcSqlRowStatus.SqlOverridesDbc or DbcSqlRowStatus.DbcOnly)
            .Select(row => row.Key).ToHashSet();
        var selected = audit.Rows.Where(row => keySet.Contains(row.Key) && row.DbcValues.Count > 0).OrderBy(row => row.Key).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("No SQL override or missing-overlay row was selected for synchronization.");
        var unknown = keySet.Except(selected.Select(row => row.Key)).ToArray();
        if (unknown.Length > 0) throw new InvalidDataException($"Selected key(s) do not have a source DBC row: {string.Join(", ", unknown)}");

        var keyPhysical = DbcRecordIdentity.PhysicalColumn(schema.Columns, schema.KeyStrategy);
        var columns = schema.Columns
            .Where(column => keyPhysical is null || column.Index != keyPhysical.Index)
            .Select(column => new DbcSqlDeploymentColumn(column.Name,
                audit.SqlColumnNames?.GetValueOrDefault(column.Name) ?? column.Name, column.Type)).ToArray();
        if (columns.Length == 0) throw new InvalidDataException("The overlay has no deployable data columns.");
        var rows = selected.Select(row => new DbcSqlDeploymentRow(row.Key, row.Dimensions, row.SqlValues.Count > 0,
            CanonicalValues(row.DbcValues, columns), CanonicalValues(row.SqlValues, columns))).ToArray();

        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidOperationException("The bundle output has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var payloadDirectory = Path.Combine(staging, "payload", "DBFilesClient"); Directory.CreateDirectory(payloadDirectory);
            var schemaDirectory = Path.Combine(staging, "schema"); Directory.CreateDirectory(schemaDirectory);
            var sqlDirectory = Path.Combine(staging, "sql"); Directory.CreateDirectory(sqlDirectory);
            var sourceRelative = Path.Combine("payload", "DBFilesClient", audit.Binding.DbcFileName);
            var schemaRelative = Path.Combine("schema", Path.GetFileName(schemaPath));
            File.Copy(audit.DbcPath, Path.Combine(staging, sourceRelative), true);
            File.Copy(schemaPath, Path.Combine(staging, schemaRelative), true);
            VerifyStagedInputs(Path.Combine(staging, sourceRelative), Path.Combine(staging, schemaRelative), audit.Binding.ClientTableName, schema, rows);

            var auditSqlRelative = Path.Combine("sql", "audit.sql");
            var migrationRelative = Path.Combine("sql", "migrate.sql");
            var rollbackRelative = Path.Combine("sql", "rollback.sql");
            WriteUtf8(Path.Combine(staging, auditSqlRelative), BuildAuditSql(audit.Binding.SqlTableName!, audit.KeyColumnName, columns, rows));
            WriteUtf8(Path.Combine(staging, migrationRelative), BuildUpsertSql(audit.Binding, audit.KeyColumnName, columns, rows, useDbc: true));
            WriteUtf8(Path.Combine(staging, rollbackRelative), BuildRollbackSql(audit.Binding, audit.KeyColumnName, columns, rows));
            var manifestRelative = Path.Combine("client-patch.crucible-patch.json");
            PatchManifestService.Save(Path.Combine(staging, manifestRelative), $"Synchronize {audit.Binding.DbcFileName}", "patch-Crucible-DBC.MPQ",
                [new PatchEntry(Path.Combine(staging, sourceRelative), $"DBFilesClient\\{audit.Binding.DbcFileName}")],
                policy: new PatchManifestPolicy(["DBFilesClient\\*.dbc"], null, 1));

            var plan = new DbcSqlDeploymentPlan(CurrentFormatVersion, DateTimeOffset.UtcNow, audit.Binding,
                new(profile.Host, profile.Port, profile.User, profile.Database), sourceRelative, Hash(Path.Combine(staging, sourceRelative)),
                schemaRelative, Hash(Path.Combine(staging, schemaRelative)), serverDbcPath, File.Exists(serverDbcPath) ? Hash(serverDbcPath) : null,
                audit.KeyColumnName, columns, rows, auditSqlRelative, Hash(Path.Combine(staging, auditSqlRelative)),
                migrationRelative, Hash(Path.Combine(staging, migrationRelative)), rollbackRelative, Hash(Path.Combine(staging, rollbackRelative)),
                manifestRelative, audit.Binding.Restart,
                $"Synchronizes {rows.Length:N0} proven {audit.Binding.SqlTableName} row(s), the server DBC, and the one-file client patch payload. {audit.Binding.Restart} is required after deployment.");
            WriteJsonAtomic(Path.Combine(staging, "deployment-plan.json"), plan);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot);
            Directory.Move(staging, outputRoot);
            return Load(outputRoot);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    public DbcSqlDeploymentBundle Load(string rootPath)
    {
        rootPath = Path.GetFullPath(rootPath); var planPath = Path.Combine(rootPath, "deployment-plan.json");
        if (!File.Exists(planPath)) throw new FileNotFoundException("The deployment bundle has no deployment-plan.json.", planPath);
        var plan = JsonSerializer.Deserialize<DbcSqlDeploymentPlan>(File.ReadAllText(planPath), JsonOptions) ?? throw new InvalidDataException("The deployment plan is empty.");
        if (plan.FormatVersion != CurrentFormatVersion) throw new InvalidDataException($"Unsupported deployment bundle version {plan.FormatVersion}.");
        if (plan.Binding.Consumption != ServerTableConsumption.SqlOverlayed || string.IsNullOrWhiteSpace(plan.Binding.SqlTableName)) throw new InvalidDataException("The deployment plan has no SQL-overlay binding.");
        if (plan.Rows.Count == 0 || plan.Columns.Count == 0) throw new InvalidDataException("The deployment plan has no synchronized rows or columns.");
        VerifyFile(rootPath, plan.SourceDbcFile, plan.SourceDbcSha256, "source DBC");
        VerifyFile(rootPath, plan.SchemaFile, plan.SchemaSha256, "schema");
        VerifyFile(rootPath, plan.AuditSqlFile, plan.AuditSqlSha256, "audit SQL");
        VerifyFile(rootPath, plan.MigrationSqlFile, plan.MigrationSqlSha256, "migration SQL");
        VerifyFile(rootPath, plan.RollbackSqlFile, plan.RollbackSqlSha256, "rollback SQL");
        var manifestPath = ResolveInside(rootPath, plan.ClientManifestFile); var manifest = PatchManifestService.Load(manifestPath);
        var validation = PatchManifestService.Validate(manifest); if (!validation.Passed) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
        return new(rootPath, planPath, plan);
    }

    public async Task<DbcSqlDeploymentApplyResult> ApplyAsync(string bundleRoot, DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var bundle = Load(bundleRoot); var plan = bundle.Plan; ValidateDatabaseTarget(plan.Database, profile);
        var source = ResolveInside(bundle.RootPath, plan.SourceDbcFile); var expectedSourceHash = Hash(source);
        var target = Path.GetFullPath(plan.ServerDbcPath); var targetExisted = File.Exists(target); var currentTargetHash = targetExisted ? Hash(target) : null;
        if (!string.Equals(currentTargetHash, plan.ExpectedServerDbcSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The live server DBC changed after this bundle was reviewed. Create a new audit/bundle before applying.");

        var applicationRoot = Path.Combine(bundle.RootPath, "applications", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(applicationRoot); string? backupRelative = null; string? backupPath = null;
        if (targetExisted)
        {
            backupPath = Path.Combine(applicationRoot, $"{Path.GetFileName(target)}.before"); File.Copy(target, backupPath, false);
            backupRelative = Path.GetRelativePath(bundle.RootPath, backupPath);
        }
        var fileChanged = false;
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var before = await ReadRowsAsync(connection, transaction, plan, cancellationToken);
            VerifyRows(plan, before, expectedDbc: false, "The live SQL overlay changed after this bundle was reviewed");
            await ExecuteSqlFileAsync(connection, transaction, ResolveInside(bundle.RootPath, plan.MigrationSqlFile), cancellationToken);
            var after = await ReadRowsAsync(connection, transaction, plan, cancellationToken);
            VerifyRows(plan, after, expectedDbc: true, "SQL migration verification failed");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temporary = target + $".crucible-{Guid.NewGuid():N}.tmp";
            try { File.Copy(source, temporary, false); if (!Hash(temporary).Equals(expectedSourceHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("The staged server DBC copy failed SHA-256 verification."); File.Move(temporary, target, true); fileChanged = true; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            if (!Hash(target).Equals(expectedSourceHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("The deployed server DBC failed SHA-256 verification.");
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            if (fileChanged) RestoreTarget(target, targetExisted, backupPath, source);
            throw;
        }

        var receipt = new DbcSqlDeploymentReceipt(1, DateTimeOffset.UtcNow, bundle.RootPath, Hash(bundle.PlanPath), target, targetExisted,
            backupRelative, currentTargetHash ?? "<missing>", expectedSourceHash, plan.Rows.Count, profile.Database);
        var receiptPath = Path.Combine(applicationRoot, "deployment-receipt.json"); WriteJsonAtomic(receiptPath, receipt);
        return new(receiptPath, backupPath, plan.Rows.Count, expectedSourceHash, plan.Restart);
    }

    public async Task<DbcSqlDeploymentRollbackResult> RollbackAsync(string receiptPath, DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        receiptPath = Path.GetFullPath(receiptPath);
        var receipt = JsonSerializer.Deserialize<DbcSqlDeploymentReceipt>(File.ReadAllText(receiptPath), JsonOptions) ?? throw new InvalidDataException("The deployment receipt is empty.");
        if (receipt.RolledBackUtc is not null) throw new InvalidOperationException($"This deployment was already rolled back at {receipt.RolledBackUtc:O}.");
        var bundle = Load(receipt.BundleRoot); var plan = bundle.Plan; ValidateDatabaseTarget(plan.Database, profile);
        if (!Hash(bundle.PlanPath).Equals(receipt.PlanSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The deployment plan changed after apply.");
        var target = Path.GetFullPath(receipt.ServerDbcPath);
        if (!File.Exists(target) || !Hash(target).Equals(receipt.AfterServerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The live server DBC no longer matches the applied receipt; rollback refuses to overwrite later work.");
        var source = ResolveInside(bundle.RootPath, plan.SourceDbcFile);
        var backup = receipt.BackupFile is null ? null : ResolveInside(bundle.RootPath, receipt.BackupFile);
        if (receipt.ServerDbcExisted && (backup is null || !File.Exists(backup) || !Hash(backup).Equals(receipt.BeforeServerSha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The verified pre-deployment server DBC backup is unavailable or changed.");

        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken); var fileChanged = false;
        try
        {
            var current = await ReadRowsAsync(connection, transaction, plan, cancellationToken);
            VerifyRows(plan, current, expectedDbc: true, "The SQL overlay changed after deployment; rollback refuses to overwrite later work");
            await ExecuteSqlFileAsync(connection, transaction, ResolveInside(bundle.RootPath, plan.RollbackSqlFile), cancellationToken);
            var restored = await ReadRowsAsync(connection, transaction, plan, cancellationToken);
            VerifyRows(plan, restored, expectedDbc: false, "SQL rollback verification failed");
            if (receipt.ServerDbcExisted) File.Copy(backup!, target, true); else File.Delete(target); fileChanged = true;
            if (receipt.ServerDbcExisted && !Hash(target).Equals(receipt.BeforeServerSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Restored server DBC failed SHA-256 verification.");
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            if (fileChanged) File.Copy(source, target, true);
            throw;
        }
        var updated = receipt with { RolledBackUtc = DateTimeOffset.UtcNow }; WriteJsonAtomic(receiptPath, updated);
        return new(receiptPath, plan.Rows.Count, receipt.ServerDbcExisted ? receipt.BeforeServerSha256 : null);
    }

    public string ExportModuleMigration(string bundleRoot, string moduleRoot)
    {
        var bundle = Load(bundleRoot); moduleRoot = Path.GetFullPath(moduleRoot);
        var directory = Path.Combine(moduleRoot, "data", "sql", "db-world"); Directory.CreateDirectory(directory);
        var stem = $"{DateTimeOffset.UtcNow:yyyy_MM_dd}_{{0:00}}_crucible_{SafeName(bundle.Plan.Binding.SqlTableName!)}.sql";
        string path; var index = 0; do { path = Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, stem, index++)); } while (File.Exists(path));
        File.Copy(ResolveInside(bundle.RootPath, bundle.Plan.MigrationSqlFile), path, false); return path;
    }

    private static async Task<Dictionary<uint, IReadOnlyDictionary<string, string?>>> ReadRowsAsync(MySqlConnection connection, MySqlTransaction transaction,
        DbcSqlDeploymentPlan plan, CancellationToken cancellationToken)
    {
        var parameters = plan.Rows.Select((row, index) => (row.Key, Name: $"@key{index}")).ToArray();
        var sql = $"SELECT {Quote(plan.KeyColumnName)},{string.Join(",", plan.Columns.Select(column => Quote(column.SqlName)))} FROM {Quote(plan.Binding.SqlTableName!)} WHERE {Quote(plan.KeyColumnName)} IN ({string.Join(",", parameters.Select(item => item.Name))}) FOR UPDATE";
        await using var command = new MySqlCommand(sql, connection, transaction) { CommandTimeout = 30 }; foreach (var item in parameters) command.Parameters.AddWithValue(item.Name, item.Key);
        var result = new Dictionary<uint, IReadOnlyDictionary<string, string?>>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = Convert.ToUInt32(reader[plan.KeyColumnName], CultureInfo.InvariantCulture); var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in plan.Columns) values[column.DbcName] = Canonical(reader[column.SqlName] is DBNull ? null : reader[column.SqlName], column.Type);
            if (!result.TryAdd(key, values)) throw new InvalidDataException($"SQL overlay contains duplicate key {key}.");
        }
        return result;
    }

    private static void VerifyStagedInputs(string dbcPath, string schemaPath, string tableName, DbcSchemaResolution reviewedSchema, IReadOnlyList<DbcSqlDeploymentRow> reviewedRows)
    {
        var file = WdbcFile.Load(dbcPath); var stagedSchema = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(tableName, file.FieldCount);
        if (stagedSchema.UsedFallback || stagedSchema.KeyStrategy != reviewedSchema.KeyStrategy || stagedSchema.Columns.Count != reviewedSchema.Columns.Count ||
            stagedSchema.Columns.Where((column, index) => column.Name != reviewedSchema.Columns[index].Name || column.Type != reviewedSchema.Columns[index].Type || column.Offset != reviewedSchema.Columns[index].Offset || column.Size != reviewedSchema.Columns[index].Size).Any())
            throw new InvalidDataException("The source DBC or schema changed after audit; create a new audit before planning deployment.");
        var indexed = DbcRecordIdentity.IndexRows(file, stagedSchema.Columns, stagedSchema.KeyStrategy);
        var keyPhysical = DbcRecordIdentity.PhysicalColumn(stagedSchema.Columns, stagedSchema.KeyStrategy);
        var columns = stagedSchema.Columns.Where(column => keyPhysical is null || column.Index != keyPhysical.Index)
            .ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var row in reviewedRows)
        {
            if (!indexed.TryGetValue(row.Key, out var rowIndex)) throw new InvalidDataException($"The source DBC changed after audit: reviewed row {row.Key} is missing.");
            foreach (var pair in row.DbcValues)
                if (!columns.TryGetValue(pair.Key, out var column) || !string.Equals(Canonical(file.GetDisplayValue(rowIndex, column), column.Type), pair.Value, StringComparison.Ordinal))
                    throw new InvalidDataException($"The source DBC changed after audit: row {row.Key}, field {pair.Key} no longer matches the reviewed value.");
        }
    }

    private static void VerifyRows(DbcSqlDeploymentPlan plan, IReadOnlyDictionary<uint, IReadOnlyDictionary<string, string?>> actual, bool expectedDbc, string message)
    {
        foreach (var row in plan.Rows)
        {
            var expectedExists = expectedDbc || row.SqlExisted; var exists = actual.TryGetValue(row.Key, out var values);
            if (exists != expectedExists) throw new InvalidDataException($"{message}: row {row.Key} existence differs from the reviewed plan.");
            if (!exists) continue;
            var expected = expectedDbc ? row.DbcValues : row.ExpectedSqlValues;
            foreach (var column in plan.Columns)
                if (!values!.TryGetValue(column.DbcName, out var value) || !string.Equals(value, expected.GetValueOrDefault(column.DbcName), StringComparison.Ordinal))
                    throw new InvalidDataException($"{message}: row {row.Key}, field {column.DbcName} differs from the reviewed plan.");
        }
    }

    private static async Task ExecuteSqlFileAsync(MySqlConnection connection, MySqlTransaction transaction, string path, CancellationToken cancellationToken)
    {
        var sql = await File.ReadAllTextAsync(path, cancellationToken); await using var command = new MySqlCommand(sql, connection, transaction) { CommandTimeout = 120 }; await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildAuditSql(string table, string key, IReadOnlyList<DbcSqlDeploymentColumn> columns, IReadOnlyList<DbcSqlDeploymentRow> rows)
        => $"-- Read-only preflight for the exact reviewed rows.\nSELECT {Quote(key)},{string.Join(",", columns.Select(column => Quote(column.SqlName)))} FROM {Quote(table)} WHERE {Quote(key)} IN ({string.Join(",", rows.Select(row => row.Key))}) ORDER BY {Quote(key)};\n";

    private static string BuildUpsertSql(ServerTableBinding binding, string key, IReadOnlyList<DbcSqlDeploymentColumn> columns, IReadOnlyList<DbcSqlDeploymentRow> rows, bool useDbc)
    {
        var builder = new StringBuilder(); builder.AppendLine($"-- WoW Crucible synchronized deployment for {binding.DbcFileName}"); builder.AppendLine("-- Idempotent for the exact reviewed keys; applying the bundle still performs a stale-target preflight.");
        builder.Append($"INSERT INTO {Quote(binding.SqlTableName!)} ({Quote(key)},{string.Join(",", columns.Select(column => Quote(column.SqlName)))}) VALUES\n");
        for (var index = 0; index < rows.Count; index++)
        {
            var values = useDbc ? rows[index].DbcValues : rows[index].ExpectedSqlValues;
            builder.Append($"({rows[index].Key},{string.Join(",", columns.Select(column => Literal(values.GetValueOrDefault(column.DbcName), column.Type)))})"); builder.AppendLine(index == rows.Count - 1 ? string.Empty : ",");
        }
        builder.Append("ON DUPLICATE KEY UPDATE "); builder.AppendLine(string.Join(",", columns.Select(column => $"{Quote(column.SqlName)}=VALUES({Quote(column.SqlName)})")) + ";"); return builder.ToString();
    }

    private static string BuildRollbackSql(ServerTableBinding binding, string key, IReadOnlyList<DbcSqlDeploymentColumn> columns, IReadOnlyList<DbcSqlDeploymentRow> rows)
    {
        var existing = rows.Where(row => row.SqlExisted).ToArray(); var missing = rows.Where(row => !row.SqlExisted).ToArray(); var builder = new StringBuilder();
        builder.AppendLine($"-- Exact SQL pre-image rollback for {binding.DbcFileName}; generated from the reviewed live rows.");
        if (existing.Length > 0) builder.Append(BuildUpsertSql(binding, key, columns, existing, useDbc: false));
        if (missing.Length > 0) builder.AppendLine($"DELETE FROM {Quote(binding.SqlTableName!)} WHERE {Quote(key)} IN ({string.Join(",", missing.Select(row => row.Key))});");
        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, string?> CanonicalValues(IReadOnlyDictionary<string, object?> values, IReadOnlyList<DbcSqlDeploymentColumn> columns)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); foreach (var column in columns) result[column.DbcName] = Canonical(values.GetValueOrDefault(column.DbcName), column.Type); return result;
    }
    private static string? Canonical(object? value, DbcValueType type)
    {
        if (value is null or DBNull) return null;
        return type switch
        {
            DbcValueType.Float32 => Convert.ToSingle(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            DbcValueType.StringOffset => Convert.ToString(value, CultureInfo.InvariantCulture),
            DbcValueType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToUInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
        };
    }
    private static string Literal(string? value, DbcValueType type) => value is null ? "NULL" : type == DbcValueType.StringOffset ? $"'{value.Replace("\\", "\\\\").Replace("'", "''")}'" : value;
    private static string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
    private static string Hash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void WriteUtf8(string path, string value) => File.WriteAllText(path, value, new UTF8Encoding(false));
    private static void WriteJsonAtomic<T>(string path, T value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false)); File.Move(temporary, path, true); }
    private static string ResolveInside(string root, string relative)
    {
        root = Path.GetFullPath(root); var path = Path.GetFullPath(Path.Combine(root, relative)); var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Bundle path escapes its root: {relative}"); return path;
    }
    private static void VerifyFile(string root, string relative, string expectedHash, string label) { var path = ResolveInside(root, relative); if (!File.Exists(path)) throw new FileNotFoundException($"The bundle {label} is missing.", path); if (!Hash(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The bundle {label} changed after planning: {path}"); }
    private static void ValidateDatabaseTarget(DbcSqlDeploymentDatabase expected, DatabaseConnectionProfile actual)
    {
        if (expected.Port != actual.Port || !expected.Host.Equals(actual.Host, StringComparison.OrdinalIgnoreCase) || !expected.User.Equals(actual.User, StringComparison.OrdinalIgnoreCase) || !expected.Database.Equals(actual.Database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"This bundle was reviewed for {expected.User}@{expected.Host}:{expected.Port}/{expected.Database}, not the supplied database target.");
    }
    private static void RestoreTarget(string target, bool existed, string? backup, string source) { if (existed && backup is not null) File.Copy(backup, target, true); else if (!existed && File.Exists(target)) File.Delete(target); else if (existed && backup is null) File.Copy(source, target, true); }
    private static string SafeName(string value) { var result = new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray()); return string.Join('_', result.Split('_', StringSplitOptions.RemoveEmptyEntries)); }
}
