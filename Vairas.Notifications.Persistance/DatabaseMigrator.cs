using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Vairas.Notifications.Persistance;

/// <summary>Explicit, transactional, checksummed SQL migrations. Never invoked by normal API startup.</summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Execute("SELECT pg_advisory_xact_lock(739215689); CREATE SCHEMA IF NOT EXISTS notifications; SET LOCAL search_path=notifications,pg_catalog; " +
            "CREATE TABLE IF NOT EXISTS notifications.schema_history(version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())");
        var assembly = typeof(DatabaseMigrator).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(x => x.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);
            var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            await using var check = new NpgsqlCommand("SELECT checksum FROM notifications.schema_history WHERE version=@version", connection, transaction);
            check.Parameters.AddWithValue("version", resource);
            var previous = await check.ExecuteScalarAsync(ct);
            if (previous is string applied)
            {
                if (applied != checksum) throw new InvalidOperationException("An applied migration was modified.");
                continue;
            }
            await Execute(sql);
            await using var record = new NpgsqlCommand("INSERT INTO notifications.schema_history(version,checksum) VALUES(@version,@checksum)", connection, transaction);
            record.Parameters.AddWithValue("version", resource); record.Parameters.AddWithValue("checksum", checksum);
            await record.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);

        async Task Execute(string sql)
        { await using var command = new NpgsqlCommand(sql, connection, transaction); await command.ExecuteNonQueryAsync(ct); }
    }
}
