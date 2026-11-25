using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record WorkItem(Guid SchoolId, Guid NotificationId);
