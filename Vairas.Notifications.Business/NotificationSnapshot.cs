using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record NotificationSnapshot(Guid SchoolId, Guid NotificationId, Guid RecipientUserId, Guid? TemplateId, long? TemplateRevision,
    NotificationType Type, string Subject, string Body, string DestinationAddress, string ContactRevision, string SourceContext, Guid SourceId,
    long SourceRevision, Guid SourceEventId, string DispatchKey, DateTimeOffset CreatedAt, string CorrelationId = "legacy");
