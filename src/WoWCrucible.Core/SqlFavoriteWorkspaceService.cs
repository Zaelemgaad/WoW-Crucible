using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum SqlFavoriteVerificationState
{
    Unchecked,
    Live,
    Missing,
    SchemaMismatch,
    Error
}

public sealed record SqlFavoriteVerification(
    string Identity,
    SqlFavoriteVerificationState State,
    string Detail,
    DateTimeOffset CheckedUtc)
{
    public string Display => State switch
    {
        SqlFavoriteVerificationState.Live => "LIVE",
        SqlFavoriteVerificationState.Missing => "MISSING",
        SqlFavoriteVerificationState.SchemaMismatch => "SCHEMA CHANGED",
        SqlFavoriteVerificationState.Error => "CHECK FAILED",
        _ => "UNCHECKED"
    };
}

/// <summary>
/// Search and live validation for portable SQL row favorites. Validation is deliberately
/// primary-key exact and reuses one database connection per schema so a large review list
/// does not create one connection per row.
/// </summary>
public sealed class SqlFavoriteWorkspaceService
{
    public static bool Matches(SqlRowFavorite favorite, string? query)
    {
        ArgumentNullException.ThrowIfNull(favorite);
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;
        var searchable = string.Join('\n', new[]
        {
            favorite.Database,
            favorite.Table,
            favorite.Label,
            favorite.Notes,
            favorite.DbcPath ?? string.Empty,
            favorite.MpqPath ?? string.Empty,
            string.Join(' ', favorite.Key.Select(pair => $"{pair.Key}={pair.Value}"))
        });
        return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<SqlFavoriteVerification>> VerifyAsync(
        DatabaseConnectionProfile login,
        IReadOnlyList<SqlRowFavorite> favorites,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(favorites);
        var checkedUtc = DateTimeOffset.UtcNow;
        var output = new List<SqlFavoriteVerification>(favorites.Count);

        foreach (var databaseGroup in favorites.GroupBy(favorite => favorite.Database, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = databaseGroup.Key;
            if (string.IsNullOrWhiteSpace(database))
            {
                output.AddRange(databaseGroup.Select(favorite => Result(favorite, SqlFavoriteVerificationState.SchemaMismatch, "The favorite has no recorded database.", checkedUtc)));
                continue;
            }

            var profile = login with { Database = database };
            try
            {
                var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
                await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
                await connection.OpenAsync(cancellationToken);
                foreach (var favorite in databaseGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Add(await VerifyOneAsync(connection, capabilities, favorite, checkedUtc, cancellationToken));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                output.AddRange(databaseGroup.Select(favorite => Result(favorite, SqlFavoriteVerificationState.Error, $"Could not inspect database {database}: {exception.Message}", checkedUtc)));
            }
        }

        return output;
    }

    private static async Task<SqlFavoriteVerification> VerifyOneAsync(
        MySqlConnection connection,
        DatabaseCapabilities capabilities,
        SqlRowFavorite favorite,
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var table = capabilities.FindTable(favorite.Table);
            if (table is null) return Result(favorite, SqlFavoriteVerificationState.SchemaMismatch, $"Table {favorite.Table} no longer exists in {favorite.Database}.", checkedUtc);
            var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).ToArray();
            if (primary.Length == 0) return Result(favorite, SqlFavoriteVerificationState.SchemaMismatch, $"Table {favorite.Table} no longer has a primary key.", checkedUtc);
            if (favorite.Key.Count != primary.Length || primary.Any(column => !favorite.Key.ContainsKey(column.Name)))
                return Result(favorite, SqlFavoriteVerificationState.SchemaMismatch, $"The recorded key ({string.Join(", ", favorite.Key.Keys)}) no longer matches the complete primary key ({string.Join(", ", primary.Select(column => column.Name))}).", checkedUtc);

            var predicates = primary.Select((column, index) => $"{ItemWritePlan.QuoteIdentifier(column.Name)} <=> @k{index}").ToArray();
            await using var command = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {string.Join(" AND ", predicates)}", connection);
            for (var index = 0; index < primary.Length; index++)
                command.Parameters.AddWithValue($"@k{index}", ParseStoredKey(primary[index], favorite.Key[primary[index].Name]) ?? DBNull.Value);
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            return count switch
            {
                1 => Result(favorite, SqlFavoriteVerificationState.Live, "The complete recorded primary key resolves to exactly one live row.", checkedUtc),
                0 => Result(favorite, SqlFavoriteVerificationState.Missing, "No live row matches the complete recorded primary key.", checkedUtc),
                _ => Result(favorite, SqlFavoriteVerificationState.SchemaMismatch, $"The recorded primary key unexpectedly matched {count:N0} rows.", checkedUtc)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Result(favorite, SqlFavoriteVerificationState.Error, exception.Message, checkedUtc);
        }
    }

    public static object? ParseStoredKey(DatabaseColumnCapability column, string? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (value is null) return null;
        var type = column.DataType.ToLowerInvariant();
        if ((type.Contains("binary", StringComparison.Ordinal) || type.Contains("blob", StringComparison.Ordinal)) && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.FromHexString(value[2..]);
        return value;
    }

    private static SqlFavoriteVerification Result(SqlRowFavorite favorite, SqlFavoriteVerificationState state, string detail, DateTimeOffset checkedUtc)
        => new(favorite.Identity, state, detail, checkedUtc);
}
