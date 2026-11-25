using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record OperationReceipt(Guid SchoolId, Guid OperationId, Guid ActorUserId, string CommandName,
    Guid TargetId, string RequestHash, string AuthorityBasis, long ResultingRevision, string ResponseJson, DateTimeOffset RecordedAt);
