using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Vairas.Notifications.HealthChecks;

public sealed class DatabaseReadiness(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT NOT rolsuper AND NOT rolbypassrls
                  AND (SELECT count(*)=5 FROM pg_class c JOIN pg_namespace n ON c.relnamespace=n.oid
                       WHERE n.nspname='notifications' AND c.relname IN ('notification_template','notification','notification_delivery_attempt','notification_inbox','notification_operation')
                         AND c.relrowsecurity AND c.relforcerowsecurity AND c.relowner<> (SELECT oid FROM pg_roles WHERE rolname=current_user))
                  AND has_table_privilege(current_user,'notifications.notification_template','SELECT')
                  AND has_table_privilege(current_user,'notifications.notification_template','INSERT')
                  AND has_table_privilege(current_user,'notifications.notification_template','UPDATE')
                  AND has_table_privilege(current_user,'notifications.notification_operation','SELECT')
                  AND has_table_privilege(current_user,'notifications.notification_operation','INSERT')
                  AND has_table_privilege(current_user,'notifications.notification','SELECT')
                  AND (SELECT bool_and(has_column_privilege(current_user,'notifications.notification',col,'UPDATE'))
                       FROM unnest(ARRAY['status','revision','cancelled_at','next_attempt_at','updated_at']) col)
                  AND NOT has_table_privilege(current_user,'notifications.notification','DELETE')
                  AND NOT has_schema_privilege(current_user,'notifications','CREATE')
                  AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member=(SELECT oid FROM pg_roles WHERE rolname=current_user))
                FROM pg_roles WHERE rolname=current_user
                """);
            return await command.ExecuteScalarAsync(ct) is true ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Schema or runtime permissions are not ready.");
        }
        catch (NpgsqlException) { return HealthCheckResult.Unhealthy("Database is unavailable."); }
    }
}
