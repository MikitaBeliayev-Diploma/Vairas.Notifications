using Microsoft.Extensions.Options;

namespace Vairas.Notifications.Configuration.Options;

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        if (!OriginValidation.IsExplicitHttpsOrigin(options.Authority) || !OriginValidation.IsExplicitHttpsOrigin(options.Issuer))
            return ValidateOptionsResult.Fail("Authentication requires absolute HTTPS origins without credentials.");
        if (string.IsNullOrWhiteSpace(options.UserAudience))
            return ValidateOptionsResult.Fail("Supply Authentication:UserAudience.");
        return ValidateOptionsResult.Success;
    }
}
