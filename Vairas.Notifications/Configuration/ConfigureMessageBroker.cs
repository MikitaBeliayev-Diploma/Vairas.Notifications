using Vairas.Notifications.Workers;

namespace Vairas.Notifications.Configuration;

public static class ConfigureMessageBroker
{
    public static IServiceCollection AddBackgroundWorkers(this IServiceCollection services)
    {
        services.AddHostedService<DeliveryWorker>();
        services.AddHostedService<InboxWorker>();
        return services;
    }
}
