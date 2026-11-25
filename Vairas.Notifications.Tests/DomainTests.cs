using NUnit.Framework;
using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Unit")]
public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static NotificationDelivery Pending() => new(NotificationStatus.Pending, 1, 0, Now, null, null, null, null, Now);

    [Test]
    public void Template_has_no_delivery_channel_and_preserves_identity_when_updated()
    {
        var template = NotificationTemplate.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationType.LessonScheduled, "Lesson at {{startsAt}}", "{{schoolName}} {{appLink}}", Now);
        var updated = template.Update("New subject", "New body", 1, Now.AddSeconds(1));
        Assert.Multiple(() =>
        {
            Assert.That(updated.TemplateId, Is.EqualTo(template.TemplateId));
            Assert.That(updated.SchoolId, Is.EqualTo(template.SchoolId));
            Assert.That(updated.Revision, Is.EqualTo(2));
            Assert.That(template.SubjectTemplate, Is.EqualTo("Lesson at {{startsAt}}"));
            Assert.That(typeof(NotificationTemplate).GetProperty("Channel"), Is.Null);
        });
    }

    [Test]
    public void Archive_is_terminal_and_revision_is_required()
    {
        var template = NotificationTemplate.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationType.AssessmentPassed, "Result", "Open {{appLink}}", Now);
        AssertRule(() => template.Update("a", "b", 2, Now), "REVISION_CONFLICT");
        var archived = template.Archive(1, Now);
        Assert.That(archived.Status, Is.EqualTo(TemplateStatus.Archived));
        AssertRule(() => archived.Update("a", "b", 2, Now), "TEMPLATE_ARCHIVED");
        AssertRule(() => archived.Archive(2, Now), "TEMPLATE_ARCHIVED");
        AssertRule(() => template.Archive(1, Now.AddSeconds(-1)), "INVALID_TIME");
    }

    [TestCase("", "Body")]
    [TestCase("Subject", " ")]
    [TestCase("Subject\r\nBcc:x", "Body")]
    [TestCase("{{schoolName", "Body")]
    [TestCase("Subject", "{{ schoolName }}")]
    [TestCase("Subject", "Body\0")]
    public void Invalid_templates_fail(string subject, string body) =>
        Assert.Throws<NotificationRuleException>(() => NotificationRenderer.Validate(NotificationType.LessonScheduled, subject, body));

    [TestCase(NotificationType.LessonScheduled)]
    [TestCase(NotificationType.LessonRescheduled)]
    [TestCase(NotificationType.LessonReminder)]
    public void Lesson_placeholders_are_allowlisted(NotificationType type)
    {
        var result = NotificationRenderer.Render(type, "{{startsAt}}", "{{endsAt}}", new Dictionary<string, string> { ["startsAt"] = "10:00", ["endsAt"] = "11:30" });
        Assert.That(result, Is.EqualTo(new NotificationContent("10:00", "11:30")));
    }

    [Test]
    public void Rendering_encodes_values_and_never_evaluates_inserted_tokens()
    {
        var content = NotificationRenderer.Render(NotificationType.AssessmentPassed, "{{schoolName}}", "{{appLink}}",
            new Dictionary<string, string> { ["schoolName"] = "<b>{{appLink}}</b>", ["appLink"] = "https://app.test/result?a=1&b=2" });
        Assert.That(content.Subject, Is.EqualTo("&lt;b&gt;{{appLink}}&lt;/b&gt;"));
        Assert.That(content.Body, Does.Contain("&amp;"));
    }

    [Test]
    public void Rendering_rejects_unknown_missing_unsafe_and_oversize_values()
    {
        AssertRule(() => NotificationRenderer.Validate(NotificationType.AssessmentFailed, "{{startsAt}}", "b"), "UNKNOWN_PLACEHOLDER");
        AssertRule(() => NotificationRenderer.Render(NotificationType.LessonScheduled, "{{schoolName}}", "b", new Dictionary<string, string>()), "MISSING_PLACEHOLDER");
        foreach (var value in new[] { "javascript:alert(1)", "http://app.test", "https://user:pass@app.test" })
            AssertRule(() => NotificationRenderer.Render(NotificationType.AssessmentPassed, "a", "{{appLink}}", new Dictionary<string, string> { ["appLink"] = value }), "INVALID_APP_LINK");
        AssertRule(() => NotificationRenderer.Render(NotificationType.LessonScheduled, "{{schoolName}}", "b", new Dictionary<string, string> { ["schoolName"] = "a\nb" }), "INVALID_PLACEHOLDER");
        AssertRule(() => NotificationRenderer.Render(NotificationType.LessonScheduled, "{{schoolName}}", "b", new Dictionary<string, string> { ["schoolName"] = new('a', 501) }), "CONTENT_TOO_LONG");
        AssertRule(() => NotificationTemplate.Create(Guid.Empty, Guid.NewGuid(), NotificationType.LessonReminder, "a", "b", Now), "VALIDATION_FAILED");
    }

    [Test]
    public void Due_claim_increments_attempt_and_acceptance_is_terminal()
    {
        var sending = Pending().Begin(1, Now);
        var sent = sending.Complete(SubmissionOutcome.Accepted, Now.AddSeconds(1));
        Assert.That(sending.AttemptCount, Is.EqualTo(1));
        Assert.That(sent.Status, Is.EqualTo(NotificationStatus.Sent));
        Assert.That(sent.SentAt, Is.EqualTo(Now.AddSeconds(1)));
        AssertRule(() => sent.Begin(sent.Revision, Now.AddMinutes(1)), "NOT_DISPATCHABLE");
        AssertRule(() => sent.Cancel(sent.Revision, Now.AddMinutes(1)), "CANCELLATION_TOO_LATE");
    }

    [Test]
    public void Unknown_cannot_be_resent_or_cancelled_until_definitive_reconciliation()
    {
        var unknown = Pending().Begin(1, Now).Complete(SubmissionOutcome.Unknown, Now);
        Assert.That(unknown.Status, Is.EqualTo(NotificationStatus.DeliveryUnknown));
        AssertRule(() => unknown.Begin(unknown.Revision, Now.AddHours(1)), "NOT_DISPATCHABLE");
        AssertRule(() => unknown.Cancel(unknown.Revision, Now.AddHours(1)), "CANCELLATION_TOO_LATE");
        AssertRule(() => unknown.Complete(SubmissionOutcome.RetryableRejection, Now), "INVALID_DELIVERY_TRANSITION");
        AssertRule(() => unknown.Complete(SubmissionOutcome.Unknown, Now, true), "RECONCILIATION_REQUIRED");
        Assert.That(unknown.Complete(SubmissionOutcome.Accepted, Now, true).Status, Is.EqualTo(NotificationStatus.Sent));
        Assert.That(unknown.Complete(SubmissionOutcome.RetryableRejection, Now, true).Status, Is.EqualTo(NotificationStatus.Pending));
    }

    [Test]
    public void Known_rejection_retries_after_one_then_five_minutes_and_exhausts_at_three()
    {
        var first = Pending().Begin(1, Now).Complete(SubmissionOutcome.RetryableRejection, Now);
        Assert.That(first.NextAttemptAt, Is.EqualTo(Now.AddMinutes(1)));
        AssertRule(() => first.Begin(first.Revision, Now.AddSeconds(59)), "NOT_DISPATCHABLE");
        var second = first.Begin(first.Revision, Now.AddMinutes(1)).Complete(SubmissionOutcome.RetryableRejection, Now.AddMinutes(1));
        Assert.That(second.NextAttemptAt, Is.EqualTo(Now.AddMinutes(6)));
        var third = second.Begin(second.Revision, Now.AddMinutes(6)).Complete(SubmissionOutcome.RetryableRejection, Now.AddMinutes(6));
        Assert.That(third.Status, Is.EqualTo(NotificationStatus.Failed));
        Assert.That(third.AttemptCount, Is.EqualTo(3));
        AssertRule(() => third.Begin(third.Revision, Now.AddDays(1)), "NOT_DISPATCHABLE");
    }

    [Test]
    public void Permanent_rejection_does_not_retry_and_pending_can_be_cancelled()
    {
        var failed = Pending().Begin(1, Now).Complete(SubmissionOutcome.PermanentRejection, Now);
        Assert.That(failed.Status, Is.EqualTo(NotificationStatus.Failed));
        var cancelled = Pending().Cancel(1, Now);
        Assert.That(cancelled.CancelledAt, Is.EqualTo(Now));
        Assert.That(cancelled.NextAttemptAt, Is.Null);
        AssertRule(() => Pending().Begin(2, Now), "REVISION_CONFLICT");
        AssertRule(() => Pending().Begin(1, Now.AddMinutes(-1)), "INVALID_TIME");
        AssertRule(() => (Pending() with { ScheduledAt = Now.AddHours(1) }).Begin(1, Now), "NOT_DISPATCHABLE");
    }

    [Test]
    public void Provider_key_is_stable_and_bound_to_school_message_and_attempt()
    {
        var school = Guid.NewGuid(); var id = Guid.NewGuid();
        var key = NotificationDelivery.ProviderKey(school, id, 1);
        Assert.That(NotificationDelivery.ProviderKey(school, id, 1), Is.EqualTo(key));
        Assert.That(NotificationDelivery.ProviderKey(school, id, 2), Is.Not.EqualTo(key));
        Assert.That(NotificationDelivery.ProviderKey(Guid.NewGuid(), id, 1), Is.Not.EqualTo(key));
        AssertRule(() => NotificationDelivery.ProviderKey(school, id, 4), "INVALID_ATTEMPT");
    }

    private static void AssertRule(TestDelegate action, string code) => Assert.That(Assert.Throws<NotificationRuleException>(action)!.Code, Is.EqualTo(code));
}
