using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum SqlTableDesignOperation
{
    AddColumn,
    ModifyColumn,
    RenameColumn,
    DropColumn,
    CloneStructure,
    RenameTable
}

public enum SqlColumnPlacement
{
    End,
    First,
    After
}

public sealed record SqlTableColumnDefinition(string Name, string Definition, int Ordinal, string Key, string Extra)
{
    public string Display => $"{Ordinal:N0} · {Name} · {Definition}{(string.IsNullOrWhiteSpace(Key) ? string.Empty : $" · {Key}")}";
}

public sealed record SqlTableDesignSnapshot(
    string Database,
    string Table,
    string CreateSql,
    string CreateSqlSha256,
    IReadOnlyList<SqlTableColumnDefinition> Columns,
    IReadOnlyList<SqlIndexInfo> Indexes,
    IReadOnlyList<DatabaseRelationCapability> Relations);

public sealed record SqlTableDesignRequest(
    SqlTableDesignOperation Operation,
    string? ColumnName = null,
    string? NewName = null,
    string? Definition = null,
    SqlColumnPlacement Placement = SqlColumnPlacement.End,
    string? AfterColumn = null);

public sealed record SqlTableDesignPlan(
    string Format,
    DateTimeOffset PreparedUtc,
    string Host,
    uint Port,
    string Database,
    string SourceTable,
    string ResultTable,
    string ExpectedCreateSqlSha256,
    SqlTableDesignRequest Request,
    string Sql,
    bool Destructive,
    IReadOnlyList<string> Warnings);

public sealed record SqlTableDesignReceipt(
    string Format,
    DateTimeOffset AppliedUtc,
    string Host,
    uint Port,
    string Database,
    string SourceTable,
    string ResultTable,
    SqlTableDesignOperation Operation,
    string Sql,
    string BeforeCreateSql,
    string BeforeCreateSqlSha256,
    string AfterCreateSql,
    string AfterCreateSqlSha256,
    IReadOnlyList<string> Warnings);

public sealed record SqlTableDesignPendingReceipt(
    string Format,
    string State,
    DateTimeOffset CreatedUtc,
    string Host,
    uint Port,
    string Database,
    string SourceTable,
    string ResultTable,
    SqlTableDesignOperation Operation,
    string Sql,
    string BeforeCreateSql,
    string BeforeCreateSqlSha256,
    IReadOnlyList<string> Warnings);

public sealed record SqlTableDesignApplyResult(SqlTableDesignReceipt Receipt, string ReceiptPath);

public sealed class SqlTableDesignerService
{
    public const string PlanFormat = "wow-crucible-sql-table-design-plan-v1";
    public const string ReceiptFormat = "wow-crucible-sql-table-design-receipt-v1";

    public async Task<SqlTableDesignSnapshot> InspectAsync(DatabaseConnectionProfile profile, string tableName,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var table = capabilities.FindTable(tableName) ?? throw new KeyNotFoundException($"{profile.Database} has no table {tableName}.");
        var administration = new SqlAdministrationService();
        var createSql = await administration.ShowCreateTableAsync(profile, table, cancellationToken);
        var columns = ParseColumns(createSql, table);
        var indexes = await administration.ReadIndexesAsync(profile, table, cancellationToken);
        var relations = capabilities.Relationships.Where(relation => relation.Declared && relation.Touches(table.Name)).ToArray();
        return new(profile.Database, table.Name, createSql, Sha256(createSql), columns, indexes, relations);
    }

    public async Task<SqlTableDesignPlan> PrepareAsync(DatabaseConnectionProfile profile, string tableName,
        SqlTableDesignRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = await InspectAsync(profile, tableName, cancellationToken);
        return Prepare(profile, snapshot, request);
    }

    public SqlTableDesignPlan Prepare(DatabaseConnectionProfile profile, SqlTableDesignSnapshot snapshot, SqlTableDesignRequest request)
    {
        if (!profile.Database.Equals(snapshot.Database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The inspected table snapshot belongs to a different database.");
        var source = ValidateIdentifier(snapshot.Table, "table");
        var warnings = new List<string> { "MySQL DDL may implicitly commit and cannot be promised transaction rollback." };
        var destructive = false;
        string resultTable = source;
        string sql;
        switch (request.Operation)
        {
            case SqlTableDesignOperation.AddColumn:
            {
                var name = ValidateNewColumn(snapshot, request.NewName ?? request.ColumnName);
                var definition = ValidateDefinition(request.Definition);
                sql = $"ALTER TABLE {Quote(source)} ADD COLUMN {Quote(name)} {definition}{Placement(snapshot, request, name)};";
                break;
            }
            case SqlTableDesignOperation.ModifyColumn:
            {
                var column = ExistingColumn(snapshot, request.ColumnName);
                var definition = ValidateDefinition(request.Definition);
                sql = $"ALTER TABLE {Quote(source)} MODIFY COLUMN {Quote(column.Name)} {definition}{Placement(snapshot, request, column.Name)};";
                warnings.Add("Changing a column type, nullability, generated expression, or default can rewrite the table and may reject or transform existing values.");
                destructive = true;
                break;
            }
            case SqlTableDesignOperation.RenameColumn:
            {
                var column = ExistingColumn(snapshot, request.ColumnName);
                var name = ValidateNewColumn(snapshot, request.NewName, column.Name);
                var definition = ValidateDefinition(request.Definition);
                sql = $"ALTER TABLE {Quote(source)} CHANGE COLUMN {Quote(column.Name)} {Quote(name)} {definition}{Placement(snapshot, request, name)};";
                warnings.Add("Code, scripts, views, triggers, and undeclared application relationships may still refer to the old column name.");
                destructive = true;
                AddDependencyWarnings(snapshot, column.Name, warnings);
                break;
            }
            case SqlTableDesignOperation.DropColumn:
            {
                var column = ExistingColumn(snapshot, request.ColumnName);
                sql = $"ALTER TABLE {Quote(source)} DROP COLUMN {Quote(column.Name)};";
                destructive = true;
                warnings.Add("DROP COLUMN permanently deletes every value in this column. The automatic receipt preserves DDL, not row data.");
                AddDependencyWarnings(snapshot, column.Name, warnings);
                break;
            }
            case SqlTableDesignOperation.CloneStructure:
            {
                resultTable = ValidateIdentifier(request.NewName, "new table");
                if (resultTable.Equals(source, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The cloned table needs a different name.");
                sql = $"CREATE TABLE {Quote(resultTable)} LIKE {Quote(source)};";
                warnings.Add("CREATE TABLE ... LIKE copies table structure and indexes but not rows, triggers, views, or foreign-key relationships.");
                break;
            }
            case SqlTableDesignOperation.RenameTable:
            {
                resultTable = ValidateIdentifier(request.NewName, "new table");
                if (resultTable.Equals(source, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The renamed table needs a different name.");
                sql = $"RENAME TABLE {Quote(source)} TO {Quote(resultTable)};";
                destructive = true;
                warnings.Add("Code, scripts, views, triggers, and undeclared application relationships may still refer to the old table name.");
                foreach (var relation in snapshot.Relations) warnings.Add($"Declared relationship {relation.Name}: {relation.FromTable}.{relation.FromColumn} → {relation.ToTable}.{relation.ToColumn}.");
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(request));
        }
        return new(PlanFormat, DateTimeOffset.UtcNow, profile.Host, profile.Port, profile.Database, source, resultTable,
            snapshot.CreateSqlSha256, request, sql, destructive, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<SqlTableDesignApplyResult> ApplyAsync(DatabaseConnectionProfile profile, SqlTableDesignPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (!plan.Format.Equals(PlanFormat, StringComparison.Ordinal)) throw new InvalidDataException($"Unsupported SQL table-design plan format {plan.Format}.");
        ValidateTarget(profile, plan);
        var before = await InspectAsync(profile, plan.SourceTable, cancellationToken);
        if (!before.CreateSqlSha256.Equals(plan.ExpectedCreateSqlSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{profile.Database}.{plan.SourceTable} changed after review. Reload the table designer and prepare a new plan.");
        if (plan.Request.Operation is SqlTableDesignOperation.CloneStructure or SqlTableDesignOperation.RenameTable)
        {
            var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
            if (capabilities.FindTable(plan.ResultTable) is not null) throw new InvalidOperationException($"Target table {plan.ResultTable} now exists; no DDL was executed.");
        }

        var receiptPath = AllocateReceiptPath(plan.ResultTable);
        var pending = new SqlTableDesignPendingReceipt("wow-crucible-sql-table-design-pending-v1", "PREIMAGE_SAVED_DDL_NOT_VERIFIED",
            DateTimeOffset.UtcNow, profile.Host, profile.Port, profile.Database, plan.SourceTable, plan.ResultTable,
            plan.Request.Operation, plan.Sql, before.CreateSql, before.CreateSqlSha256, plan.Warnings);
        WriteArtifact(receiptPath, pending, replace: false);

        await using (var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand(plan.Sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = await InspectAsync(profile, plan.ResultTable, cancellationToken);
        var receipt = new SqlTableDesignReceipt(ReceiptFormat, DateTimeOffset.UtcNow, profile.Host, profile.Port, profile.Database,
            plan.SourceTable, plan.ResultTable, plan.Request.Operation, plan.Sql, before.CreateSql, before.CreateSqlSha256,
            after.CreateSql, after.CreateSqlSha256, plan.Warnings);
        WriteArtifact(receiptPath, receipt, replace: true);
        return new(receipt, receiptPath);
    }

    public static IReadOnlyList<SqlTableColumnDefinition> ParseColumns(string createSql, DatabaseTableCapability table)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in createSql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (!line.StartsWith('`')) continue;
            var close = FindIdentifierEnd(line);
            if (close < 1) continue;
            var name = line[1..close].Replace("``", "`", StringComparison.Ordinal);
            var definition = line[(close + 1)..].Trim();
            if (definition.EndsWith(',')) definition = definition[..^1].TrimEnd();
            parsed[name] = definition;
        }
        var missing = table.Columns.Where(column => !parsed.ContainsKey(column.Name)).Select(column => column.Name).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"SHOW CREATE TABLE could not be mapped to live column(s): {string.Join(", ", missing)}. Crucible will not offer a lossy structure edit.");
        return table.Columns.OrderBy(column => column.Ordinal)
            .Select(column => new SqlTableColumnDefinition(column.Name, parsed[column.Name], column.Ordinal, column.Key, column.Extra)).ToArray();
    }

    public static string ValidateDefinition(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 8192) throw new ArgumentException("A complete column definition must contain 1–8,192 characters after the column name.");
        var depth = 0; char quote = '\0'; var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (character == '\\' && quote != '`') { escaped = true; continue; }
                if (character == quote)
                {
                    if (index + 1 < value.Length && value[index + 1] == quote) { index++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (character is '\'' or '"' or '`') { quote = character; continue; }
            if (character == '(') { depth++; continue; }
            if (character == ')') { if (--depth < 0) throw new ArgumentException("The column definition has an unmatched closing parenthesis."); continue; }
            if (character == ';') throw new ArgumentException("A column definition cannot contain a statement delimiter.");
            if (character == ',' && depth == 0) throw new ArgumentException("A column definition cannot inject another top-level ALTER clause.");
            if (character == '#' || (character == '-' && index + 1 < value.Length && value[index + 1] == '-') || (character == '/' && index + 1 < value.Length && value[index + 1] == '*'))
                throw new ArgumentException("SQL comments are not allowed inside a guided column definition.");
            if (char.IsControl(character) && character is not '\t' and not '\r' and not '\n') throw new ArgumentException("The column definition contains a control character.");
        }
        if (quote != '\0' || depth != 0) throw new ArgumentException("The column definition has an unterminated quote or unbalanced parentheses.");
        return value;
    }

    private static string Placement(SqlTableDesignSnapshot snapshot, SqlTableDesignRequest request, string editedColumn)
    {
        return request.Placement switch
        {
            SqlColumnPlacement.End => string.Empty,
            SqlColumnPlacement.First => " FIRST",
            SqlColumnPlacement.After => $" AFTER {Quote(ExistingColumn(snapshot, request.AfterColumn, editedColumn).Name)}",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    private static SqlTableColumnDefinition ExistingColumn(SqlTableDesignSnapshot snapshot, string? name, string? excluded = null)
    {
        name = name?.Trim();
        var column = snapshot.Columns.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (column is null || (excluded is not null && column.Name.Equals(excluded, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Select an existing column{(excluded is null ? string.Empty : " other than the edited column")}.");
        return column;
    }

    private static string ValidateNewColumn(SqlTableDesignSnapshot snapshot, string? name, string? except = null)
    {
        name = ValidateIdentifier(name, "column");
        if (snapshot.Columns.Any(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !column.Name.Equals(except, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Column {name} already exists on {snapshot.Table}.");
        return name;
    }

    private static string ValidateIdentifier(string? value, string label)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 64 || value.Any(character => char.IsControl(character) || character == '\0'))
            throw new ArgumentException($"The {label} name must contain 1–64 non-control characters.");
        return value;
    }

    private static void AddDependencyWarnings(SqlTableDesignSnapshot snapshot, string column, ICollection<string> warnings)
    {
        foreach (var index in snapshot.Indexes.Where(index => index.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))) warnings.Add($"Index {index.Name} includes {column}.");
        foreach (var relation in snapshot.Relations.Where(relation =>
                     relation.FromTable.Equals(snapshot.Table, StringComparison.OrdinalIgnoreCase) && relation.FromColumn.Equals(column, StringComparison.OrdinalIgnoreCase) ||
                     relation.ToTable.Equals(snapshot.Table, StringComparison.OrdinalIgnoreCase) && relation.ToColumn.Equals(column, StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"Declared relationship {relation.Name}: {relation.FromTable}.{relation.FromColumn} → {relation.ToTable}.{relation.ToColumn}.");
    }

    private static int FindIdentifierEnd(string line)
    {
        for (var index = 1; index < line.Length; index++)
        {
            if (line[index] != '`') continue;
            if (index + 1 < line.Length && line[index + 1] == '`') { index++; continue; }
            return index;
        }
        return -1;
    }

    private static void ValidateTarget(DatabaseConnectionProfile profile, SqlTableDesignPlan plan)
    {
        if (!profile.Host.Equals(plan.Host, StringComparison.OrdinalIgnoreCase) || profile.Port != plan.Port || !profile.Database.Equals(plan.Database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The reviewed DDL plan is bound to a different database target.");
    }

    private static string AllocateReceiptPath(string resultTable)
    {
        Directory.CreateDirectory(CruciblePaths.SqlSchemaBackupDirectory);
        var safe = string.Concat(resultTable.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return Path.Combine(CruciblePaths.SqlSchemaBackupDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{safe}-{Guid.NewGuid():N}.crucible-sql-schema.json");
    }

    private static void WriteArtifact(string path, object artifact, bool replace)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(artifact, artifact.GetType(), new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, path, replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Quote(string name) => ItemWritePlan.QuoteIdentifier(name);
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
