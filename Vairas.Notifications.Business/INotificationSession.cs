using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

/// <summary>One local tenant transaction; never hold it across remote authority or provider calls.</summary>
public interface INotificationSession : IAsyncDisposable
{
    Task<DateTimeOffset> NowAsync(CancellationToken ct);
    Task<IReadOnlyList<NotificationTemplate>> TemplatesAsync(CancellationToken ct);
    Task<NotificationTemplate?> TemplateAsync(Guid id, CancellationToken ct);
    Task InsertTemplateAsync(NotificationTemplate template, CancellationToken ct);
    Task UpdateTemplateAsync(NotificationTemplate template, long expectedRevision, CancellationToken ct);
    Task<StoredNotification?> NotificationAsync(Guid id, CancellationToken ct);
    Task UpdateDeliveryAsync(Guid id, NotificationDelivery delivery, long expectedRevision, CancellationToken ct);
    Task<OperationReceipt?> ReceiptAsync(Guid operation, CancellationToken ct);
    Task InsertReceiptAsync(OperationReceipt receipt, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
}
