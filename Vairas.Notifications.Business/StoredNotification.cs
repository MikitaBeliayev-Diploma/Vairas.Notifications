using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record StoredNotification(Guid SchoolId, Guid NotificationId, Guid RecipientUserId, NotificationDelivery Delivery);
