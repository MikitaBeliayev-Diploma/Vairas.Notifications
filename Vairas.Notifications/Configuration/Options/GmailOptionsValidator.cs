using Microsoft.Extensions.Options;

namespace Vairas.Notifications.Configuration.Options;

public sealed class GmailOptionsValidator(IHostEnvironment environment) : IValidateOptions<GmailOptions>
{
    public ValidateOptionsResult Validate(string? name, GmailOptions options)
    {
        if (options.ApiBaseUrl != GmailOptions.DefaultApiBaseUrl || options.TokenUrl != GmailOptions.DefaultTokenUrl)
        {
            if (!environment.IsDevelopment())
                return ValidateOptionsResult.Fail("Gmail endpoint overrides are restricted to Development.");
            if (!OriginValidation.IsExplicitHttpsOrigin(options.ApiBaseUrl) || !OriginValidation.IsExplicitHttpsOrigin(options.TokenUrl))
                return ValidateOptionsResult.Fail("Gmail endpoint overrides must be absolute HTTPS URLs.");
            if (!options.ApiBaseUrl.EndsWith('/'))
                return ValidateOptionsResult.Fail("Gmail API base URL must end with '/'.");
        }
        if (options.Enabled && (string.IsNullOrWhiteSpace(options.Sender) || string.IsNullOrWhiteSpace(options.ClientId)
            || string.IsNullOrWhiteSpace(options.ClientSecret) || string.IsNullOrWhiteSpace(options.RefreshToken)))
            return ValidateOptionsResult.Fail("Enabled Gmail requires sender and OAuth credentials.");
        return ValidateOptionsResult.Success;
    }
}
