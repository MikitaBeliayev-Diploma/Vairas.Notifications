using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record DeliveryItem(NotificationSnapshot Snapshot, NotificationDelivery Delivery, DeliveryAttempt? Attempt);
