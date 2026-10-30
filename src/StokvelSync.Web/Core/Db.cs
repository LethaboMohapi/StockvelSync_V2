using MySqlConnector;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StokvelSync.Web.Core;

public sealed class Row : Dictionary<string, object?>
{
    public string Text(string key) => Convert.ToString(this.GetValueOrDefault(key), CultureInfo.InvariantCulture) ?? "";
    public long Number(string key) => Convert.ToInt64(this.GetValueOrDefault(key) ?? 0, CultureInfo.InvariantCulture);
    public bool Flag(string key) => Number(key) != 0;
}
public sealed class Db(IConfiguration config)
{
    public string ConnectionString => config.GetConnectionString("MySql") ?? throw new InvalidOperationException("MySQL connection string is required.");
    public async Task<MySqlConnection> Open()
    {
        var c = new MySqlConnection(ConnectionString); await c.OpenAsync(); return c;
    }
    static MySqlCommand Command(string sql, object?[] values, MySqlConnection c, MySqlTransaction? tx)
    {
        var i = 0; var text = Regex.Replace(sql, @"\?", _ => "@p" + i++);
        if (i != values.Length) throw new InvalidOperationException("SQL parameter count mismatch.");
        var command = new MySqlCommand(text, c, tx);
        for (var n = 0; n < values.Length; n++) command.Parameters.AddWithValue("@p" + n, values[n] ?? DBNull.Value);
        return command;
    }
    public async Task<List<Row>> Rows(string sql, object?[]? values = null, MySqlConnection? c = null, MySqlTransaction? tx = null)
    {
        var own = c is null; c ??= await Open();
        try
        {
            await using var command = Command(sql, values ?? [], c, tx); await using var reader = await command.ExecuteReaderAsync(); var rows = new List<Row>();
            while (await reader.ReadAsync())
            {
                var row = new Row();
                for (var n = 0; n < reader.FieldCount; n++)
                {
                    object? value = reader.IsDBNull(n) ? null : reader.GetValue(n);
                    if (value is DateTime date) value = reader.GetDataTypeName(n).Equals("DATE", StringComparison.OrdinalIgnoreCase) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString("O");
                    row[reader.GetName(n)] = value;
                }
                rows.Add(row);
            }
            return rows;
        }
        finally { if (own) await c.DisposeAsync(); }
    }
    public async Task<int> Exec(string sql, object?[]? values = null, MySqlConnection? c = null, MySqlTransaction? tx = null)
    {
        var own = c is null; c ??= await Open();
        try { await using var command = Command(sql, values ?? [], c, tx); return await command.ExecuteNonQueryAsync(); }
        finally { if (own) await c.DisposeAsync(); }
    }
    public async Task<T> Transaction<T>(Func<MySqlConnection, MySqlTransaction, Task<T>> fn)
    {
        await using var c = await Open(); await using var tx = await c.BeginTransactionAsync();
        try { var result = await fn(c, tx); await tx.CommitAsync(); return result; }
        catch { await tx.RollbackAsync(); throw; }
    }
    public async Task Migrate(string root)
    {
        await using var c = await Open();
        var acquired = (await Rows("SELECT GET_LOCK('stokvelsync_migrations',30) AS acquired", c: c)).Single().Number("acquired");
        if (acquired != 1) throw new InvalidOperationException("Migration lock could not be acquired.");
        try
        {
            await Exec("CREATE TABLE IF NOT EXISTS schema_migrations(version VARCHAR(50) PRIMARY KEY,applied_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP)", c: c);
            foreach (var file in Directory.GetFiles(Path.Combine(root, "Database"), "*.sql").Order())
            {
                var version = Path.GetFileNameWithoutExtension(file);
                if ((await Rows("SELECT version FROM schema_migrations WHERE version=?", [version], c)).Count != 0) continue;
                foreach (var sql in (await File.ReadAllTextAsync(file)).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) await Exec(sql, c: c);
                await Exec("INSERT INTO schema_migrations(version) VALUES (?)", [version], c);
            }
        }
        finally { await Rows("SELECT RELEASE_LOCK('stokvelsync_migrations')", c: c); }
    }
}
