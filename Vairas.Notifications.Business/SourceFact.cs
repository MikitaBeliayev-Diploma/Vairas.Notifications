using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record SourceFact(Guid SchoolId, Guid SourceId, long Revision, string Kind, string Status, DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt, Guid[] EnrollmentIds, Guid? TeacherId, DateTimeOffset? AssessedAt, string? Outcome);
