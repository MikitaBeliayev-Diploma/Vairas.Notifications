using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vairas.Notifications.Business;
using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Persistance;

public sealed class NotificationStore(NpgsqlDataSource dataSource) : INotificationStore
{
    public Task<INotificationSession> OpenAsync(Guid school, bool write, CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(() => OpenAsyncCore(school, write, ct));
    private async Task<INotificationSession> OpenAsyncCore(Guid school, bool write, CancellationToken ct)
    {
        if (school == Guid.Empty) throw NotificationErrors.Invalid();
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            var session = new Session(connection, transaction, school, write);
            await session.ConfigureAsync(ct);
            return session;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid school, bool write) : INotificationSession
    {
        private readonly NotificationDbContext db = new(new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options);

        public Task ConfigureAsync(CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(async () =>
        {
            await db.Database.UseTransactionAsync(transaction, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.school_id', {school.ToString("D")}, true), set_config('lock_timeout', '5s', true)", ct);
            if (write)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({school.ToString("D")}, 8))", ct);
        });

        public async Task<DateTimeOffset> NowAsync(CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction);
            return new((DateTime)(await command.ExecuteScalarAsync(ct))!, TimeSpan.Zero);
        }
        public async Task<IReadOnlyList<NotificationTemplate>> TemplatesAsync(CancellationToken ct)
        {
            var rows = await DatabaseExceptionTranslator.GuardAsync(() => db.Set<NotificationTemplate>().Where(x => x.SchoolId == school)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.TemplateId).Take(501).ToArrayAsync(ct));
            if (rows.Length > 500) throw new ConflictException("TEMPLATE_CATALOGUE_LIMIT");
            return rows;
        }
        public Task<NotificationTemplate?> TemplateAsync(Guid id, CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(() =>
            db.Set<NotificationTemplate>().SingleOrDefaultAsync(x => x.SchoolId == school && x.TemplateId == id, ct));
        public Task InsertTemplateAsync(NotificationTemplate template, CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(async () =>
        {
            Writable();
            if (template.SchoolId != school) throw new InvalidOperationException("Tenant mismatch.");
            db.Add(template);
            await db.SaveChangesAsync(ct);
            db.Entry(template).State = EntityState.Detached;
        });
        public Task UpdateTemplateAsync(NotificationTemplate template, long expectedRevision, CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(async () =>
        {
            Writable();
            if (template.SchoolId != school) throw new InvalidOperationException("Tenant mismatch.");
            var count = await db.Set<NotificationTemplate>().Where(x => x.SchoolId == school && x.TemplateId == template.TemplateId && x.Revision == expectedRevision)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.SubjectTemplate, template.SubjectTemplate)
                    .SetProperty(x => x.BodyTemplate, template.BodyTemplate).SetProperty(x => x.Status, template.Status)
                    .SetProperty(x => x.Revision, template.Revision).SetProperty(x => x.UpdatedAt, template.UpdatedAt), ct);
            if (count != 1) throw new ConflictException("REVISION_CONFLICT");
        });
        public async Task<StoredNotification?> NotificationAsync(Guid id, CancellationToken ct)
        {
            var row = await DatabaseExceptionTranslator.GuardAsync(() => db.Set<NotificationDeliveryRow>()
                .SingleOrDefaultAsync(x => x.SchoolId == school && x.NotificationId == id, ct));
            return row is null ? null : new(school, row.NotificationId, row.RecipientUserId,
                new(row.Status, row.Revision, row.AttemptCount, row.ScheduledAt, row.NextAttemptAt, row.SentAt, row.CancelledAt, row.FailureCode, row.UpdatedAt));
        }
        public Task UpdateDeliveryAsync(Guid id, NotificationDelivery delivery, long expectedRevision, CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(async () =>
        {
            Writable();
            if (delivery.Status != NotificationStatus.Cancelled) throw new InvalidOperationException("Only cancellation is exposed by this session.");
            var count = await db.Set<NotificationDeliveryRow>().Where(x => x.SchoolId == school && x.NotificationId == id && x.Revision == expectedRevision && x.Status == NotificationStatus.Pending)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, NotificationStatus.Cancelled).SetProperty(x => x.Revision, delivery.Revision)
                    .SetProperty(x => x.CancelledAt, delivery.CancelledAt!.Value).SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, delivery.UpdatedAt), ct);
            if (count != 1) throw new ConflictException("REVISION_CONFLICT");
        });
        public Task<OperationReceipt?> ReceiptAsync(Guid operation, CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(() =>
            db.Set<OperationReceipt>().SingleOrDefaultAsync(x => x.SchoolId == school && x.OperationId == operation, ct));
        public Task InsertReceiptAsync(OperationReceipt receipt, CancellationToken ct) => DatabaseExceptionTranslator.GuardAsync(async () =>
        {
            Writable();
            if (receipt.SchoolId != school) throw new InvalidOperationException("Tenant mismatch.");
            var entry = db.Add(receipt);
            entry.Property("Channel").CurrentValue = "Web";
            entry.Property("Outcome").CurrentValue = "Applied";
            entry.Property("OutcomeCode").CurrentValue = "OK";
            await db.SaveChangesAsync(ct);
            entry.State = EntityState.Detached;
        });
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await transaction.DisposeAsync(); await connection.DisposeAsync(); }
        private void Writable() { if (!write) throw new InvalidOperationException("Read session."); }
    }
}
