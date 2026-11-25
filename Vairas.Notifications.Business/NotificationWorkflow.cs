using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed class NotificationWorkflow(IDeliveryStore store, INotificationOwners owners, IEmailProvider provider, DeliveryOptions options, TimeProvider clock) : INotificationWorkflow
{
    public async Task ConsumeAsync(SourceMessage message, CancellationToken ct)
    {
        var hash = SourceMessagePolicy.ValidateAndHash(message);
        await using (var check = await store.OpenAsync(message.SchoolId, ct))
            if (Replay(await check.InboxAsync(message, ct), hash)) return;
        var admitted = clock.GetUtcNow();
        var source = await owners.SourceAsync(message.Producer, message.SchoolId, message.SourceId, ct);
        if (source is not null) Bind(source, message.SchoolId, message.SourceId, message.Producer);
        if (source is not null && source.Revision < message.SourceRevision) throw new ServiceUnavailableException("SOURCE_BEHIND_EVENT");
        var stale = source is null || source.Revision != message.SourceRevision;
        var scope = !stale ? await owners.RecipientsAsync(source!, ct) : null;
        if (scope is not null) ValidateScope(source!, scope);
        var contacts = new List<ContactEvidence>();
        if (scope is not null)
            foreach (var batch in scope.UserIds.Chunk(16)) contacts.AddRange(await Task.WhenAll(batch.Select(x => owners.ContactAsync(x, ct))));
        await using var tx = await store.OpenAsync(message.SchoolId, ct);
        if (Replay(await tx.InboxAsync(message, ct), hash)) return;
        var now = await tx.NowAsync(ct);
        var context = SourceMessagePolicy.Context(message.Producer);
        if (source is null) await tx.CancelObsoleteAsync(context, message.SourceId, long.MaxValue, true, ct);
        else await tx.CancelObsoleteAsync(context, source.SourceId, source.Revision, !Current(source), ct);
        if (stale)
        {
            await tx.RecordInboxAsync(message, hash, "Ignored", "STALE_OR_REMOVED_SOURCE", ct);
            await tx.CommitAsync(ct); return;
        }
        Fresh(admitted.AddSeconds(30)); Fresh(scope!.ValidUntil);
        var type = Type(source!, message.EventType);
        var templates = await tx.TemplatesAsync(ct);
        if (type is not null)
            foreach (var contact in contacts.Where(x => x.Eligible))
            {
                ValidateContact(contact, scope.UserIds);
                await Prepare(tx, source!, scope, contact, type.Value, message.EventId, message.CorrelationId, now, templates, false, ct);
                if (source!.Kind == "Lesson" && source.Status == "Scheduled")
                    await Prepare(tx, source, scope, contact, NotificationType.LessonReminder, message.EventId, message.CorrelationId, now, templates, true, ct);
            }
        Fresh(admitted.AddSeconds(30)); Fresh(scope.ValidUntil);
        foreach (var contact in contacts) Fresh(contact.ValidUntil);
        await tx.RecordInboxAsync(message, hash, "Applied", type is null ? "INVALIDATED" : "PREPARED", ct);
        await tx.CommitAsync(ct);
    }

    public async Task ProcessAsync(WorkItem work, CancellationToken ct)
    {
        DeliveryItem? item;
        await using (var read = await store.OpenAsync(work.SchoolId, ct)) item = await read.ReadAsync(work.NotificationId, ct);
        if (item is null || item.Delivery.Status is NotificationStatus.Sent or NotificationStatus.Cancelled or NotificationStatus.Failed) return;
        System.Diagnostics.Activity.Current?.AddBaggage("correlationId", item.Snapshot.CorrelationId);
        if (item.Delivery.Status == NotificationStatus.Sending)
        {
            await using var expired = await store.OpenAsync(work.SchoolId, ct);
            var current = await expired.ReadAsync(work.NotificationId, ct);
            if (current is not { Delivery.Status: NotificationStatus.Sending, Attempt.Status: AttemptStatus.Started }) return;
            var now = await expired.NowAsync(ct);
            if (now < current.Attempt.StartedAt.AddSeconds(options.SendingTimeoutSeconds)) return;
            await expired.SaveAsync(current, current.Delivery.Complete(SubmissionOutcome.Unknown, now), current.Attempt with
                { Status = AttemptStatus.Unknown, FinishedAt = now, ErrorCode = "WORKER_INTERRUPTED" }, ct);
            await expired.CommitAsync(ct); return;
        }
        if (item.Delivery.Status == NotificationStatus.DeliveryUnknown)
        {
            if (item.Attempt is null) throw new InvalidOperationException("Unknown delivery has no attempt.");
            var reconciled = await provider.ReconcileAsync(item.Snapshot, item.Attempt, ct);
            if (reconciled.Outcome == SubmissionOutcome.Unknown) { await Defer(work, 30, ct); return; }
            await Finish(work, item.Attempt.AttemptId, reconciled, ct); return;
        }
        var admitted = clock.GetUtcNow();
        var snapshot = item.Snapshot;
        var producer = SourceMessagePolicy.Producer(snapshot.SourceContext);
        var fact = await owners.SourceAsync(producer, work.SchoolId, snapshot.SourceId, ct);
        if (fact is not null) Bind(fact, work.SchoolId, snapshot.SourceId, producer);
        var validSource = fact is not null && fact.Revision == snapshot.SourceRevision && AllowedFor(fact, snapshot.Type);
        RecipientScope? scope = validSource ? await owners.RecipientsAsync(fact!, ct) : null;
        if (scope is not null) ValidateScope(fact!, scope);
        ContactEvidence? contact = scope?.UserIds.Contains(snapshot.RecipientUserId) == true ? await owners.ContactAsync(snapshot.RecipientUserId, ct) : null;
        if (scope is not null) Fresh(scope.ValidUntil);
        if (contact is not null) { Fresh(contact.ValidUntil); if (contact.Eligible) ValidateContact(contact, [snapshot.RecipientUserId]); }
        Fresh(admitted.AddSeconds(30));
        DeliveryAttempt? attempt;
        await using (var claim = await store.OpenAsync(work.SchoolId, ct))
        {
            var current = await claim.ReadAsync(work.NotificationId, ct);
            if (current is null || current.Delivery.Status != NotificationStatus.Pending || current.Delivery.Revision != item.Delivery.Revision) return;
            var now = await claim.NowAsync(ct);
            if (!validSource || contact is not { Eligible: true } || !AllowedFor(fact!, snapshot.Type))
            {
                await claim.SaveAsync(current, current.Delivery.Cancel(current.Delivery.Revision, now), null, ct);
                await claim.CommitAsync(ct); return;
            }
            Fresh(scope!.ValidUntil); Fresh(contact.ValidUntil); Fresh(admitted.AddSeconds(30));
            if (contact.Revision != snapshot.ContactRevision || contact.Address != snapshot.DestinationAddress)
            {
                await claim.SaveAsync(current, current.Delivery.Cancel(current.Delivery.Revision, now), null, ct);
                var replacement = snapshot with { NotificationId = Guid.NewGuid(), DestinationAddress = contact.Address!, ContactRevision = contact.Revision,
                    DispatchKey = Intent(snapshot.SourceContext, snapshot.SourceId, snapshot.SourceRevision, snapshot.Type, contact.Revision), CreatedAt = now };
                await claim.InsertAsync(replacement, current.Delivery with { Revision = 1, UpdatedAt = now, NextAttemptAt = null, AttemptCount = 0,
                    ScheduledAt = current.Delivery.ScheduledAt > now ? current.Delivery.ScheduledAt : now, FailureCode = null }, snapshot.CorrelationId, ct);
                await claim.CommitAsync(ct); return;
            }
            if (now < current.Delivery.ScheduledAt || current.Delivery.NextAttemptAt > now) return;
            var sending = current.Delivery.Begin(current.Delivery.Revision, now);
            attempt = new(Guid.NewGuid(), work.NotificationId, sending.AttemptCount, NotificationDelivery.ProviderKey(work.SchoolId, work.NotificationId, sending.AttemptCount), AttemptStatus.Started, now);
            await claim.SaveAsync(current, sending, attempt, ct);
            Fresh(contact.ValidUntil); Fresh(scope.ValidUntil); Fresh(admitted.AddSeconds(30));
            await claim.CommitAsync(ct);
        }
        // Submission has no SQL transaction. A crash from here is recovered conservatively as Unknown.
        var submissionUntil = new[] { admitted.AddSeconds(30), scope!.ValidUntil, contact!.ValidUntil }.Min();
        ProviderResult result;
        var remaining = submissionUntil - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) result = new(SubmissionOutcome.RetryableRejection, ErrorCode: "ADMISSION_EXPIRED_BEFORE_SUBMIT");
        else
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(ct);
            admission.CancelAfter(remaining);
            try { result = await provider.SubmitAsync(snapshot, attempt, admission.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { result = new(SubmissionOutcome.Unknown, ErrorCode: "SUBMISSION_DEADLINE_UNKNOWN"); }
        }
        await Finish(work, attempt.AttemptId, result, ct);
    }

    private async Task Finish(WorkItem work, Guid attemptId, ProviderResult result, CancellationToken ct)
    {
        await using var tx = await store.OpenAsync(work.SchoolId, ct);
        var item = await tx.ReadAsync(work.NotificationId, ct);
        if (item is null || item.Attempt?.AttemptId != attemptId || item.Delivery.Status is not (NotificationStatus.Sending or NotificationStatus.DeliveryUnknown)) return;
        if (item.Delivery.Status == NotificationStatus.DeliveryUnknown && result.Outcome == SubmissionOutcome.Unknown) return;
        var now = await tx.NowAsync(ct);
        var delivery = item.Delivery.Complete(result.Outcome, now, item.Delivery.Status == NotificationStatus.DeliveryUnknown);
        var status = result.Outcome switch { SubmissionOutcome.Accepted => AttemptStatus.Accepted, SubmissionOutcome.Unknown => AttemptStatus.Unknown, _ => AttemptStatus.Rejected };
        await tx.SaveAsync(item, delivery, item.Attempt with { Status = status, FinishedAt = now, ProviderMessageId = result.MessageId, ErrorCode = result.ErrorCode }, ct);
        await tx.CommitAsync(ct);
    }
    private async Task Defer(WorkItem work, int seconds, CancellationToken ct)
    { await using var tx = await store.OpenAsync(work.SchoolId, ct); await tx.DeferAsync(work.NotificationId, seconds, ct); await tx.CommitAsync(ct); }

    private async Task Prepare(IDeliveryTransaction tx, SourceFact source, RecipientScope scope, ContactEvidence contact, NotificationType type, Guid eventId,
        string correlation, DateTimeOffset now, IReadOnlyList<NotificationTemplate> templates, bool reminder, CancellationToken ct)
    {
        if (source.Kind == "Lesson" && source.EndsAt <= now) return;
        var scheduled = reminder ? source.StartsAt!.Value.AddMinutes(-options.ReminderMinutes) : now;
        if (reminder && scheduled <= now) return;
        var template = templates.SingleOrDefault(x => x.Status == TemplateStatus.Active && x.Type == type);
        var appLink = $"{options.ApplicationOrigin.TrimEnd('/')}/schools/{source.SchoolId:D}/{(source.Kind == "Lesson" ? "lessons" : "assessments")}/{source.SourceId:D}";
        var subject = template?.SubjectTemplate ?? type switch
        {
            NotificationType.LessonScheduled => "Your lesson was scheduled", NotificationType.LessonRescheduled => "Your lesson time changed",
            NotificationType.LessonCancelled => "Your lesson was cancelled", NotificationType.LessonReminder => "Your upcoming lesson", _ => "An assessment result is available"
        };
        var body = template?.BodyTemplate ?? "{{schoolName}}: open the Vairas application for details: {{appLink}}";
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["schoolName"] = scope.SchoolName, ["appLink"] = appLink,
            ["startsAt"] = source.StartsAt?.ToString("O", CultureInfo.InvariantCulture) ?? "", ["endsAt"] = source.EndsAt?.ToString("O", CultureInfo.InvariantCulture) ?? "" };
        var content = NotificationRenderer.Render(type, subject, body, values);
        var context = source.Kind == "Lesson" ? "Scheduling" : "LearningTracking";
        var snapshot = new NotificationSnapshot(source.SchoolId, Guid.NewGuid(), contact.UserId, template?.TemplateId, template?.Revision, type, content.Subject, content.Body,
            contact.Address!, contact.Revision, context, source.SourceId, source.Revision, eventId, Intent(context, source.SourceId, source.Revision, type, contact.Revision), now, correlation);
        await tx.InsertAsync(snapshot, new(NotificationStatus.Pending, 1, 0, scheduled, null, null, null, null, now), correlation, ct);
    }

    public static string Intent(string context, Guid source, long revision, NotificationType type, string contactRevision) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{context}|{source:D}|{revision}|{type}|{contactRevision}")));
    private static bool Replay(InboxReceipt? receipt, string hash)
    { if (receipt is null) return false; if (receipt.Hash != hash) throw new ConflictException("EVENT_ID_REUSED"); return true; }
    private static bool Current(SourceFact source) => source.Kind == "Lesson" ? source.Status is "Scheduled" or "Cancelled" : source.Status == "Recorded";
    private static NotificationType? Type(SourceFact source, string eventType) => source.Kind == "Assessment"
        ? source.Status == "Recorded" ? source.Outcome switch { "Passed" => NotificationType.AssessmentPassed, "Failed" => NotificationType.AssessmentFailed, _ => throw NotificationErrors.Dependency() } : null
        : source.Status == "Cancelled" ? NotificationType.LessonCancelled : source.Status == "Scheduled" ? eventType == "LessonScheduled" ? NotificationType.LessonScheduled : NotificationType.LessonRescheduled : null;
    private bool AllowedFor(SourceFact source, NotificationType type) => source.Kind == "Lesson"
        ? (type == NotificationType.LessonCancelled ? source.Status == "Cancelled" : source.Status == "Scheduled") && source.EndsAt > clock.GetUtcNow()
        : source.Status == "Recorded" && (source.Outcome == "Passed" ? type == NotificationType.AssessmentPassed : source.Outcome == "Failed" && type == NotificationType.AssessmentFailed);
    private static void Bind(SourceFact source, Guid school, Guid id, string producer)
    {
        if (source.SchoolId != school || source.SourceId != id || source.Revision < 1 || source.Kind != (producer == "scheduling" ? "Lesson" : "Assessment")
            || source.EnrollmentIds is null || source.EnrollmentIds.Length > 500 || source.EnrollmentIds.Any(x => x == Guid.Empty)
            || source.Kind == "Lesson" && source.Status is not ("Scheduled" or "Cancelled" or "Completed" or "NoShow")
            || source.Kind == "Assessment" && source.Status is not ("Recorded" or "ReviewRequired" or "Annulled")
            || source.Kind == "Lesson" && (source.StartsAt is null || source.EndsAt is null || source.EndsAt <= source.StartsAt || source.TeacherId is null || source.TeacherId == Guid.Empty))
            throw NotificationErrors.Dependency();
    }
    private void ValidateScope(SourceFact source, RecipientScope scope)
    {
        if (scope.SchoolId != source.SchoolId || scope.SchoolRevision < 1 || scope.UserIds is null || scope.UserIds.Length > options.MaxRecipients
            || scope.UserIds.Any(x => x == Guid.Empty) || scope.UserIds.Distinct().Count() != scope.UserIds.Length || string.IsNullOrWhiteSpace(scope.SchoolName)) throw NotificationErrors.Dependency();
        Fresh(scope.ValidUntil);
    }
    private void ValidateContact(ContactEvidence contact, Guid[] users)
    {
        if (!users.Contains(contact.UserId) || contact.Revision.Length is < 1 or > 100 || string.IsNullOrWhiteSpace(contact.Address) || contact.Address.Length > 254
            || contact.Address.Any(char.IsControl)) throw NotificationErrors.Dependency();
        Fresh(contact.ValidUntil);
    }
    private void Fresh(DateTimeOffset until)
    { if (until <= clock.GetUtcNow() || until > clock.GetUtcNow().AddSeconds(31)) throw new ServiceUnavailableException("EVIDENCE_EXPIRED"); }
}
