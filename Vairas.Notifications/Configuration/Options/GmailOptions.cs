namespace Vairas.Notifications.Configuration.Options;

public sealed class GmailOptions
{
    public bool Enabled { get; set; }
    public string Sender { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public const string DefaultApiBaseUrl = "https://gmail.googleapis.com/gmail/v1/users/me/";
    public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";
    /// <summary>Development-only override for a local Gmail API double; startup rejects any other value outside Development.</summary>
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    /// <summary>Development-only override for a local OAuth token double; startup rejects any other value outside Development.</summary>
    public string TokenUrl { get; set; } = DefaultTokenUrl;
}
