using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum SqlDatabaseObjectType { View, Trigger, Procedure, Function, Event }
public sealed record SqlDatabaseObjectInfo(SqlDatabaseObjectType Type, string Database, string Name, string Definer,
    string Details, DateTime? Created = null, DateTime? Modified = null, string? State = null)
{
    public string Identity => $"{Database}\u001f{Type}\u001f{Name}";
    public string Display => $"{Type} · {Name} · {Details}{(string.IsNullOrWhiteSpace(State) ? string.Empty : $" · {State}")}";
}
public sealed record SqlDatabaseObjectDefinition(SqlDatabaseObjectInfo Object, string CreateSql);
public sealed record SqlDatabaseObjectExportResult(string Path, int Objects, long Bytes);
public sealed record SqlDatabaseObjectChangePlan(
    string Format,
    DateTimeOffset PreparedUtc,
    string Host,
    uint Port,
    string Database,
    SqlDatabaseObjectType Type,
    string Name,
    string DesiredCreateSql,
    string DesiredCreateSqlSha256,
    string? ExpectedCreateSql,
    string? ExpectedCreateSqlSha256,
    string ReviewSql,
    IReadOnlyList<string> Warnings)
{
    public bool ReplacesExisting => ExpectedCreateSql is not null;
}
public sealed record SqlDatabaseObjectChangeReceipt(
    string Format,
    string State,
    DateTimeOffset AppliedUtc,
    DateTimeOffset? RolledBackUtc,
    string Host,
    uint Port,
    string Database,
    SqlDatabaseObjectType Type,
    string Name,
    string DesiredCreateSql,
    string? BeforeCreateSql,
    string? BeforeCreateSqlSha256,
    string? AfterCreateSql,
    string? AfterCreateSqlSha256,
    string? Failure,
    string ContentSha256);
public sealed record SqlDatabaseObjectChangeResult(SqlDatabaseObjectChangeReceipt Receipt, string ReceiptPath);

public sealed class SqlDatabaseObjectService
{
    public const string ChangePlanFormat = "wow-crucible-sql-object-plan-v1";
    public const string ChangeReceiptFormat = "wow-crucible-sql-object-receipt-v1";
    private static readonly JsonSerializerOptions ArtifactJson = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly Regex CreateHeader = new(
        @"^\s*CREATE\s+(?:(?:OR\s+REPLACE|ALGORITHM\s*=\s*[A-Z_]+|DEFINER\s*=\s*(?:`(?:``|[^`])+`@`(?:``|[^`])+`|'(?:''|[^'])+'@'(?:'(?:''|[^'])+'|[^\s]+)|[^\s]+)|SQL\s+SECURITY\s+(?:DEFINER|INVOKER))\s+)*(?<type>VIEW|TRIGGER|PROCEDURE|FUNCTION|EVENT)\s+(?:(?<database>`(?:``|[^`])+`|[A-Z0-9_$]+)\s*\.\s*)?(?<name>`(?:``|[^`])+`|[A-Z0-9_$]+)(?![A-Z0-9_$`])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<IReadOnlyList<SqlDatabaseObjectInfo>> ListAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var result = new List<SqlDatabaseObjectInfo>();
        await ReadAsync(connection, """
            SELECT TABLE_NAME, COALESCE(DEFINER,''), SECURITY_TYPE, CHECK_OPTION, IS_UPDATABLE
            FROM information_schema.VIEWS WHERE TABLE_SCHEMA=@database ORDER BY TABLE_NAME
            """, profile.Database, reader => new(SqlDatabaseObjectType.View, profile.Database, reader.GetString(0), reader.GetString(1),
                $"security {reader.GetString(2)} · check {reader.GetString(3)} · updatable {reader.GetString(4)}"), result, cancellationToken);
        await ReadAsync(connection, """
            SELECT TRIGGER_NAME, COALESCE(DEFINER,''), ACTION_TIMING, EVENT_MANIPULATION, EVENT_OBJECT_TABLE, CREATED
            FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@database ORDER BY TRIGGER_NAME
            """, profile.Database, reader => new(SqlDatabaseObjectType.Trigger, profile.Database, reader.GetString(0), reader.GetString(1),
                $"{reader.GetString(2)} {reader.GetString(3)} on {reader.GetString(4)}", NullableDateTime(reader, 5)), result, cancellationToken);
        await ReadAsync(connection, """
            SELECT ROUTINE_NAME, ROUTINE_TYPE, COALESCE(DEFINER,''), SECURITY_TYPE, SQL_DATA_ACCESS, DTD_IDENTIFIER, CREATED, LAST_ALTERED
            FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=@database ORDER BY ROUTINE_TYPE, ROUTINE_NAME
            """, profile.Database, reader =>
            {
                var type = reader.GetString(1).Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ? SqlDatabaseObjectType.Function : SqlDatabaseObjectType.Procedure;
                return new(type, profile.Database, reader.GetString(0), reader.GetString(2),
                    $"security {reader.GetString(3)} · {reader.GetString(4)}{(reader.IsDBNull(5) ? string.Empty : $" · returns {reader.GetString(5)}")}", NullableDateTime(reader, 6), NullableDateTime(reader, 7));
            }, result, cancellationToken);
        await ReadAsync(connection, """
            SELECT EVENT_NAME, COALESCE(DEFINER,''), EVENT_TYPE, STATUS, ON_COMPLETION, CREATED, LAST_ALTERED,
                   EXECUTE_AT, INTERVAL_VALUE, INTERVAL_FIELD
            FROM information_schema.EVENTS WHERE EVENT_SCHEMA=@database ORDER BY EVENT_NAME
            """, profile.Database, reader => new(SqlDatabaseObjectType.Event, profile.Database, reader.GetString(0), reader.GetString(1),
                EventDetails(reader), NullableDateTime(reader, 5), NullableDateTime(reader, 6), reader.GetString(3)), result, cancellationToken);
        return result.OrderBy(item => item.Type).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<SqlDatabaseObjectDefinition> ShowCreateAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectInfo item, CancellationToken cancellationToken = default)
    {
        ValidateTarget(profile, item); await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"SHOW CREATE {Keyword(item.Type)} {Qualified(item.Database, item.Name)}", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException($"SHOW CREATE returned no row for {item.Display}.");
        var index = Enumerable.Range(0, reader.FieldCount).FirstOrDefault(field => reader.GetName(field).StartsWith("Create ", StringComparison.OrdinalIgnoreCase) || reader.GetName(field).Equals("SQL Original Statement", StringComparison.OrdinalIgnoreCase), -1);
        if (index < 0 || reader.IsDBNull(index)) throw new InvalidDataException($"SHOW CREATE did not expose a definition column for {item.Display}.");
        return new(item, reader.GetString(index));
    }

    public async Task<SqlDatabaseObjectExportResult> ExportAsync(DatabaseConnectionProfile profile, string outputPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath); if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output already exists: {outputPath}");
        var objects = await ListAsync(profile, cancellationToken); var builder = new StringBuilder();
        builder.AppendLine($"-- WoW Crucible exact database-object export"); builder.AppendLine($"-- Database: {profile.Database}"); builder.AppendLine($"-- Generated: {DateTimeOffset.UtcNow:O}"); builder.AppendLine("-- Review DEFINER clauses before importing on another server."); builder.AppendLine();
        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested(); var definition = await ShowCreateAsync(profile, item, cancellationToken);
            builder.AppendLine($"-- {item.Type} {item.Database}.{item.Name}"); builder.AppendLine("DELIMITER $$"); builder.Append(definition.CreateSql.Trim().TrimEnd(';')).AppendLine("$$"); builder.AppendLine("DELIMITER ;"); builder.AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); var temporary = outputPath + $".{Guid.NewGuid():N}.tmp";
        try { await File.WriteAllTextAsync(temporary, builder.ToString(), new UTF8Encoding(false), cancellationToken); File.Move(temporary, outputPath, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(outputPath, objects.Count, new FileInfo(outputPath).Length);
    }

    public static string BuildDropSql(SqlDatabaseObjectInfo item) => $"DROP {Keyword(item.Type)} {Qualified(item.Database, ValidateName(item.Name))};";

    public async Task DropAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectInfo item, CancellationToken cancellationToken = default)
    {
        ValidateTarget(profile, item); var current = await ListAsync(profile, cancellationToken);
        if (!current.Any(candidate => candidate.Identity.Equals(item.Identity, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"{item.Display} no longer exists.");
        await ExecuteDdlAsync(profile, BuildDropSql(item), cancellationToken);
    }

    public static string BuildCreateOrReplaceViewSql(string database, string name, string selectSql)
    {
        database = ValidateName(database); name = ValidateName(name); var statements = SqlReadBatchParser.Split(selectSql);
        if (statements.Count != 1 || !SqlReadBatchParser.IsReadOnlyStatement(statements[0]) || !FirstToken(statements[0]).Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A guided view must contain exactly one SELECT statement. SHOW/DESCRIBE/EXPLAIN, writes, batches, and SELECT file output are blocked.");
        return $"CREATE OR REPLACE VIEW {Qualified(database, name)} AS\n{statements[0].Trim().TrimEnd(';')};";
    }

    public async Task CreateOrReplaceViewAsync(DatabaseConnectionProfile profile, string name, string selectSql, CancellationToken cancellationToken = default)
        => await ExecuteDdlAsync(profile, BuildCreateOrReplaceViewSql(profile.Database, name, selectSql), cancellationToken);

    public static string BuildEventStateSql(SqlDatabaseObjectInfo item, bool enabled)
    {
        if (item.Type != SqlDatabaseObjectType.Event) throw new ArgumentException("Only scheduled events have an ENABLE/DISABLE state.");
        return $"ALTER EVENT {Qualified(item.Database, ValidateName(item.Name))} {(enabled ? "ENABLE" : "DISABLE")};";
    }

    public async Task SetEventEnabledAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectInfo item, bool enabled, CancellationToken cancellationToken = default)
    {
        ValidateTarget(profile, item); await ExecuteDdlAsync(profile, BuildEventStateSql(item, enabled), cancellationToken);
    }

    public async Task<SqlDatabaseObjectChangePlan> PrepareChangeAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectType type,
        string name, string createSql, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name); var desired = ValidateCreateDefinition(type, profile.Database, name, createSql);
        var existing = (await ListAsync(profile, cancellationToken)).FirstOrDefault(candidate => candidate.Type == type && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var before = existing is null ? null : (await ShowCreateAsync(profile, existing, cancellationToken)).CreateSql;
        var warnings = new List<string>
        {
            "MySQL object DDL may implicitly commit and cannot be promised transaction rollback.",
            existing is null ? "The reviewed identity must remain absent until apply." : "The exact current SHOW CREATE definition is hash-bound and must remain unchanged until apply."
        };
        if (existing is not null && type != SqlDatabaseObjectType.View) warnings.Add("Replacement requires DROP followed by CREATE. Crucible saves the exact preimage first and automatically attempts restoration if CREATE fails.");
        if (desired.Contains("DEFINER", StringComparison.OrdinalIgnoreCase)) warnings.Add("The exact DEFINER clause is retained. Confirm that account exists and is intentional on this server.");
        var review = existing is null ? desired : $"{BuildDropSql(existing)}\n{desired}";
        return new(ChangePlanFormat, DateTimeOffset.UtcNow, profile.Host, profile.Port, profile.Database, type, name, desired, Sha256(desired), before, before is null ? null : Sha256(before), review, warnings);
    }

    public async Task<SqlDatabaseObjectChangeResult> ApplyChangeAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectChangePlan plan,
        CancellationToken cancellationToken = default)
    {
        ValidateChangeTarget(profile, plan); if (!plan.Format.Equals(ChangePlanFormat, StringComparison.Ordinal)) throw new InvalidDataException($"Unsupported SQL object plan format {plan.Format}.");
        var desired = ValidateCreateDefinition(plan.Type, plan.Database, plan.Name, plan.DesiredCreateSql);
        if (!Sha256(desired).Equals(plan.DesiredCreateSqlSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The reviewed SQL object definition hash is invalid.");
        var current = await ReadExactAsync(profile, plan.Type, plan.Name, cancellationToken);
        if (plan.ExpectedCreateSql is null)
        {
            if (current is not null) throw new InvalidOperationException($"{plan.Type} {plan.Database}.{plan.Name} appeared after review; no DDL was executed.");
        }
        else
        {
            if (current is null || !Sha256(current.CreateSql).Equals(plan.ExpectedCreateSqlSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{plan.Type} {plan.Database}.{plan.Name} changed after review; no DDL was executed.");
        }

        var receiptPath = AllocateReceiptPath(plan.Name); var pending = Receipt("PREIMAGE_SAVED_DDL_NOT_VERIFIED", profile, plan, null, null, null, null);
        WriteArtifact(receiptPath, pending, replace: false); var existingInfo = current?.Object;
        try
        {
            if (existingInfo is not null) await ExecuteDdlAsync(profile, BuildDropSql(existingInfo), cancellationToken);
            try { await ExecuteDdlAsync(profile, desired, cancellationToken); }
            catch (Exception createFailure) when (plan.ExpectedCreateSql is not null)
            {
                try
                {
                    await ExecuteDdlAsync(profile, plan.ExpectedCreateSql, CancellationToken.None);
                    var restored = await ReadExactAsync(profile, plan.Type, plan.Name, CancellationToken.None);
                    if (restored is null || !Sha256(restored.CreateSql).Equals(plan.ExpectedCreateSqlSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The server did not restore the exact pre-change definition.");
                    var recovered = Receipt("CREATE_FAILED_PREIMAGE_RESTORED", profile, plan, restored.CreateSql, Sha256(restored.CreateSql), createFailure.Message, null); WriteArtifact(receiptPath, recovered, replace: true);
                    throw new InvalidOperationException($"Replacement CREATE failed, but Crucible restored the exact previous {plan.Type}. Recovery receipt: {receiptPath}", createFailure);
                }
                catch (InvalidOperationException exception) when (exception.InnerException == createFailure) { throw; }
                catch (Exception recoveryFailure)
                {
                    var failed = Receipt("RECOVERY_FAILED_MANUAL_ACTION_REQUIRED", profile, plan, null, null, $"CREATE: {createFailure.Message}\nRECOVERY: {recoveryFailure.Message}", null); WriteArtifact(receiptPath, failed, replace: true);
                    throw new InvalidOperationException($"Replacement CREATE failed and automatic restoration also failed. Use the exact preimage in {receiptPath} for manual recovery.", new AggregateException(createFailure, recoveryFailure));
                }
            }
            var after = await ReadExactAsync(profile, plan.Type, plan.Name, cancellationToken) ?? throw new InvalidOperationException("The server accepted object DDL but the expected identity is not visible afterward.");
            var receipt = Receipt("COMMITTED", profile, plan, after.CreateSql, Sha256(after.CreateSql), null, null); WriteArtifact(receiptPath, receipt, replace: true); return new(receipt, receiptPath);
        }
        catch (Exception failure)
        {
            try
            {
                var recorded = JsonSerializer.Deserialize<SqlDatabaseObjectChangeReceipt>(await File.ReadAllTextAsync(receiptPath, CancellationToken.None), ArtifactJson);
                if (recorded is not null && recorded.State.Equals("PREIMAGE_SAVED_DDL_NOT_VERIFIED", StringComparison.Ordinal))
                {
                    var observed = await ReadExactAsync(profile, plan.Type, plan.Name, CancellationToken.None); var observedSql = observed?.CreateSql; var observedSha = observedSql is null ? null : Sha256(observedSql);
                    var state = observedSql is null ? "CREATE_FAILED_IDENTITY_ABSENT" : plan.ExpectedCreateSqlSha256 is not null && observedSha!.Equals(plan.ExpectedCreateSqlSha256, StringComparison.OrdinalIgnoreCase) ? "APPLY_FAILED_PREIMAGE_PRESENT" : "APPLY_FAILED_LIVE_OBJECT_REQUIRES_REVIEW";
                    var failed = Receipt(state, profile, plan, observedSql, observedSha, failure.Message, null); WriteArtifact(receiptPath, failed, replace: true);
                }
            }
            catch { /* The pending preimage receipt remains the recovery authority if failure-state enrichment is itself unavailable. */ }
            throw;
        }
    }

    public async Task<SqlDatabaseObjectChangeResult> RollbackChangeAsync(DatabaseConnectionProfile profile, string receiptPath,
        CancellationToken cancellationToken = default)
    {
        receiptPath = Path.GetFullPath(receiptPath); var receipt = await LoadReceiptAsync(receiptPath, cancellationToken);
        if (!receipt.State.Equals("COMMITTED", StringComparison.Ordinal) || receipt.RolledBackUtc is not null) throw new InvalidOperationException("Only a committed, not-yet-rolled-back SQL object receipt can be rolled back.");
        if (!profile.Host.Equals(receipt.Host, StringComparison.OrdinalIgnoreCase) || profile.Port != receipt.Port || !profile.Database.Equals(receipt.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The SQL object receipt belongs to a different database target.");
        var current = await ReadExactAsync(profile, receipt.Type, receipt.Name, cancellationToken);
        if (current is null || !Sha256(current.CreateSql).Equals(receipt.AfterCreateSqlSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The live SQL object changed after this receipt; rollback is refused.");
        await ExecuteDdlAsync(profile, BuildDropSql(current.Object), cancellationToken);
        if (receipt.BeforeCreateSql is not null)
        {
            try { await ExecuteDdlAsync(profile, receipt.BeforeCreateSql, cancellationToken); }
            catch
            {
                try { await ExecuteDdlAsync(profile, receipt.AfterCreateSql!, CancellationToken.None); } catch { }
                throw;
            }
            var restored = await ReadExactAsync(profile, receipt.Type, receipt.Name, cancellationToken);
            if (restored is null || !Sha256(restored.CreateSql).Equals(receipt.BeforeCreateSqlSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Rollback did not restore the exact pre-change SQL object definition.");
        }
        else if (await ReadExactAsync(profile, receipt.Type, receipt.Name, cancellationToken) is not null) throw new InvalidOperationException("Rollback did not remove the newly created SQL object.");
        var rolledBackUtc = DateTimeOffset.UtcNow; var rolledBack = receipt with { State = "ROLLED_BACK", RolledBackUtc = rolledBackUtc, ContentSha256 = string.Empty }; rolledBack = rolledBack with { ContentSha256 = ReceiptHash(rolledBack) }; WriteArtifact(receiptPath, rolledBack, replace: true); return new(rolledBack, receiptPath);
    }

    public async Task<SqlDatabaseObjectChangeReceipt> LoadReceiptAsync(string receiptPath, CancellationToken cancellationToken = default)
    {
        var receipt = JsonSerializer.Deserialize<SqlDatabaseObjectChangeReceipt>(await File.ReadAllTextAsync(Path.GetFullPath(receiptPath), cancellationToken), ArtifactJson) ?? throw new InvalidDataException("SQL object receipt is empty."); ValidateReceipt(receipt); return receipt;
    }

    public static string ValidateCreateDefinition(SqlDatabaseObjectType type, string database, string name, string createSql)
    {
        database = ValidateName(database); name = ValidateName(name); var value = (createSql ?? string.Empty).Trim().TrimStart('\uFEFF');
        if (value.Length is < 8 or > 2_000_000) throw new ArgumentException("An exact object definition must contain 8–2,000,000 characters.");
        if (Regex.IsMatch(value, @"(?im)^\s*DELIMITER\b")) throw new InvalidDataException("Remove mysql-client DELIMITER lines. Crucible sends one exact CREATE definition directly through the server protocol.");
        var match = CreateHeader.Match(value); if (!match.Success) throw new InvalidDataException("Definition must begin with one exact CREATE VIEW, TRIGGER, PROCEDURE, FUNCTION, or EVENT statement.");
        var parsedType = Enum.Parse<SqlDatabaseObjectType>(match.Groups["type"].Value, true); if (parsedType != type) throw new InvalidDataException($"Definition creates {parsedType}, but the reviewed object type is {type}.");
        var parsedName = Unquote(match.Groups["name"].Value); if (!parsedName.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Definition creates {parsedName}, but the reviewed object name is {name}.");
        if (match.Groups["database"].Success && !Unquote(match.Groups["database"].Value).Equals(database, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Definition targets database {Unquote(match.Groups["database"].Value)}, not {database}.");
        EnsureSingleCreateStatement(value, type);
        return value.TrimEnd().TrimEnd(';').TrimEnd() + ";";
    }

    private static void EnsureSingleCreateStatement(string sql, SqlDatabaseObjectType type)
    {
        var tokens = TokenizeDefinition(sql);
        var firstBegin = type == SqlDatabaseObjectType.View ? -1 : tokens.FindIndex(token => token == "BEGIN");
        if (firstBegin >= 0 && tokens.Take(firstBegin).Contains(";"))
            throw new InvalidDataException("An exact object definition must contain one CREATE statement; its compound root BEGIN must precede every statement terminator.");
        if (firstBegin < 0)
        {
            var terminator = tokens.FindIndex(token => token == ";");
            if (terminator >= 0 && tokens.Skip(terminator + 1).Any())
                throw new InvalidDataException("An exact object definition must contain one CREATE statement; remove every statement after its terminal semicolon.");
            return;
        }

        var beginDepth = 0;
        var caseDepth = 0;
        var rootEndedAt = -1;
        for (var index = firstBegin; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token == "BEGIN") { beginDepth++; continue; }
            if (token == "CASE") { caseDepth++; continue; }
            if (token != "END") continue;

            var qualifier = index + 1 < tokens.Count ? tokens[index + 1] : string.Empty;
            if (qualifier is "IF" or "LOOP" or "WHILE" or "REPEAT") { index++; continue; }
            if (qualifier == "CASE") { if (caseDepth > 0) caseDepth--; index++; continue; }
            if (caseDepth > 0) { caseDepth--; continue; }
            if (beginDepth > 0 && --beginDepth == 0) { rootEndedAt = index; break; }
        }

        if (rootEndedAt < 0)
            throw new InvalidDataException("The compound CREATE definition has no matching terminal END for its root BEGIN block.");
        var tail = tokens.Skip(rootEndedAt + 1).ToArray();
        if (tail.Length > 1 || tail.Length == 1 && tail[0] != ";")
            throw new InvalidDataException("An exact object definition must contain one CREATE statement; remove every statement after its root END block.");
    }

    private static List<string> TokenizeDefinition(string sql)
    {
        var tokens = new List<string>();
        for (var index = 0; index < sql.Length;)
        {
            var character = sql[index];
            if (char.IsWhiteSpace(character)) { index++; continue; }
            if (character == '#' || character == '-' && index + 2 < sql.Length && sql[index + 1] == '-' && char.IsWhiteSpace(sql[index + 2]))
            {
                index = sql.IndexOf('\n', index) is var lineEnd && lineEnd >= 0 ? lineEnd + 1 : sql.Length; continue;
            }
            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                if (index + 2 < sql.Length && sql[index + 2] is '!' or '+')
                    throw new InvalidDataException("Executable or optimizer-hint block comments are not accepted in exact object definitions.");
                var blockEnd = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (blockEnd < 0) throw new InvalidDataException("The exact object definition contains an unterminated block comment.");
                index = blockEnd + 2; continue;
            }
            if (character is '\'' or '"' or '`')
            {
                var quote = character; index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (sql[index] == '\\' && quote != '`' && index + 1 < sql.Length) { index += 2; continue; }
                    if (sql[index] != quote) { index++; continue; }
                    if (index + 1 < sql.Length && sql[index + 1] == quote) { index += 2; continue; }
                    index++; closed = true; break;
                }
                if (!closed) throw new InvalidDataException("The exact object definition contains an unterminated quoted value or identifier.");
                continue;
            }
            if (character == ';') { tokens.Add(";"); index++; continue; }
            if (char.IsLetter(character) || character == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$')) index++;
                tokens.Add(sql[start..index].ToUpperInvariant()); continue;
            }
            index++;
        }
        return tokens;
    }

    private async Task<SqlDatabaseObjectDefinition?> ReadExactAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectType type, string name,
        CancellationToken cancellationToken)
    {
        var item = (await ListAsync(profile, cancellationToken)).FirstOrDefault(candidate => candidate.Type == type && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return item is null ? null : await ShowCreateAsync(profile, item, cancellationToken);
    }

    private static SqlDatabaseObjectChangeReceipt Receipt(string state, DatabaseConnectionProfile profile, SqlDatabaseObjectChangePlan plan,
        string? after, string? afterSha, string? failure, DateTimeOffset? rolledBackUtc)
    {
        var receipt = new SqlDatabaseObjectChangeReceipt(ChangeReceiptFormat, state, DateTimeOffset.UtcNow, rolledBackUtc, profile.Host, profile.Port,
            profile.Database, plan.Type, plan.Name, plan.DesiredCreateSql, plan.ExpectedCreateSql, plan.ExpectedCreateSqlSha256, after, afterSha, failure, string.Empty);
        return receipt with { ContentSha256 = ReceiptHash(receipt) };
    }

    private static void ValidateReceipt(SqlDatabaseObjectChangeReceipt receipt)
    {
        if (!receipt.Format.Equals(ChangeReceiptFormat, StringComparison.Ordinal)) throw new InvalidDataException($"Unsupported SQL object receipt format {receipt.Format}.");
        if (!ReceiptHash(receipt).Equals(receipt.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQL object receipt content hash is invalid.");
        ValidateName(receipt.Database); ValidateName(receipt.Name);
        if (receipt.BeforeCreateSql is not null && !Sha256(receipt.BeforeCreateSql).Equals(receipt.BeforeCreateSqlSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQL object receipt preimage hash is invalid.");
        if (receipt.AfterCreateSql is not null && !Sha256(receipt.AfterCreateSql).Equals(receipt.AfterCreateSqlSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQL object receipt postimage hash is invalid.");
    }

    private static string ReceiptHash(SqlDatabaseObjectChangeReceipt receipt)
    {
        var identity = new { receipt.Format, receipt.State, receipt.AppliedUtc, receipt.RolledBackUtc, receipt.Host, receipt.Port, receipt.Database, receipt.Type, receipt.Name, receipt.DesiredCreateSql, receipt.BeforeCreateSql, receipt.BeforeCreateSqlSha256, receipt.AfterCreateSql, receipt.AfterCreateSqlSha256, receipt.Failure };
        return Sha256(JsonSerializer.Serialize(identity));
    }

    private static void ValidateChangeTarget(DatabaseConnectionProfile profile, SqlDatabaseObjectChangePlan plan)
    {
        if (!profile.Host.Equals(plan.Host, StringComparison.OrdinalIgnoreCase) || profile.Port != plan.Port || !profile.Database.Equals(plan.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The reviewed SQL object plan belongs to a different database target.");
    }

    private static string AllocateReceiptPath(string name)
    {
        Directory.CreateDirectory(CruciblePaths.SqlSchemaBackupDirectory); var safe = string.Concat(name.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return Path.Combine(CruciblePaths.SqlSchemaBackupDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{safe}-{Guid.NewGuid():N}.crucible-sql-object.json");
    }

    private static void WriteArtifact(string path, object artifact, bool replace)
    {
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(artifact, artifact.GetType(), ArtifactJson) + Environment.NewLine, new UTF8Encoding(false)); File.Move(temporary, path, replace); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Unquote(string value)
    {
        value = value.Trim(); return value.Length >= 2 && value[0] == '`' && value[^1] == '`' ? value[1..^1].Replace("``", "`", StringComparison.Ordinal) : value;
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task ReadAsync(MySqlConnection connection, string sql, string database, Func<MySqlDataReader, SqlDatabaseObjectInfo> map,
        ICollection<SqlDatabaseObjectInfo> output, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@database", database); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) output.Add(map(reader));
    }
    private static DateTime? NullableDateTime(MySqlDataReader reader, int index) => reader.IsDBNull(index) ? null : Convert.ToDateTime(reader.GetValue(index), CultureInfo.InvariantCulture);
    private static string EventDetails(MySqlDataReader reader)
    {
        if (reader.GetString(2).Equals("ONE TIME", StringComparison.OrdinalIgnoreCase)) return reader.IsDBNull(7) ? "one time" : $"one time at {reader.GetValue(7)}";
        var value = reader.IsDBNull(8) ? "?" : reader.GetString(8); var field = reader.IsDBNull(9) ? "?" : reader.GetString(9); return $"recurring every {value} {field}";
    }
    private static void ValidateTarget(DatabaseConnectionProfile profile, SqlDatabaseObjectInfo item)
    {
        if (!profile.Database.Equals(item.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"The selected object belongs to {item.Database}, not the connected schema {profile.Database}."); ValidateName(item.Name);
    }
    private static string Keyword(SqlDatabaseObjectType type) => type switch { SqlDatabaseObjectType.View => "VIEW", SqlDatabaseObjectType.Trigger => "TRIGGER", SqlDatabaseObjectType.Procedure => "PROCEDURE", SqlDatabaseObjectType.Function => "FUNCTION", SqlDatabaseObjectType.Event => "EVENT", _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static string Qualified(string database, string name) => $"{ItemWritePlan.QuoteIdentifier(ValidateName(database))}.{ItemWritePlan.QuoteIdentifier(ValidateName(name))}";
    private static string ValidateName(string value) { value = value.Trim(); if (value.Length is < 1 or > 64 || value.Any(character => char.IsControl(character) || character is '\0' or ';')) throw new ArgumentException("Database object names must contain 1–64 non-control characters without statement delimiters."); return value; }
    private static string FirstToken(string sql) { var text = sql.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'); while (text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith('#') || text.StartsWith("/*", StringComparison.Ordinal)) { if (text.StartsWith("/*", StringComparison.Ordinal)) { var end = text.IndexOf("*/", StringComparison.Ordinal); if (end < 0) return string.Empty; text = text[(end + 2)..].TrimStart(); } else { var end = text.IndexOf('\n'); if (end < 0) return string.Empty; text = text[(end + 1)..].TrimStart(); } } return new string(text.TakeWhile(char.IsLetter).ToArray()); }
    private static async Task ExecuteDdlAsync(DatabaseConnectionProfile profile, string sql, CancellationToken cancellationToken) { await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken); await using var command = new MySqlCommand(sql, connection); await command.ExecuteNonQueryAsync(cancellationToken); }
}
