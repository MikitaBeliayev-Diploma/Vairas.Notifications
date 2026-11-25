using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

/// <summary>School-owned template and its current revision.</summary>
public sealed record TemplateResponse(Guid TemplateId, NotificationType Type, string SubjectTemplate, string BodyTemplate,
    TemplateStatus Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
