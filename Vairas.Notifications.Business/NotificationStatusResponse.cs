using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

/// <summary>Delivery state. Sent means provider acceptance. No recipient address or body is returned.</summary>
public sealed record NotificationStatusResponse(Guid NotificationId, NotificationStatus Status, long Revision);
