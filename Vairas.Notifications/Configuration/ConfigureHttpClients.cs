using Vairas.Notifications.Integrations;

namespace Vairas.Notifications.Configuration;

public static class ConfigureHttpClients
{
    public static IServiceCollection AddDependencyHttpClients(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddTransient<DependencyCorrelationHandler>();
        services.AddHttpClient("dependencies", client => client.Timeout = TimeSpan.FromSeconds(5))
            .AddHttpMessageHandler<DependencyCorrelationHandler>();
        services.AddHttpClient("gmail", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IWorkloadTokens, WorkloadTokens>();
        services.AddScoped<IDependencyTransport, DependencyTransport>();
        return services;
    }
}
