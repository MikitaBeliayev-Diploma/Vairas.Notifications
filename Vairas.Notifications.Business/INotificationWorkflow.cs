using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public interface INotificationWorkflow
{
    Task ConsumeAsync(SourceMessage message, CancellationToken ct);
    Task ProcessAsync(WorkItem item, CancellationToken ct);
}
