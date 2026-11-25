using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

/// <summary>Immutable successful command result; replay returns the original resource.</summary>
public sealed record CommandResponse<T>(Guid OperationId, T Resource, bool Replayed);
