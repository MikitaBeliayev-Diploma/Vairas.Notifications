namespace Vairas.Notifications.Domain;

public sealed class NotificationRuleException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
