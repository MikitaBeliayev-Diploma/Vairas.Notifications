using Npgsql;

namespace Vairas.Notifications.Persistance;

/// <summary>Explicit migration-owner maintenance. Keep semantic receipts; remove terminal message content only.</summary>
public static class ContentRetention
{
    public static async Task<int> RunAsync(string ownerConnection, DateTimeOffset cutoff, string reason, bool apply, CancellationToken ct = default)
    {
        if (cutoff > DateTimeOffset.UtcNow || cutoff.Year < 2000 || string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ArgumentException("Supply an explicit past UTC cutoff and maintenance reason.");
        await using var connection = new NpgsqlConnection(ownerConnection); await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var check = new NpgsqlCommand("SELECT relowner=(SELECT oid FROM pg_roles WHERE rolname=current_user) FROM pg_class WHERE oid='notifications.notification'::regclass", connection, transaction))
            if (await check.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Content retention requires the table-owning migration login.");
        await using (var setup = new NpgsqlCommand("SET LOCAL lock_timeout='5s'; SELECT pg_advisory_xact_lock(739215689); ALTER TABLE notifications.notification NO FORCE ROW LEVEL SECURITY", connection, transaction))
            await setup.ExecuteNonQueryAsync(ct);
        var predicate = "status IN ('Sent','Failed','Cancelled') AND updated_at<@cutoff AND content_purged_at IS NULL";
        if (!apply)
        {
            await using var count = new NpgsqlCommand("SELECT least(count(*),1000)::int FROM notifications.notification WHERE " + predicate, connection, transaction);
            count.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime()); return (int)(await count.ExecuteScalarAsync(ct))!; // Disposal rolls back the temporary owner-only RLS change.
        }
        await using (var disable = new NpgsqlCommand("ALTER TABLE notifications.notification DISABLE TRIGGER n_guard", connection, transaction)) await disable.ExecuteNonQueryAsync(ct);
        await using var purge = new NpgsqlCommand("""
            WITH batch AS (SELECT school_id,notification_id FROM notifications.notification
              WHERE status IN ('Sent','Failed','Cancelled') AND updated_at<@cutoff AND content_purged_at IS NULL
              ORDER BY updated_at,school_id,notification_id LIMIT 1000)
            UPDATE notifications.notification n SET subject='[retained receipt]',body='[content removed by retention]',destination_address='removed@invalid.test',content_purged_at=clock_timestamp()
            FROM batch b WHERE n.school_id=b.school_id AND n.notification_id=b.notification_id
            """, connection, transaction);
        purge.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime()); var affected = await purge.ExecuteNonQueryAsync(ct);
        await using (var restore = new NpgsqlCommand("ALTER TABLE notifications.notification ENABLE TRIGGER n_guard; ALTER TABLE notifications.notification FORCE ROW LEVEL SECURITY", connection, transaction)) await restore.ExecuteNonQueryAsync(ct);
        await using var record = new NpgsqlCommand("INSERT INTO notifications.notification_retention_run VALUES(@id,@cutoff,@affected,clock_timestamp(),current_user,@reason)", connection, transaction);
        record.Parameters.AddWithValue("id", Guid.NewGuid()); record.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime()); record.Parameters.AddWithValue("affected", affected); record.Parameters.AddWithValue("reason", reason);
        await record.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct); return affected;
    }
}
