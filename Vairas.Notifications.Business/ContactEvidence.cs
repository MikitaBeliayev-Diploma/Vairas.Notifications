using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record ContactEvidence(Guid UserId, bool Eligible, string? Address, string Revision, DateTimeOffset ValidUntil);
