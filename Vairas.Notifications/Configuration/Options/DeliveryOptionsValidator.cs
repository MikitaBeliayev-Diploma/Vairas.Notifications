using Microsoft.Extensions.Options;
using Vairas.Notifications.Business;

namespace Vairas.Notifications.Configuration.Options;

public sealed class DeliveryOptionsValidator(IOptions<GmailOptions> gmail) : IValidateOptions<DeliveryOptions>
{
    public ValidateOptionsResult Validate(string? name, DeliveryOptions options)
    {
        if (!OriginValidation.IsExplicitHttpsOrigin(options.ApplicationOrigin) || new Uri(options.ApplicationOrigin).AbsolutePath != "/")
            return ValidateOptionsResult.Fail("Delivery:ApplicationOrigin must be an HTTPS origin.");
        if (options.ReminderMinutes is < 1 or > 10080 || options.SendingTimeoutSeconds is < 60 or > 600
            || options.PollSeconds is < 1 or > 60 || options.MaxRecipients is < 1 or > 500)
            return ValidateOptionsResult.Fail("Invalid delivery bounds.");
        if (options.Enabled && !gmail.Value.Enabled)
            return ValidateOptionsResult.Fail("Enabled delivery requires configured Gmail.");
        return ValidateOptionsResult.Success;
    }
}
