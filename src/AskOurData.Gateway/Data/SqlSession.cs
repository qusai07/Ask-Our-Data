using System.Data;
using Microsoft.Data.SqlClient;

namespace AskOurData.Gateway.Data;

/// <summary>
/// One database connection, locked down before any tool SQL runs:
///  1. (dev) EXECUTE AS USER = '&lt;least-privilege user&gt;' WITH NO REVERT on a NON-pooled connection,
///     so the session can never regain the service account's rights;
///  2. region scope set read-only in SESSION_CONTEXT for the row-level security policy.
/// </summary>
public sealed class SqlSession : IAsyncDisposable
{
    private readonly SqlConnection _conn;
    private readonly int _timeoutSeconds;

    private SqlSession(SqlConnection conn, int timeoutSeconds)
    {
        _conn = conn;
        _timeoutSeconds = timeoutSeconds;
    }

    public static async Task<SqlSession> OpenAsync(
        string connectionString, string? impersonateUser, string? regionScopeJson, int timeoutSeconds, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ApplicationName = "AskOurData.Gateway" };
        if (impersonateUser is not null) builder.Pooling = false;   // NO REVERT connections must never be reused
        var conn = new SqlConnection(builder.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            if (impersonateUser is not null)
            {
                // Must be an ad-hoc (parameterless) batch: SQL Server only allows NO REVERT outside sp_executesql.
                // impersonateUser is validated against ^[A-Za-z_][A-Za-z0-9_]*$ at startup (config, never caller input).
                await using var imp = conn.CreateCommand();
                imp.CommandTimeout = timeoutSeconds;
                imp.CommandText = $"EXECUTE AS USER = N'{impersonateUser}' WITH NO REVERT;";
                await imp.ExecuteNonQueryAsync(ct);
            }
            if (regionScopeJson is not null)
            {
                await using var scope = conn.CreateCommand();
                scope.CommandTimeout = timeoutSeconds;
                scope.CommandText = "EXEC sys.sp_set_session_context @key = N'region_scope', @value = @scope, @read_only = 1;";
                scope.Parameters.Add(new SqlParameter("@scope", SqlDbType.NVarChar, 400) { Value = regionScopeJson });
                await scope.ExecuteNonQueryAsync(ct);
            }
            return new SqlSession(conn, timeoutSeconds);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task<List<object?[]>> QueryAsync(
        string sql, IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<string> columns, CancellationToken ct)
    {
        await using var cmd = Command(sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);
        var ordinals = columns.Select(reader.GetOrdinal).ToArray();   // throws if the query and catalog disagree
        var isDate = ordinals.Select(o => reader.GetDataTypeName(o).Equals("date", StringComparison.OrdinalIgnoreCase)).ToArray();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[ordinals.Length];
            for (var i = 0; i < ordinals.Length; i++)
            {
                var v = reader.GetValue(ordinals[i]);
                row[i] = v switch
                {
                    DBNull => null,
                    DateTime dt when isDate[i] => DateOnly.FromDateTime(dt),
                    _ => v,
                };
            }
            rows.Add(row);
        }
        return rows;
    }

    public async Task<object?> ScalarAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        await using var cmd = Command(sql, parameters);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    public async Task<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        await using var cmd = Command(sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private SqlCommand Command(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = _timeoutSeconds;
        foreach (var (name, value) in parameters) cmd.Parameters.Add(ToParameter(name, value));
        return cmd;
    }

    private static SqlParameter ToParameter(string name, object? value) => value switch
    {
        null => new SqlParameter(name, SqlDbType.NVarChar, 4000) { Value = DBNull.Value },
        string s => new SqlParameter(name, SqlDbType.NVarChar, s.Length <= 4000 ? 4000 : -1) { Value = s },
        int i => new SqlParameter(name, SqlDbType.Int) { Value = i },
        long l when l is >= int.MinValue and <= int.MaxValue => new SqlParameter(name, SqlDbType.Int) { Value = (int)l },
        long l => new SqlParameter(name, SqlDbType.BigInt) { Value = l },
        DateOnly d => new SqlParameter(name, SqlDbType.Date) { Value = d.ToDateTime(TimeOnly.MinValue) },
        DateTime dt => new SqlParameter(name, SqlDbType.DateTime2) { Value = dt },
        decimal m => new SqlParameter(name, SqlDbType.Decimal) { Value = m, Precision = 18, Scale = 2 },
        bool b => new SqlParameter(name, SqlDbType.Bit) { Value = b },
        Guid g => new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = g },
        _ => throw new InvalidOperationException($"Unsupported SQL parameter type {value.GetType().Name} for {name}"),
    };

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}
