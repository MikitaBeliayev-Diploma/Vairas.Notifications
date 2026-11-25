using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

/// <summary>Create an active school template. Channel belongs to a prepared notification.</summary>
public sealed record CreateTemplateRequest(NotificationType Type, string SubjectTemplate, string BodyTemplate);
