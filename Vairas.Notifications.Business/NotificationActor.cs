using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record NotificationActor(Guid SchoolId, Guid UserId, bool Administrator, DateTimeOffset ValidUntil, string AuthorityBasis);
