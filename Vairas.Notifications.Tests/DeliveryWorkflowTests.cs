using System.Text.Json;
using Npgsql;
using NUnit.Framework;
using Vairas.Notifications.Business;
using Vairas.Notifications.Domain;
using Vairas.Notifications.Persistance;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Integration"), NonParallelizable]
public sealed class DeliveryWorkflowTests
{
    private PostgresFixture pg = null!;
    private NpgsqlDataSource data = null!;
    private DeliveryStore store = null!;
    private Owners owners = null!;
    private Provider provider = null!;
    private DeliveryOptions options = null!;
    private Guid school, source, user;
    private NotificationWorkflow Workflow => new(store, owners, provider, options, TimeProvider.System);

    [OneTimeSetUp] public async Task Start() { pg = new(); await pg.StartAsync(); data = NpgsqlDataSource.Create(pg.WorkerConnection); store = new(data); }
    [OneTimeTearDown] public async Task Stop() { await data.DisposeAsync(); await pg.DisposeAsync(); }
    [SetUp] public void Setup()
    {
        school = Guid.NewGuid(); source = Guid.NewGuid(); user = Guid.NewGuid(); options = new() { ApplicationOrigin = "https://vairas.test", ReminderMinutes = 1440 };
        owners = new() { Fact = new(school, source, 1, "Lesson", "Scheduled", DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(2).AddHours(1), [Guid.NewGuid()], Guid.NewGuid(), null, null),
            Scope = new(school, "Synthetic school", 1, [user], DateTimeOffset.UtcNow.AddSeconds(30)), Contact = new(user, true, "synthetic@example.test", "verified-contact-1", DateTimeOffset.UtcNow.AddSeconds(30)) };
        provider = new();
    }
    private SourceMessage Event(string type = "LessonScheduled", Guid? id = null)
    {
        var fact = owners.Fact!;
        var payload = type == "LessonCancelled" ? JsonSerializer.Serialize(new { schoolId = school, lessonId = source, lessonKind = "Practical", teacherId = fact.TeacherId,
            enrollmentId = fact.EnrollmentIds[0], vehicleId = source, cancelledAt = DateTimeOffset.UtcNow }, NotificationService.Json)
            : JsonSerializer.Serialize(new { schoolId = school, lessonId = source, lessonKind = "Practical", teacherId = fact.TeacherId, enrollmentId = fact.EnrollmentIds[0], vehicleId = source,
                startsAt = fact.StartsAt, endsAt = fact.EndsAt, previousStartsAt = fact.StartsAt!.Value.AddHours(-1), previousEndsAt = fact.EndsAt!.Value.AddHours(-1) }, NotificationService.Json);
        return new(id ?? Guid.NewGuid(), type, 1, "scheduling", school, source, fact.Revision, DateTimeOffset.UtcNow, "delivery-fixture", payload);
    }
    private async Task<DeliveryItem[]> Rows()
    {
        var ids = new List<Guid>();
        await using (var connection = new NpgsqlConnection(pg.OwnerConnection))
        {
            await connection.OpenAsync(); await using var command = new NpgsqlCommand("SELECT notification_id FROM notifications.notification WHERE school_id=@s ORDER BY notification_type", connection); command.Parameters.AddWithValue("s", school);
            await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        }
        var rows = new List<DeliveryItem>(); await using var tx = await store.OpenAsync(school, default);
        foreach (var id in ids) rows.Add((await tx.ReadAsync(id, default))!); return rows.ToArray();
    }

    [Test]
    public async Task Event_replay_semantic_duplicates_and_concurrent_consumption_create_only_one_notice_and_reminder()
    {
        var message = Event();
        await Task.WhenAll(Workflow.ConsumeAsync(message, default), Workflow.ConsumeAsync(message, default));
        await Workflow.ConsumeAsync(message with { EventId = Guid.NewGuid() }, default);
        var rows = await Rows(); Assert.That(rows, Has.Length.EqualTo(2));
        Assert.That(rows.Select(x => x.Snapshot.Type), Is.EquivalentTo(new[] { NotificationType.LessonScheduled, NotificationType.LessonReminder }));
        Assert.That(rows.All(x => x.Snapshot.Body.Contains("https://vairas.test/", StringComparison.Ordinal)), Is.True);
        Assert.That(rows.Single(x => x.Snapshot.Type == NotificationType.LessonReminder).Delivery.ScheduledAt, Is.EqualTo(owners.Fact!.StartsAt!.Value.AddDays(-1)).Within(TimeSpan.FromMilliseconds(1)));
        var reused = message with { CorrelationId = "changed-envelope" };
        Assert.That(Business.ThrowsBusinessAsync(async () => await Workflow.ConsumeAsync(reused, default))!.Code, Is.EqualTo("EVENT_ID_REUSED"));
    }

    [Test]
    public async Task Unknown_provider_outcome_reconciles_positive_evidence_without_second_submission()
    {
        await Workflow.ConsumeAsync(Event(), default);
        var row = (await Rows()).Single(x => x.Snapshot.Type == NotificationType.LessonScheduled);
        var work = new WorkItem(school, row.Snapshot.NotificationId);
        provider.Submission = new(SubmissionOutcome.Unknown, ErrorCode: "ACK_LOST");
        await Workflow.ProcessAsync(work, default);
        Assert.That((await Rows()).Single(x => x.Snapshot.NotificationId == work.NotificationId).Delivery.Status, Is.EqualTo(NotificationStatus.DeliveryUnknown));
        await Workflow.ProcessAsync(work, default);
        Assert.That(provider.Submissions, Is.EqualTo(1));
        provider.Reconciliation = new(SubmissionOutcome.Accepted, "gmail-message-id");
        await Workflow.ProcessAsync(work, default);
        var resolved = (await Rows()).Single(x => x.Snapshot.NotificationId == work.NotificationId);
        Assert.That(resolved.Delivery.Status, Is.EqualTo(NotificationStatus.Sent));
        Assert.That(resolved.Attempt!.Status, Is.EqualTo(AttemptStatus.Accepted)); Assert.That(provider.Submissions, Is.EqualTo(1));
    }

    [Test]
    public async Task Multiple_workers_claim_one_submission_and_finished_attempt_is_immutable()
    {
        await Workflow.ConsumeAsync(Event(), default);
        var row = (await Rows()).Single(x => x.Snapshot.Type == NotificationType.LessonScheduled); var work = new WorkItem(school, row.Snapshot.NotificationId);
        await Task.WhenAll(Workflow.ProcessAsync(work, default), Workflow.ProcessAsync(work, default), Workflow.ProcessAsync(work, default));
        var sent = (await Rows()).Single(x => x.Snapshot.NotificationId == work.NotificationId);
        Assert.That(sent.Delivery.Status, Is.EqualTo(NotificationStatus.Sent)); Assert.That(sent.Delivery.AttemptCount, Is.EqualTo(1)); Assert.That(provider.Submissions, Is.EqualTo(1));
        var error = Assert.ThrowsAsync<PostgresException>(async () => await PostgresFixture.Sql(pg.OwnerConnection, $"UPDATE notifications.notification_delivery_attempt SET status='Rejected' WHERE attempt_id='{sent.Attempt!.AttemptId}'"));
        Assert.That(error!.SqlState, Is.EqualTo("P0001"));
    }

    [Test]
    public async Task Worker_crash_after_claim_recovers_to_unknown_instead_of_resending()
    {
        await Workflow.ConsumeAsync(Event(), default);
        var row = (await Rows()).Single(x => x.Snapshot.Type == NotificationType.LessonScheduled); var work = new WorkItem(school, row.Snapshot.NotificationId);
        await using (var tx = await store.OpenAsync(school, default))
        {
            var now = await tx.NowAsync(default); var sending = row.Delivery.Begin(row.Delivery.Revision, now);
            await tx.SaveAsync(row, sending, new(Guid.NewGuid(), work.NotificationId, 1, NotificationDelivery.ProviderKey(school, work.NotificationId, 1), AttemptStatus.Started, now), default);
            await tx.CommitAsync(default);
        }
        // Fixture shortens the recovery threshold without rewriting immutable attempt timestamps.
        options.SendingTimeoutSeconds = 0;
        await Workflow.ProcessAsync(work, default);
        Assert.That((await Rows()).Single(x => x.Snapshot.NotificationId == work.NotificationId).Delivery.Status, Is.EqualTo(NotificationStatus.DeliveryUnknown));
        Assert.That(provider.Submissions, Is.Zero);
    }

    [Test]
    public async Task Reschedule_invalidates_old_pending_and_cancellation_invalidates_the_replacement_reminder()
    {
        await Workflow.ConsumeAsync(Event(), default);
        owners.Fact = owners.Fact! with { Revision = 2, StartsAt = owners.Fact.StartsAt!.Value.AddDays(1), EndsAt = owners.Fact.EndsAt!.Value.AddDays(1) };
        await Workflow.ConsumeAsync(Event("LessonRescheduled"), default);
        var after = await Rows(); Assert.That(after.Count(x => x.Delivery.Status == NotificationStatus.Cancelled), Is.EqualTo(2));
        Assert.That(after.Count(x => x.Delivery.Status == NotificationStatus.Pending), Is.EqualTo(2));
        owners.Fact = owners.Fact with { Revision = 3, Status = "Cancelled" };
        await Workflow.ConsumeAsync(Event("LessonCancelled"), default);
        var cancelled = await Rows(); Assert.That(cancelled.Count(x => x.Delivery.Status == NotificationStatus.Cancelled), Is.EqualTo(4));
        Assert.That(cancelled.Single(x => x.Delivery.Status == NotificationStatus.Pending).Snapshot.Type, Is.EqualTo(NotificationType.LessonCancelled));
        await Workflow.ConsumeAsync(Event("LessonCancelled"), default);
        Assert.That((await Rows()).Count(x => x.Delivery.Status == NotificationStatus.Pending), Is.EqualTo(1), "A semantic duplicate must not cancel the current cancellation notice.");
    }

    [Test]
    public async Task Outage_has_no_receipt_and_unverified_email_is_not_used()
    {
        var message = Event(); owners.Unavailable = true;
        Business.ThrowsBusinessAsync(async () => await Workflow.ConsumeAsync(message, default));
        await using (var tx = await store.OpenAsync(school, default)) Assert.That(await tx.InboxAsync(message, default), Is.Null);
        Assert.That(await Rows(), Is.Empty);
        owners.Unavailable = false; owners.Contact = owners.Contact with { Eligible = false, Address = null };
        await Workflow.ConsumeAsync(message, default); Assert.That(await Rows(), Is.Empty);
        await using var receipt = await store.OpenAsync(school, default); Assert.That((await receipt.InboxAsync(message, default))!.Outcome, Is.EqualTo("Applied"));
    }

    [Test]
    public async Task Changed_contact_cancels_snapshot_and_creates_a_new_authorized_intent_before_submission()
    {
        await Workflow.ConsumeAsync(Event(), default); var old = (await Rows()).Single(x => x.Snapshot.Type == NotificationType.LessonScheduled);
        owners.Contact = owners.Contact with { Revision = "verified-contact-2", Address = "changed@example.test" };
        await Workflow.ProcessAsync(new(school, old.Snapshot.NotificationId), default);
        var rows = await Rows(); Assert.That(rows.Single(x => x.Snapshot.NotificationId == old.Snapshot.NotificationId).Delivery.Status, Is.EqualTo(NotificationStatus.Cancelled));
        var replacement = rows.Single(x => x.Snapshot.Type == NotificationType.LessonScheduled && x.Delivery.Status == NotificationStatus.Pending);
        Assert.That(replacement.Snapshot.DestinationAddress, Is.EqualTo("changed@example.test")); Assert.That(provider.Submissions, Is.Zero);
        await Workflow.ProcessAsync(new(school, replacement.Snapshot.NotificationId), default); Assert.That(provider.Submissions, Is.EqualTo(1));
    }

    [Test]
    public async Task Recipient_revocation_source_change_and_contact_outage_never_submit()
    {
        await Workflow.ConsumeAsync(Event(), default); var row = (await Rows()).Single(x => x.Snapshot.Type == NotificationType.LessonScheduled); var work = new WorkItem(school, row.Snapshot.NotificationId);
        owners.Unavailable = true; Business.ThrowsBusinessAsync(async () => await Workflow.ProcessAsync(work, default));
        Assert.That((await Rows()).Single(x => x.Snapshot.NotificationId == work.NotificationId).Delivery.Status, Is.EqualTo(NotificationStatus.Pending));
        owners.Unavailable = false; owners.Scope = owners.Scope with { UserIds = [] };
        await Workflow.ProcessAsync(work, default); Assert.That(provider.Submissions, Is.Zero);
        Assert.That((await Rows()).Single(x => x.Snapshot.NotificationId == work.NotificationId).Delivery.Status, Is.EqualTo(NotificationStatus.Cancelled));
    }

    [Test]
    public async Task Definitive_rejection_persists_retry_and_future_reminder_is_not_submitted_early()
    {
        await Workflow.ConsumeAsync(Event(), default); var rows = await Rows();
        var reminder = rows.Single(x => x.Snapshot.Type == NotificationType.LessonReminder); await Workflow.ProcessAsync(new(school, reminder.Snapshot.NotificationId), default);
        Assert.That(provider.Submissions, Is.Zero);
        provider.Submission = new(SubmissionOutcome.RetryableRejection, ErrorCode: "QUOTA_REJECTED");
        var immediate = rows.Single(x => x.Snapshot.Type == NotificationType.LessonScheduled); await Workflow.ProcessAsync(new(school, immediate.Snapshot.NotificationId), default);
        var pending = (await Rows()).Single(x => x.Snapshot.NotificationId == immediate.Snapshot.NotificationId);
        Assert.That(pending.Delivery.Status, Is.EqualTo(NotificationStatus.Pending)); Assert.That(pending.Attempt!.Status, Is.EqualTo(AttemptStatus.Rejected));
        Assert.That(pending.Delivery.NextAttemptAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddSeconds(30)));
        await Workflow.ProcessAsync(new(school, immediate.Snapshot.NotificationId), default); Assert.That(provider.Submissions, Is.EqualTo(1));
    }

    [Test]
    public async Task Provider_call_is_bounded_by_the_current_admission_and_timeout_never_blindly_retries()
    {
        await Workflow.ConsumeAsync(Event(), default);
        var row = (await Rows()).Single(x => x.Snapshot.Type == NotificationType.LessonScheduled);
        owners.DeadlineSeconds = 3; provider.AwaitCancellation = true;
        await Workflow.ProcessAsync(new(school, row.Snapshot.NotificationId), default);
        var timedOut = (await Rows()).Single(x => x.Snapshot.NotificationId == row.Snapshot.NotificationId);
        Assert.That(timedOut.Delivery.Status, Is.EqualTo(NotificationStatus.DeliveryUnknown)); Assert.That(provider.Submissions, Is.EqualTo(1));
        await Workflow.ProcessAsync(new(school, row.Snapshot.NotificationId), default); Assert.That(provider.Submissions, Is.EqualTo(1));
    }

    [Test]
    public async Task Assessment_correction_cancels_obsolete_notice_and_annulment_creates_no_new_notice()
    {
        var enrollment = Guid.NewGuid(); owners.Fact = new(school, source, 1, "Assessment", "Recorded", null, null, [enrollment], null, DateTimeOffset.UtcNow, "Passed");
        SourceMessage Assessment(string type) => new(Guid.NewGuid(), type, 1, "learning", school, source, owners.Fact.Revision, DateTimeOffset.UtcNow, "assessment-fixture",
            type == "AssessmentResultRevised" ? JsonSerializer.Serialize(new { schoolId = school, assessmentId = source, enrollmentId = enrollment, version = owners.Fact.Revision, status = owners.Fact.Status }, NotificationService.Json)
            : JsonSerializer.Serialize(new { schoolId = school, assessmentId = source, enrollmentId = enrollment, assessedAt = owners.Fact.AssessedAt }, NotificationService.Json));
        await Workflow.ConsumeAsync(Assessment("AssessmentPassed"), default); Assert.That((await Rows()).Single().Snapshot.Type, Is.EqualTo(NotificationType.AssessmentPassed));
        owners.Fact = owners.Fact with { Revision = 2, Outcome = "Failed" }; await Workflow.ConsumeAsync(Assessment("AssessmentResultRevised"), default);
        Assert.That((await Rows()).Single(x => x.Delivery.Status == NotificationStatus.Pending).Snapshot.Type, Is.EqualTo(NotificationType.AssessmentFailed));
        owners.Fact = owners.Fact with { Revision = 3, Status = "Annulled" }; await Workflow.ConsumeAsync(Assessment("AssessmentResultRevised"), default);
        Assert.That((await Rows()).All(x => x.Delivery.Status == NotificationStatus.Cancelled), Is.True);
    }

    [Test]
    public async Task Worker_permissions_cannot_modify_templates_receipts_or_foreign_tenant_state()
    {
        await Workflow.ConsumeAsync(Event(), default);
        await using var other = await store.OpenAsync(Guid.NewGuid(), default);
        Assert.That(await other.ReadAsync((await Rows()).First().Snapshot.NotificationId, default), Is.Null);
        await using var connection = new NpgsqlConnection(pg.WorkerConnection); await connection.OpenAsync();
        foreach (var sql in new[] { "UPDATE notifications.notification_template SET subject_template='tampered'", "DELETE FROM notifications.notification", "SELECT * FROM notifications.notification_operation" })
        { await using var command = new NpgsqlCommand(sql, connection); Assert.That(Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync())!.SqlState, Is.EqualTo("42501")); }
        var stats = await store.StatsAsync(default); Assert.That(stats.Pending, Is.GreaterThanOrEqualTo(2));
        Assert.That(await store.DiscoverAsync(50, default), Is.Not.Empty);
    }

    [Test]
    public async Task Content_retention_is_explicit_owner_only_terminal_only_and_preserves_deduplication()
    {
        var message = Event();
        await using (var connection = new NpgsqlConnection(pg.OwnerConnection))
        {
            await connection.OpenAsync();
            foreach (var type in new[] { NotificationType.LessonScheduled, NotificationType.LessonReminder })
            {
                var id = Guid.NewGuid(); var sent = type == NotificationType.LessonScheduled;
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO notifications.notification(school_id,notification_id,recipient_user_id,notification_type,subject,body,destination_address,contact_revision,
                      source_context,source_id,source_revision,source_event_id,dispatch_key,scheduled_at,status,revision,attempt_count,sent_at,created_at,updated_at)
                    VALUES(@s,@id,@user,@type,'Old retained subject','Old retained body','synthetic@example.test',@contact,'Scheduling',@source,1,@event,@key,
                      CASE WHEN @sent THEN statement_timestamp()-interval '7 days' ELSE @due END,CASE WHEN @sent THEN 'Sent' ELSE 'Pending' END,1,
                      CASE WHEN @sent THEN 1 ELSE 0 END,CASE WHEN @sent THEN statement_timestamp()-interval '6 days' ELSE NULL END,
                      statement_timestamp()-interval '7 days',statement_timestamp()-interval '6 days')
                    """, connection);
                seed.Parameters.AddWithValue("s", school); seed.Parameters.AddWithValue("id", id); seed.Parameters.AddWithValue("user", user); seed.Parameters.AddWithValue("type", type.ToString());
                seed.Parameters.AddWithValue("contact", owners.Contact.Revision); seed.Parameters.AddWithValue("source", source); seed.Parameters.AddWithValue("event", message.EventId);
                seed.Parameters.AddWithValue("key", NotificationWorkflow.Intent("Scheduling", source, 1, type, owners.Contact.Revision)); seed.Parameters.AddWithValue("sent", sent);
                seed.Parameters.AddWithValue("due", owners.Fact!.StartsAt!.Value.AddDays(-1)); await seed.ExecuteNonQueryAsync();
                if (sent)
                {
                    await using var attempt = new NpgsqlCommand("""
                        INSERT INTO notifications.notification_delivery_attempt(school_id,attempt_id,notification_id,attempt_no,provider_key,status,started_at,finished_at)
                        VALUES(@s,@attempt,@id,1,@key,'Accepted',statement_timestamp()-interval '7 days',statement_timestamp()-interval '6 days')
                        """, connection);
                    attempt.Parameters.AddWithValue("s", school); attempt.Parameters.AddWithValue("attempt", Guid.NewGuid()); attempt.Parameters.AddWithValue("id", id);
                    attempt.Parameters.AddWithValue("key", NotificationDelivery.ProviderKey(school, id, 1)); await attempt.ExecuteNonQueryAsync();
                }
            }
        }
        await Workflow.ConsumeAsync(message, default);
        var row = (await Rows()).Single(x => x.Snapshot.Type == NotificationType.LessonScheduled);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var preview = await ContentRetention.RunAsync(pg.OwnerConnection, cutoff, "synthetic retention test", false); Assert.That(preview, Is.EqualTo(1));
        Assert.That((await Rows()).Single(x => x.Snapshot.NotificationId == row.Snapshot.NotificationId).Snapshot.DestinationAddress, Is.EqualTo("synthetic@example.test"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ContentRetention.RunAsync(pg.WorkerConnection, cutoff, "forbidden", true));
        await ContentRetention.RunAsync(pg.OwnerConnection, cutoff, "synthetic retention test", true);
        var terminal = (await Rows()).Single(x => x.Snapshot.NotificationId == row.Snapshot.NotificationId);
        Assert.That(terminal.Snapshot.DestinationAddress, Is.EqualTo("removed@invalid.test")); Assert.That(terminal.Delivery.Status, Is.EqualTo(NotificationStatus.Sent));
        Assert.That((await Rows()).Single(x => x.Snapshot.Type == NotificationType.LessonReminder).Snapshot.DestinationAddress, Is.EqualTo("synthetic@example.test"));
        await Workflow.ConsumeAsync(message with { EventId = Guid.NewGuid() }, default);
        Assert.That(await Rows(), Has.Length.EqualTo(2), "Terminal intent keys must survive content retention.");
    }

    public sealed class Owners : INotificationOwners
    {
        public SourceFact? Fact { get; set; } public RecipientScope Scope { get; set; } = null!; public ContactEvidence Contact { get; set; } = null!; public bool Unavailable { get; set; }
        public int DeadlineSeconds { get; set; } = 30;
        private void Check() { if (Unavailable) throw NotificationErrors.Dependency(); }
        public Task<SourceFact?> SourceAsync(string producer, Guid school, Guid source, CancellationToken ct) { Check(); return Task.FromResult(Fact); }
        public Task<RecipientScope> RecipientsAsync(SourceFact source, CancellationToken ct) { Check(); return Task.FromResult(Scope with { ValidUntil = DateTimeOffset.UtcNow.AddSeconds(DeadlineSeconds) }); }
        public Task<ContactEvidence> ContactAsync(Guid user, CancellationToken ct) { Check(); return Task.FromResult(Contact with { ValidUntil = DateTimeOffset.UtcNow.AddSeconds(DeadlineSeconds) }); }
    }
    public sealed class Provider : IEmailProvider
    {
        private int submissions; public int Submissions => submissions;
        public ProviderResult Submission { get; set; } = new(SubmissionOutcome.Accepted, "synthetic-gmail-id");
        public ProviderResult Reconciliation { get; set; } = new(SubmissionOutcome.Unknown);
        public bool AwaitCancellation { get; set; }
        public async Task<ProviderResult> SubmitAsync(NotificationSnapshot snapshot, DeliveryAttempt attempt, CancellationToken ct)
        { Interlocked.Increment(ref submissions); if (AwaitCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Submission; }
        public Task<ProviderResult> ReconcileAsync(NotificationSnapshot snapshot, DeliveryAttempt attempt, CancellationToken ct) => Task.FromResult(Reconciliation);
    }
}
