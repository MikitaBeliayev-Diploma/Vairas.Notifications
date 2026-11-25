using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed class DeliveryOptions
{
    public bool Enabled { get; set; }
    public string ApplicationOrigin { get; set; } = "https://localhost:4200";
    public int ReminderMinutes { get; set; } = 1440;
    public int SendingTimeoutSeconds { get; set; } = 120;
    public int PollSeconds { get; set; } = 2;
    public int MaxRecipients { get; set; } = 500;
}
