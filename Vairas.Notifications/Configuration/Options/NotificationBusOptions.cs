namespace Vairas.Notifications.Configuration.Options;

public sealed class NotificationBusOptions
{
    public bool Enabled { get; set; }
    public string Namespace { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string Subscription { get; set; } = "notifications";
    public string SchedulingTopic { get; set; } = "vairas.scheduling.events.v1";
    public string LearningTopic { get; set; } = "vairas.learning.events.v1";
    public int MaxDeliveryCount { get; set; } = 10;
}
