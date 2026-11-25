using Microsoft.Extensions.Options;
using Vairas.Notifications.Business;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Configuration;

/// <summary>Binds and validates every strongly typed options section; invalid required settings fail at startup.</summary>
public static class ConfigureOptions
{
    public static IServiceCollection AddNotificationOptions(this IServiceCollection services, IConfiguration configuration)
    {
        AddValidated<DatabaseOptions, DatabaseOptionsValidator>(services, configuration.GetSection("ConnectionStrings"));
        AddValidated<JwtOptions, JwtOptionsValidator>(services, configuration.GetSection("Authentication"));
        AddValidated<DependencyOptions, DependencyOptionsValidator>(services, configuration.GetSection("Dependencies"));
        AddValidated<DeliveryOptions, DeliveryOptionsValidator>(services, configuration.GetSection("Delivery"));
        AddValidated<GmailOptions, GmailOptionsValidator>(services, configuration.GetSection("Gmail"));
        AddValidated<NotificationBusOptions, NotificationBusOptionsValidator>(services, configuration.GetSection("ServiceBus"));
        // The business layer takes the plain settings object so it does not depend on the options infrastructure.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<DeliveryOptions>>().Value);
        return services;
    }

    private static void AddValidated<TOptions, TValidator>(IServiceCollection services, IConfigurationSection section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
    }
}
