using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Vairas.Notifications.Business;
using Vairas.Notifications.Workers;
using Microsoft.Extensions.Options;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.HealthChecks;

public sealed class WorkerReadiness(IServiceProvider services, IOptions<DeliveryOptions> deliverySettings, IOptions<NotificationBusOptions> busSettings) : IHealthCheck
{
    private readonly NotificationBusOptions bus = busSettings.Value;
    private readonly DeliveryOptions delivery = deliverySettings.Value;
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!delivery.Enabled && !bus.Enabled) return HealthCheckResult.Healthy("Workers disabled.");
        try
        {
            await using var command = services.GetRequiredKeyedService<NpgsqlDataSource>("delivery").CreateCommand("""
                SELECT NOT rolsuper AND NOT rolbypassrls AND NOT has_schema_privilege(current_user,'notifications','CREATE')
                AND has_table_privilege(current_user,'notifications.notification','INSERT')
                AND has_table_privilege(current_user,'notifications.notification_delivery_attempt','INSERT')
                AND has_table_privilege(current_user,'notifications.notification_inbox','INSERT')
                AND has_table_privilege(current_user,'notifications.notification_work','SELECT')
                AND NOT has_table_privilege(current_user,'notifications.notification_template','UPDATE')
                AND NOT has_table_privilege(current_user,'notifications.notification','DELETE')
                AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member=(SELECT oid FROM pg_roles WHERE rolname=current_user))
                AND (SELECT count(*)=5 FROM pg_class c JOIN pg_namespace n ON c.relnamespace=n.oid
                  WHERE n.nspname='notifications' AND c.relname IN ('notification_template','notification','notification_delivery_attempt','notification_inbox','notification_operation')
                    AND c.relrowsecurity AND c.relforcerowsecurity AND c.relowner<>(SELECT oid FROM pg_roles WHERE rolname=current_user))
                FROM pg_roles WHERE rolname=current_user
                """);
            return await command.ExecuteScalarAsync(ct) is true ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Worker permissions are not ready.");
        }
        catch (NpgsqlException) { return HealthCheckResult.Unhealthy("Worker database unavailable."); }
    }
}
