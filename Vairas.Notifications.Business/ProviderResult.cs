using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record ProviderResult(SubmissionOutcome Outcome, string? MessageId = null, string? ErrorCode = null);
