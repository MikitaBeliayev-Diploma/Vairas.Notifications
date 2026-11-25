namespace Vairas.Notifications.Configuration.Options;

/// <summary>Bound from "ConnectionStrings": request runtime role and the separate delivery worker role.</summary>
public sealed class DatabaseOptions
{
    public string Notifications { get; set; } = "";
    public string Delivery { get; set; } = "";
}
