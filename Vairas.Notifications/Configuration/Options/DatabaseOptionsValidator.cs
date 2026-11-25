using Microsoft.Extensions.Options;
using Vairas.Notifications.Business;

namespace Vairas.Notifications.Configuration.Options;

public sealed class DatabaseOptionsValidator(IOptions<DeliveryOptions> delivery, IOptions<NotificationBusOptions> bus) : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Notifications))
            return ValidateOptionsResult.Fail("Supply ConnectionStrings:Notifications.");
        if ((delivery.Value.Enabled || bus.Value.Enabled) && string.IsNullOrWhiteSpace(options.Delivery))
            return ValidateOptionsResult.Fail("Supply a separate Delivery worker connection.");
        return ValidateOptionsResult.Success;
    }
}
