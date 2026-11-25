using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed record InboxReceipt(string Hash, string Outcome, string Code);
