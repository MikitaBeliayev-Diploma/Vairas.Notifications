using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public interface INotificationStore
{
    Task<INotificationSession> OpenAsync(Guid school, bool write, CancellationToken ct);
}
