using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public interface IEmailProvider
{
    Task<ProviderResult> SubmitAsync(NotificationSnapshot snapshot, DeliveryAttempt attempt, CancellationToken ct);
    Task<ProviderResult> ReconcileAsync(NotificationSnapshot snapshot, DeliveryAttempt attempt, CancellationToken ct);
}
