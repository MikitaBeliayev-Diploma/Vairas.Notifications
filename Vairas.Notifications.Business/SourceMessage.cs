using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record SourceMessage(Guid EventId, string EventType, int EventVersion, string Producer, Guid SchoolId, Guid SourceId,
    long SourceRevision, DateTimeOffset OccurredAt, string CorrelationId, string Payload);
