namespace Vairas.Notifications.Configuration.Options;

public sealed class DependencyOptions
{
    public string AuthBaseUrl { get; set; } = "https://localhost:7100/";
    public string UserManagementBaseUrl { get; set; } = "https://localhost:7001/";
    public string SchoolBaseUrl { get; set; } = "https://localhost:7200/";
    public string SchedulingBaseUrl { get; set; } = "https://localhost:7300/";
    public string LearningBaseUrl { get; set; } = "https://localhost:7500/";
    public string ClientId { get; set; } = "notifications";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } = "identity.validate identity.contact.read profiles.contact.read school.notifications.read scheduling.notifications.read learning.notifications.read";
}
