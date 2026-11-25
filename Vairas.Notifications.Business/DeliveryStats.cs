using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record DeliveryStats(long Pending, long Unknown, double OldestAgeSeconds);
