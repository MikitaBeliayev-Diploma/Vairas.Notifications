namespace Vairas.Notifications.Configuration;

public static class ConfigureAuthorization
{
    public static IServiceCollection AddEntryAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization();
        return services;
    }
}
