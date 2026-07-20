using System.Globalization;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public enum SqlTriggerTiming { Before, After }
public enum SqlTriggerEvent { Insert, Update, Delete }
public enum SqlRoutineParameterMode { In, Out, InOut }
public enum SqlRoutineDataAccess { NoSql, ContainsSql, ReadsSqlData, ModifiesSqlData }
public enum SqlSecurityMode { Definer, Invoker }
public enum SqlEventIntervalUnit { Second, Minute, Hour, Day, Week, Month, Quarter, Year }

public sealed record SqlRoutineParameter(string Name, string DataType, SqlRoutineParameterMode Mode = SqlRoutineParameterMode.In)
{
    public string Display => $"{Mode.ToString().ToUpperInvariant()} {Name} {DataType}";
}

/// <summary>
/// Beginner-facing database-object composition. Every result is handed back through
/// SqlDatabaseObjectService.ValidateCreateDefinition and the normal hash-bound
/// preimage/recovery workflow; this is never an independent DDL execution path.
/// </summary>
public static class SqlDatabaseObjectComposer
{
    private static readonly Regex SimpleParameter = new(@"^(?:(?<mode>INOUT|IN|OUT)\s+)?(?<name>[A-Z_][A-Z0-9_$]*)\s+(?<type>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DataType = new(
        @"^(?:TINYINT|SMALLINT|MEDIUMINT|INT|INTEGER|BIGINT|DECIMAL|NUMERIC|FLOAT|DOUBLE|REAL|BIT|BOOL|BOOLEAN|CHAR|VARCHAR|BINARY|VARBINARY|TINYBLOB|BLOB|MEDIUMBLOB|LONGBLOB|TINYTEXT|TEXT|MEDIUMTEXT|LONGTEXT|DATE|TIME|DATETIME|TIMESTAMP|YEAR|JSON)(?:\s*\(\s*\d+(?:\s*,\s*\d+)?\s*\))?(?:\s+(?:UNSIGNED|ZEROFILL))*(?:\s+CHARACTER\s+SET\s+[A-Z0-9_]+)?(?:\s+COLLATE\s+[A-Z0-9_]+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    public static IReadOnlyList<SqlRoutineParameter> ParseParameters(SqlDatabaseObjectType type, string? text)
    {
        if (type is not (SqlDatabaseObjectType.Procedure or SqlDatabaseObjectType.Function)) throw new ArgumentException("Only procedures and functions have guided parameters.");
        var result = new List<SqlRoutineParameter>();
        foreach (var sourceLine in (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = sourceLine.Trim(); if (line.Length == 0) continue;
            var match = SimpleParameter.Match(line); if (!match.Success) throw new InvalidDataException($"Invalid parameter line '{line}'. Use {(type == SqlDatabaseObjectType.Procedure ? "[IN|OUT|INOUT] name TYPE" : "name TYPE")}.");
            var mode = match.Groups["mode"].Success ? ParseMode(match.Groups["mode"].Value) : SqlRoutineParameterMode.In;
            if (type == SqlDatabaseObjectType.Function && mode != SqlRoutineParameterMode.In) throw new InvalidDataException("Function parameters cannot use OUT or INOUT mode.");
            var name = ValidateIdentifier(match.Groups["name"].Value, "parameter"); var dataType = ValidateDataType(match.Groups["type"].Value);
            if (result.Any(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"Parameter {name} is listed more than once.");
            result.Add(new(name, dataType, mode));
        }
        return result;
    }

    public static string BuildTrigger(string database, string name, string table, SqlTriggerTiming timing, SqlTriggerEvent triggerEvent, string body)
    {
        database = ValidateIdentifier(database, "database"); name = ValidateIdentifier(name, "trigger"); table = ValidateIdentifier(table, "trigger table");
        var sql = $"CREATE TRIGGER {Qualified(database, name)} {timing.ToString().ToUpperInvariant()} {triggerEvent.ToString().ToUpperInvariant()} ON {Qualified(database, table)} FOR EACH ROW\n{NormalizeBody(body)};";
        return SqlDatabaseObjectService.ValidateCreateDefinition(SqlDatabaseObjectType.Trigger, database, name, sql);
    }

    public static string BuildProcedure(string database, string name, IReadOnlyList<SqlRoutineParameter> parameters, SqlRoutineDataAccess dataAccess,
        SqlSecurityMode security, string body)
    {
        database = ValidateIdentifier(database, "database"); name = ValidateIdentifier(name, "procedure"); parameters = ValidateParameters(parameters, allowOutput: true);
        var sql = $"CREATE PROCEDURE {Qualified(database, name)}({string.Join(", ", parameters.Select(parameter => $"{ModeSql(parameter.Mode)} {Quote(parameter.Name)} {ValidateDataType(parameter.DataType)}"))})\n{DataAccessSql(dataAccess)}\nSQL SECURITY {security.ToString().ToUpperInvariant()}\n{NormalizeBody(body)};";
        return SqlDatabaseObjectService.ValidateCreateDefinition(SqlDatabaseObjectType.Procedure, database, name, sql);
    }

    public static string BuildFunction(string database, string name, IReadOnlyList<SqlRoutineParameter> parameters, string returnType, bool deterministic,
        SqlRoutineDataAccess dataAccess, SqlSecurityMode security, string body)
    {
        database = ValidateIdentifier(database, "database"); name = ValidateIdentifier(name, "function"); parameters = ValidateParameters(parameters, allowOutput: false); returnType = ValidateDataType(returnType);
        var sql = $"CREATE FUNCTION {Qualified(database, name)}({string.Join(", ", parameters.Select(parameter => $"{Quote(parameter.Name)} {ValidateDataType(parameter.DataType)}"))})\nRETURNS {returnType}\n{(deterministic ? "DETERMINISTIC" : "NOT DETERMINISTIC")}\n{DataAccessSql(dataAccess)}\nSQL SECURITY {security.ToString().ToUpperInvariant()}\n{NormalizeBody(body)};";
        return SqlDatabaseObjectService.ValidateCreateDefinition(SqlDatabaseObjectType.Function, database, name, sql);
    }

    public static string BuildRecurringEvent(string database, string name, int every, SqlEventIntervalUnit unit, string? starts, string? ends,
        bool preserve, bool enabled, string body)
    {
        database = ValidateIdentifier(database, "database"); name = ValidateIdentifier(name, "event"); if (every < 1) throw new ArgumentOutOfRangeException(nameof(every), "Recurring event interval must be at least one.");
        var schedule = $"EVERY {every.ToString(CultureInfo.InvariantCulture)} {unit.ToString().ToUpperInvariant()}" + TimestampClause(" STARTS ", starts) + TimestampClause(" ENDS ", ends);
        return BuildEvent(database, name, schedule, preserve, enabled, body);
    }

    public static string BuildOneTimeEvent(string database, string name, string executeAt, bool preserve, bool enabled, string body)
    {
        database = ValidateIdentifier(database, "database"); name = ValidateIdentifier(name, "event"); var schedule = "AT " + TimestampLiteral(executeAt, "execute-at");
        return BuildEvent(database, name, schedule, preserve, enabled, body);
    }

    private static string BuildEvent(string database, string name, string schedule, bool preserve, bool enabled, string body)
    {
        var sql = $"CREATE EVENT {Qualified(database, name)}\nON SCHEDULE {schedule}\nON COMPLETION {(preserve ? "PRESERVE" : "NOT PRESERVE")}\n{(enabled ? "ENABLE" : "DISABLE")}\nDO\n{NormalizeBody(body)};";
        return SqlDatabaseObjectService.ValidateCreateDefinition(SqlDatabaseObjectType.Event, database, name, sql);
    }

    private static IReadOnlyList<SqlRoutineParameter> ValidateParameters(IReadOnlyList<SqlRoutineParameter>? parameters, bool allowOutput)
    {
        var result = (parameters ?? []).Select(parameter => new SqlRoutineParameter(ValidateIdentifier(parameter.Name, "parameter"), ValidateDataType(parameter.DataType), parameter.Mode)).ToArray();
        if (!allowOutput && result.Any(parameter => parameter.Mode != SqlRoutineParameterMode.In)) throw new InvalidDataException("Function parameters cannot use OUT or INOUT mode.");
        if (result.Select(parameter => parameter.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length) throw new InvalidDataException("Routine parameter names must be unique.");
        return result;
    }

    private static string NormalizeBody(string body)
    {
        var value = (body ?? string.Empty).Trim().TrimStart('\uFEFF'); if (value.Length is < 1 or > 1_500_000) throw new ArgumentException("A guided object body must contain 1–1,500,000 characters.");
        if (Regex.IsMatch(value, @"(?im)^\s*DELIMITER\b")) throw new InvalidDataException("Remove mysql-client DELIMITER lines; they are not server SQL.");
        return value.TrimEnd().TrimEnd(';').TrimEnd();
    }

    private static string ValidateDataType(string value)
    {
        value = (value ?? string.Empty).Trim(); if (value.Length is < 1 or > 160 || !DataType.IsMatch(value))
            throw new InvalidDataException($"Unsupported guided SQL data type '{value}'. Use a standard scalar type such as INT UNSIGNED, DECIMAL(10,2), VARCHAR(255), DATETIME, TEXT, BLOB, or use the exact editor for specialized types.");
        return Regex.Replace(value, @"\s+", " ").ToUpperInvariant();
    }

    private static string ValidateIdentifier(string value, string label)
    {
        value = (value ?? string.Empty).Trim(); if (value.Length is < 1 or > 64 || value.Any(character => char.IsControl(character) || character is '\0' or ';')) throw new ArgumentException($"The guided {label} identifier must contain 1–64 non-control characters without statement delimiters."); return value;
    }

    private static string TimestampClause(string prefix, string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : prefix + TimestampLiteral(value, prefix.Trim());
    private static string TimestampLiteral(string value, string label)
    {
        if (!DateTime.TryParseExact(value.Trim(), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) throw new InvalidDataException($"Event {label} must use {TimestampFormat} in the database server's local time.");
        return $"'{parsed.ToString(TimestampFormat, CultureInfo.InvariantCulture)}'";
    }
    private static SqlRoutineParameterMode ParseMode(string value) => value.ToUpperInvariant() switch { "OUT" => SqlRoutineParameterMode.Out, "INOUT" => SqlRoutineParameterMode.InOut, _ => SqlRoutineParameterMode.In };
    private static string ModeSql(SqlRoutineParameterMode mode) => mode switch { SqlRoutineParameterMode.Out => "OUT", SqlRoutineParameterMode.InOut => "INOUT", _ => "IN" };
    private static string DataAccessSql(SqlRoutineDataAccess value) => value switch { SqlRoutineDataAccess.NoSql => "NO SQL", SqlRoutineDataAccess.ReadsSqlData => "READS SQL DATA", SqlRoutineDataAccess.ModifiesSqlData => "MODIFIES SQL DATA", _ => "CONTAINS SQL" };
    private static string Qualified(string database, string name) => $"{Quote(database)}.{Quote(name)}";
    private static string Quote(string value) => ItemWritePlan.QuoteIdentifier(value);
}
