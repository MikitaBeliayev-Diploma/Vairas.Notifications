namespace Vairas.Notifications.Configuration.Options;

/// <summary>Bound from "Authentication": user-token validation settings.</summary>
public sealed class JwtOptions
{
    public string Authority { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string UserAudience { get; set; } = "";
}
