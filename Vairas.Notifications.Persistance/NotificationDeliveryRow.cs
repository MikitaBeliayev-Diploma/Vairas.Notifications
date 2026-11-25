using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Persistance;

/// <summary>Public read/cancellation sessions select delivery metadata without message content.</summary>
internal sealed class NotificationDeliveryRow
{
    public Guid SchoolId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid RecipientUserId { get; set; }
    public NotificationStatus Status { get; set; }
    public long Revision { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
