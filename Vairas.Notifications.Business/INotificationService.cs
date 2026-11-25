using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public interface INotificationService
{
    Task<IReadOnlyList<TemplateResponse>> TemplatesAsync(Guid school, NotificationActor actor, CancellationToken ct);
    Task<TemplateResponse> TemplateAsync(Guid school, Guid template, NotificationActor actor, CancellationToken ct);
    Task<CommandResponse<TemplateResponse>> CreateTemplateAsync(Guid school, Guid operation, CreateTemplateRequest request, NotificationActor actor, CancellationToken ct);
    Task<CommandResponse<TemplateResponse>> UpdateTemplateAsync(Guid school, Guid template, Guid operation, UpdateTemplateRequest request, NotificationActor actor, CancellationToken ct);
    Task<CommandResponse<TemplateResponse>> ArchiveTemplateAsync(Guid school, Guid template, Guid operation, RevisionRequest request, NotificationActor actor, CancellationToken ct);
    Task<NotificationStatusResponse> StatusAsync(Guid school, Guid notification, NotificationActor actor, CancellationToken ct);
    Task<CommandResponse<NotificationStatusResponse>> CancelAsync(Guid school, Guid notification, Guid operation, RevisionRequest request, NotificationActor actor, CancellationToken ct);
}
