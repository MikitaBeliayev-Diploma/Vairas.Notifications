using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

/// <summary>Update content with optimistic concurrency.</summary>
public sealed record UpdateTemplateRequest(string SubjectTemplate, string BodyTemplate, long ExpectedRevision);
