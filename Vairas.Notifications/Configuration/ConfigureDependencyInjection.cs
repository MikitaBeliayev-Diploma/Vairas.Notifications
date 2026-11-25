using Vairas.Notifications.Business;
using Vairas.Notifications.Integrations;

namespace Vairas.Notifications.Configuration;

/// <summary>Application services; infrastructure registrations live in the dedicated Configure* classes.</summary>
public static class ConfigureDependencyInjection
{
    public static IServiceCollection AddNotificationServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationActorFactory, NotificationActorFactory>();
        services.AddScoped<INotificationOwners, NotificationOwners>();
        services.AddScoped<INotificationWorkflow, NotificationWorkflow>();
        services.AddSingleton<IEmailProvider, GmailEmailProvider>();
        return services;
    }
}
