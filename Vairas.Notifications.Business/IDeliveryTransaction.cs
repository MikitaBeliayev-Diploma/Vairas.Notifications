using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public interface IDeliveryTransaction : IAsyncDisposable
{
    Task<DateTimeOffset> NowAsync(CancellationToken ct);
    Task<InboxReceipt?> InboxAsync(SourceMessage message, CancellationToken ct);
    Task RecordInboxAsync(SourceMessage message, string hash, string outcome, string code, CancellationToken ct);
    Task<IReadOnlyList<NotificationTemplate>> TemplatesAsync(CancellationToken ct);
    Task CancelObsoleteAsync(string context, Guid source, long revision, bool all, CancellationToken ct);
    Task InsertAsync(NotificationSnapshot snapshot, NotificationDelivery delivery, string correlationId, CancellationToken ct);
    Task<DeliveryItem?> ReadAsync(Guid id, CancellationToken ct);
    Task SaveAsync(DeliveryItem item, NotificationDelivery delivery, DeliveryAttempt? attempt, CancellationToken ct);
    Task DeferAsync(Guid id, int seconds, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
}
