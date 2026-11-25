using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record RecipientScope(Guid SchoolId, string SchoolName, long SchoolRevision, Guid[] UserIds, DateTimeOffset ValidUntil);
