using Microsoft.Extensions.Options;
using Npgsql;
using Vairas.Notifications.Business;
using Vairas.Notifications.Configuration.Options;
using Vairas.Notifications.Persistance;

namespace Vairas.Notifications.Configuration;

public static class ConfigureDatabase
{
    public const string DeliveryDataSource = "delivery";

    public static IServiceCollection AddNotificationDatabase(this IServiceCollection services)
    {
        services.AddSingleton(provider => NpgsqlDataSource.Create(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Notifications));
        services.AddScoped<INotificationStore, NotificationStore>();
        // The delivery worker connects with its own, separately granted database role.
        services.AddKeyedSingleton<NpgsqlDataSource>(DeliveryDataSource, (provider, _) =>
            NpgsqlDataSource.Create(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Delivery));
        services.AddSingleton<IDeliveryStore>(provider => new DeliveryStore(provider.GetRequiredKeyedService<NpgsqlDataSource>(DeliveryDataSource)));
        return services;
    }
}
