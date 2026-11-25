using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record DeliveryAttempt(Guid AttemptId, Guid NotificationId, int Number, string ProviderKey, AttemptStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null, string? ProviderMessageId = null, string? ErrorCode = null);
