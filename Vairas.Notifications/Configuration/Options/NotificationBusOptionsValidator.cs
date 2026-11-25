using Microsoft.Extensions.Options;

namespace Vairas.Notifications.Configuration.Options;

/// <summary>A disabled broker integration needs no namespace or credentials.</summary>
public sealed class NotificationBusOptionsValidator : IValidateOptions<NotificationBusOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationBusOptions options) =>
        options.Enabled && (string.IsNullOrWhiteSpace(options.Subscription) || string.IsNullOrWhiteSpace(options.SchedulingTopic)
            || string.IsNullOrWhiteSpace(options.LearningTopic) || options.MaxDeliveryCount is < 1 or > 100
            || string.IsNullOrWhiteSpace(options.ConnectionString) && string.IsNullOrWhiteSpace(options.Namespace))
            ? ValidateOptionsResult.Fail("Configure Service Bus namespace/credentials and subscriptions.")
            : ValidateOptionsResult.Success;
}
