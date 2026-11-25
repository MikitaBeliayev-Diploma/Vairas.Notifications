using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vairas.Notifications.Business;
using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Persistance;

public sealed class DeliveryStore(NpgsqlDataSource source) : IDeliveryStore
{
    public Task<IDeliveryTransaction> OpenAsync(Guid school, CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(() => OpenAsyncCore(school, ct));
    private async Task<IDeliveryTransaction> OpenAsyncCore(Guid school, CancellationToken ct)
    {
        if (school == Guid.Empty) throw NotificationErrors.Invalid();
        var connection = await source.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            var session = new Session(connection, transaction, school);
            await session.Initialize(ct); return session;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task<IReadOnlyList<WorkItem>> DiscoverAsync(int take, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT school_id,notification_id FROM notifications.notification_work WHERE available_at<=clock_timestamp() ORDER BY available_at,school_id,notification_id LIMIT @take");
        command.Parameters.AddWithValue("take", Math.Clamp(take, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<WorkItem>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetGuid(1)));
        return result;
    }
    public async Task<DeliveryStats> StatsAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT count(*) FILTER (WHERE status='Pending'), count(*) FILTER (WHERE status='DeliveryUnknown'), coalesce(extract(epoch FROM clock_timestamp()-min(available_at) FILTER (WHERE status='Pending' AND available_at<=clock_timestamp())),0)::float8 FROM notifications.notification_work");
        await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
        return new(reader.GetInt64(0), reader.GetInt64(1), Math.Max(0, reader.GetDouble(2)));
    }
    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid school) : IDeliveryTransaction
    {
        public async Task Initialize(CancellationToken ct)
        {
            await Execute("SELECT set_config('app.school_id',@school,true), set_config('lock_timeout','5s',true)", ct, ("school", school.ToString("D")));
            await Execute("SELECT pg_advisory_xact_lock(hashtextextended(@school,8))", ct, ("school", school.ToString("D")));
        }
        public async Task<DateTimeOffset> NowAsync(CancellationToken ct)
        { await using var command = Command("SELECT clock_timestamp()"); return new((DateTime)(await command.ExecuteScalarAsync(ct))!, TimeSpan.Zero); }
        public async Task<InboxReceipt?> InboxAsync(SourceMessage message, CancellationToken ct)
        {
            await using var command = Command("SELECT payload_hash,outcome,outcome_code FROM notifications.notification_inbox WHERE school_id=@s AND producer=@p AND event_id=@id",
                ("s", school), ("p", SourceMessagePolicy.Context(message.Producer)), ("id", message.EventId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
        }
        public Task RecordInboxAsync(SourceMessage message, string hash, string outcome, string code, CancellationToken ct) => Execute("""
            INSERT INTO notifications.notification_inbox(school_id,producer,event_id,payload_hash,source_id,source_revision,outcome,outcome_code,received_at,processed_at)
            VALUES(@s,@p,@id,@hash,@source,@revision,@outcome,@code,clock_timestamp(),clock_timestamp())
            """, ct, ("s", school), ("p", SourceMessagePolicy.Context(message.Producer)), ("id", message.EventId), ("hash", hash), ("source", message.SourceId),
            ("revision", message.SourceRevision), ("outcome", outcome), ("code", code));
        public async Task<IReadOnlyList<NotificationTemplate>> TemplatesAsync(CancellationToken ct)
        {
            await using var db = new NotificationDbContext(new DbContextOptionsBuilder<NotificationDbContext>()
                .UseNpgsql(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options);
            await db.Database.UseTransactionAsync(transaction, ct);
            return await DatabaseExceptionTranslator.GuardAsync(() => db.Set<NotificationTemplate>()
                .Where(x => x.SchoolId == school && x.Status == TemplateStatus.Active).ToArrayAsync(ct));
        }
        public Task CancelObsoleteAsync(string context, Guid sourceId, long revision, bool all, CancellationToken ct) => Execute("""
            UPDATE notifications.notification SET status='Cancelled',revision=revision+1,cancelled_at=clock_timestamp(),updated_at=clock_timestamp(),next_attempt_at=NULL
            WHERE school_id=@s AND source_context=@context AND source_id=@source AND status='Pending' AND (source_revision<@revision OR @all)
            """, ct, ("s", school), ("context", context), ("source", sourceId), ("revision", revision), ("all", all));
        public async Task InsertAsync(NotificationSnapshot snapshot, NotificationDelivery delivery, string correlationId, CancellationToken ct)
        {
            if (snapshot.SchoolId != school || delivery.Status != NotificationStatus.Pending || delivery.Revision != 1 || delivery.AttemptCount != 0) throw new InvalidOperationException("Invalid initial notification.");
            await using var command = Command("""
                INSERT INTO notifications.notification(school_id,notification_id,recipient_user_id,template_id,template_revision,notification_type,channel,subject,body,destination_address,
                    contact_revision,source_context,source_id,source_revision,source_event_id,dispatch_key,scheduled_at,status,revision,attempt_count,created_at,updated_at,correlation_id)
                VALUES(@s,@id,@user,@template,@templateRevision,@type,'Email',@subject,@body,@address,@contact,@context,@source,@sourceRevision,@event,@key,@due,'Pending',1,0,@created,@created,@correlation)
                ON CONFLICT(school_id,recipient_user_id,channel,dispatch_key) DO NOTHING
                """, ("s", school), ("id", snapshot.NotificationId), ("user", snapshot.RecipientUserId), ("type", snapshot.Type.ToString()), ("subject", snapshot.Subject), ("body", snapshot.Body),
                ("address", snapshot.DestinationAddress), ("contact", snapshot.ContactRevision), ("context", snapshot.SourceContext), ("source", snapshot.SourceId), ("sourceRevision", snapshot.SourceRevision),
                ("event", snapshot.SourceEventId), ("key", snapshot.DispatchKey), ("due", delivery.ScheduledAt), ("created", snapshot.CreatedAt), ("correlation", correlationId));
            Nullable(command, "template", NpgsqlDbType.Uuid, snapshot.TemplateId); Nullable(command, "templateRevision", NpgsqlDbType.Bigint, snapshot.TemplateRevision);
            await command.ExecuteNonQueryAsync(ct);
        }
        public async Task<DeliveryItem?> ReadAsync(Guid id, CancellationToken ct)
        {
            NotificationSnapshot snapshot; NotificationDelivery delivery;
            await using (var command = Command("""
                SELECT notification_id,recipient_user_id,template_id,template_revision,notification_type,subject,body,destination_address,contact_revision,source_context,
                    source_id,source_revision,source_event_id,dispatch_key,created_at,status,revision,attempt_count,scheduled_at,next_attempt_at,sent_at,cancelled_at,failure_code,updated_at,correlation_id
                FROM notifications.notification WHERE school_id=@s AND notification_id=@id
                """, ("s", school), ("id", id)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return null;
                snapshot = new(school, reader.GetGuid(0), reader.GetGuid(1), Value<Guid>(reader, 2), Value<long>(reader, 3), Enum.Parse<NotificationType>(reader.GetString(4)),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetGuid(10), reader.GetInt64(11), reader.GetGuid(12), reader.GetString(13), reader.GetFieldValue<DateTimeOffset>(14), reader.GetString(24));
                delivery = new(Enum.Parse<NotificationStatus>(reader.GetString(15)), reader.GetInt64(16), reader.GetInt32(17), reader.GetFieldValue<DateTimeOffset>(18),
                    Value<DateTimeOffset>(reader, 19), Value<DateTimeOffset>(reader, 20), Value<DateTimeOffset>(reader, 21), Text(reader, 22), reader.GetFieldValue<DateTimeOffset>(23));
            }
            await using var attempts = Command("SELECT attempt_id,attempt_no,provider_key,status,started_at,finished_at,provider_message_id,error_code FROM notifications.notification_delivery_attempt WHERE school_id=@s AND notification_id=@id ORDER BY attempt_no DESC LIMIT 1", ("s", school), ("id", id));
            await using var attemptReader = await attempts.ExecuteReaderAsync(ct);
            var attempt = await attemptReader.ReadAsync(ct) ? new DeliveryAttempt(attemptReader.GetGuid(0), id, attemptReader.GetInt32(1), attemptReader.GetString(2), Enum.Parse<AttemptStatus>(attemptReader.GetString(3)),
                attemptReader.GetFieldValue<DateTimeOffset>(4), Value<DateTimeOffset>(attemptReader, 5), Text(attemptReader, 6), Text(attemptReader, 7)) : null;
            return new(snapshot, delivery, attempt);
        }
        public async Task SaveAsync(DeliveryItem item, NotificationDelivery delivery, DeliveryAttempt? attempt, CancellationToken ct)
        {
            if (item.Snapshot.SchoolId != school || delivery.Revision != item.Delivery.Revision + 1) throw new InvalidOperationException("Invalid delivery revision.");
            await using var root = Command("""
                UPDATE notifications.notification SET status=@status,revision=@revision,attempt_count=@count,next_attempt_at=@next,sent_at=@sent,cancelled_at=@cancelled,failure_code=@failure,updated_at=@updated
                WHERE school_id=@s AND notification_id=@id AND revision=@expected AND status=@before
                """, ("s", school), ("id", item.Snapshot.NotificationId), ("status", delivery.Status.ToString()), ("revision", delivery.Revision), ("count", delivery.AttemptCount),
                ("updated", delivery.UpdatedAt), ("expected", item.Delivery.Revision), ("before", item.Delivery.Status.ToString()));
            Nullable(root, "next", NpgsqlDbType.TimestampTz, delivery.NextAttemptAt); Nullable(root, "sent", NpgsqlDbType.TimestampTz, delivery.SentAt);
            Nullable(root, "cancelled", NpgsqlDbType.TimestampTz, delivery.CancelledAt); Nullable(root, "failure", NpgsqlDbType.Varchar, delivery.FailureCode);
            if (await root.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("DELIVERY_CHANGED");
            if (attempt is null)
            { if (delivery.Status != NotificationStatus.Cancelled) throw new InvalidOperationException("Missing attempt."); return; }
            if (attempt.NotificationId != item.Snapshot.NotificationId || attempt.Number != delivery.AttemptCount) throw new InvalidOperationException("Attempt binding mismatch.");
            var insert = delivery.Status == NotificationStatus.Sending;
            if (insert && (attempt.Status != AttemptStatus.Started || attempt.Number != item.Delivery.AttemptCount + 1)) throw new InvalidOperationException("Invalid claim.");
            await using var command = Command(insert ? """
                INSERT INTO notifications.notification_delivery_attempt(school_id,attempt_id,notification_id,attempt_no,provider_key,status,started_at)
                VALUES(@s,@id,@notification,@number,@key,'Started',@started)
                """ : """
                UPDATE notifications.notification_delivery_attempt SET status=@status,finished_at=@finished,provider_message_id=@message,error_code=@error
                WHERE school_id=@s AND attempt_id=@id AND notification_id=@notification AND status IN ('Started','Unknown')
                """, ("s", school), ("id", attempt.AttemptId), ("notification", attempt.NotificationId));
            if (insert) { command.Parameters.AddWithValue("number", attempt.Number); command.Parameters.AddWithValue("key", attempt.ProviderKey); command.Parameters.AddWithValue("started", attempt.StartedAt); }
            else
            {
                command.Parameters.AddWithValue("status", attempt.Status.ToString()); Nullable(command, "finished", NpgsqlDbType.TimestampTz, attempt.FinishedAt);
                Nullable(command, "message", NpgsqlDbType.Varchar, attempt.ProviderMessageId); Nullable(command, "error", NpgsqlDbType.Varchar, attempt.ErrorCode);
            }
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new ConflictException("ATTEMPT_CHANGED");
        }
        public Task DeferAsync(Guid id, int seconds, CancellationToken ct) => Execute("UPDATE notifications.notification_work SET available_at=clock_timestamp()+make_interval(secs=>@seconds) WHERE school_id=@s AND notification_id=@id", ct,
            ("seconds", Math.Clamp(seconds, 1, 3600)), ("s", school), ("id", id));
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public async ValueTask DisposeAsync() { await transaction.DisposeAsync(); await connection.DisposeAsync(); }
        private NpgsqlCommand Command(string sql, params (string Name, object Value)[] parameters)
        { var command = new NpgsqlCommand(sql, connection, transaction); foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value); return command; }
        private async Task Execute(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
        { await using var command = Command(sql, parameters); await command.ExecuteNonQueryAsync(ct); }
        private static void Nullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) => command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);
        private static T? Value<T>(NpgsqlDataReader reader, int index) where T : struct => reader.IsDBNull(index) ? null : reader.GetFieldValue<T>(index);
        private static string? Text(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    }
}
