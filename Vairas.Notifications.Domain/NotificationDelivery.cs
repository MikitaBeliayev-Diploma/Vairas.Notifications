namespace Vairas.Notifications.Domain;

/// <summary>Delivery state independent of any provider. Only definitive rejection permits another submission.</summary>
public sealed record NotificationDelivery(NotificationStatus Status, long Revision, int AttemptCount,
    DateTimeOffset ScheduledAt, DateTimeOffset? NextAttemptAt, DateTimeOffset? SentAt, DateTimeOffset? CancelledAt,
    string? FailureCode, DateTimeOffset UpdatedAt)
{
    public const int MaxAttempts = 3;

    public NotificationDelivery Begin(long expectedRevision, DateTimeOffset now)
    {
        RequireRevision(expectedRevision, now);
        if (Status != NotificationStatus.Pending || now < ScheduledAt || NextAttemptAt > now || AttemptCount >= MaxAttempts)
            throw new NotificationRuleException("NOT_DISPATCHABLE");
        return this with { Status = NotificationStatus.Sending, Revision = Revision + 1, AttemptCount = AttemptCount + 1,
            NextAttemptAt = null, FailureCode = null, UpdatedAt = now };
    }

    public NotificationDelivery Complete(SubmissionOutcome outcome, DateTimeOffset now, bool reconciled = false)
    {
        if (!Enum.IsDefined(outcome) || AttemptCount is < 1 or > MaxAttempts || now < UpdatedAt
            || Status != NotificationStatus.Sending && !(Status == NotificationStatus.DeliveryUnknown && reconciled))
            throw new NotificationRuleException("INVALID_DELIVERY_TRANSITION");
        if (Status == NotificationStatus.DeliveryUnknown && outcome == SubmissionOutcome.Unknown)
            throw new NotificationRuleException("RECONCILIATION_REQUIRED");
        return outcome switch
        {
            SubmissionOutcome.Accepted => this with { Status = NotificationStatus.Sent, SentAt = now, FailureCode = null, Revision = Revision + 1, UpdatedAt = now },
            SubmissionOutcome.Unknown => this with { Status = NotificationStatus.DeliveryUnknown, FailureCode = "PROVIDER_OUTCOME_UNKNOWN", Revision = Revision + 1, UpdatedAt = now },
            SubmissionOutcome.RetryableRejection when AttemptCount < MaxAttempts => this with { Status = NotificationStatus.Pending,
                NextAttemptAt = now.AddMinutes(AttemptCount == 1 ? 1 : 5), FailureCode = "PROVIDER_REJECTED", Revision = Revision + 1, UpdatedAt = now },
            _ => this with { Status = NotificationStatus.Failed, FailureCode = "PROVIDER_REJECTED", Revision = Revision + 1, UpdatedAt = now }
        };
    }

    public NotificationDelivery Cancel(long expectedRevision, DateTimeOffset now)
    {
        RequireRevision(expectedRevision, now);
        if (Status != NotificationStatus.Pending) throw new NotificationRuleException("CANCELLATION_TOO_LATE");
        return this with { Status = NotificationStatus.Cancelled, CancelledAt = now, NextAttemptAt = null, Revision = Revision + 1, UpdatedAt = now };
    }

    public static string ProviderKey(Guid school, Guid notification, int attempt)
    {
        if (school == Guid.Empty || notification == Guid.Empty || attempt is < 1 or > MaxAttempts) throw new NotificationRuleException("INVALID_ATTEMPT");
        return $"{school:N}/{notification:N}/{attempt}";
    }

    private void RequireRevision(long expected, DateTimeOffset now)
    {
        if (expected <= 0 || Revision != expected) throw new NotificationRuleException("REVISION_CONFLICT");
        if (now < UpdatedAt) throw new NotificationRuleException("INVALID_TIME");
    }
}
