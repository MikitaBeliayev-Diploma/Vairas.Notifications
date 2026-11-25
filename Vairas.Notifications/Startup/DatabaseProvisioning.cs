using Npgsql;

namespace Vairas.Notifications.Startup;

public static class DatabaseProvisioning
{
    public static async Task<string> ProvisionAsync(CancellationToken ct = default)
    {
        var admin = Environment.GetEnvironmentVariable("VAIRAS_PROVISION_CONNECTION") ?? throw new InvalidOperationException("Supply VAIRAS_PROVISION_CONNECTION.");
        await using var connection = new NpgsqlConnection(admin); await connection.OpenAsync(ct);
        foreach (var (role, variable) in new[] { ("v_notifications_migration", "VAIRAS_MIGRATION_PASSWORD"), ("v_notifications_runtime", "VAIRAS_RUNTIME_PASSWORD"), ("v_notifications_worker", "VAIRAS_WORKER_PASSWORD") })
        {
            var password = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(password) || password.Length < 20) throw new InvalidOperationException("Supply strong database credentials externally.");
            await using var exists = new NpgsqlCommand("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname=@role", connection);
            exists.Parameters.AddWithValue("role", role); var unsafeRole = await exists.ExecuteScalarAsync(ct);
            if (unsafeRole is true) throw new InvalidOperationException("A service role has unsafe privileges.");
            if (unsafeRole is not null) continue; // Never silently rotate a live service role's password.
            await using var quote = new NpgsqlCommand("SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS',@role,@password)", connection);
            quote.Parameters.AddWithValue("role", role); quote.Parameters.AddWithValue("password", password);
            await using var create = new NpgsqlCommand((string)(await quote.ExecuteScalarAsync(ct))!, connection); await create.ExecuteNonQueryAsync(ct);
        }
        await using (var grant = new NpgsqlCommand("GRANT v_notifications_migration TO CURRENT_USER", connection)) await grant.ExecuteNonQueryAsync(ct);
        await using var database = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname='vairas_notifications'", connection);
        var owner = await database.ExecuteScalarAsync(ct);
        if (owner is string name && name != "v_notifications_migration") throw new InvalidOperationException("Existing Notifications database has a different owner.");
        if (owner is null) { await using var create = new NpgsqlCommand("CREATE DATABASE vairas_notifications OWNER v_notifications_migration", connection); await create.ExecuteNonQueryAsync(ct); }
        await using (var grants = new NpgsqlCommand("REVOKE CONNECT,CREATE,TEMPORARY ON DATABASE vairas_notifications FROM PUBLIC; GRANT CONNECT ON DATABASE vairas_notifications TO v_notifications_migration,v_notifications_runtime,v_notifications_worker", connection)) await grants.ExecuteNonQueryAsync(ct);
        return new NpgsqlConnectionStringBuilder(admin) { Database = "vairas_notifications", Username = "v_notifications_migration",
            Password = Environment.GetEnvironmentVariable("VAIRAS_MIGRATION_PASSWORD"), Pooling = false }.ConnectionString;
    }
}
