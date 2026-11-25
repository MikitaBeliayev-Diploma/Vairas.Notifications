using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

/// <summary>Expected revision for archive or cancellation.</summary>
public sealed record RevisionRequest(long ExpectedRevision);
