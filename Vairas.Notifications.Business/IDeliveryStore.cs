using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public interface IDeliveryStore
{
    Task<IDeliveryTransaction> OpenAsync(Guid school, CancellationToken ct);
    Task<IReadOnlyList<WorkItem>> DiscoverAsync(int take, CancellationToken ct);
    Task<DeliveryStats> StatsAsync(CancellationToken ct);
}
