using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vairas.Notifications.Business;
using Microsoft.Extensions.Options;

namespace Vairas.Notifications.Workers;


public sealed class DeliveryWorker(IServiceScopeFactory scopes, IOptions<DeliveryOptions> optionsSettings, ILogger<DeliveryWorker> logger) : BackgroundService
{
    private readonly DeliveryOptions options = optionsSettings.Value;
    private long pending, unknown;
    private double oldest;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        _ = NotificationTelemetry.Meter.CreateObservableGauge("notifications.pending", () => Interlocked.Read(ref pending));
        _ = NotificationTelemetry.Meter.CreateObservableGauge("notifications.delivery_unknown", () => Interlocked.Read(ref unknown));
        _ = NotificationTelemetry.Meter.CreateObservableGauge("notifications.oldest_age_seconds", () => Volatile.Read(ref oldest));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IDeliveryStore>();
                var stats = await store.StatsAsync(ct); Interlocked.Exchange(ref pending, stats.Pending); Interlocked.Exchange(ref unknown, stats.Unknown); Volatile.Write(ref oldest, stats.OldestAgeSeconds);
                logger.LogInformation("Notification backlog: {Pending} pending; {Unknown} unknown; {OverdueSeconds} overdue seconds", stats.Pending, stats.Unknown, stats.OldestAgeSeconds);
                foreach (var item in await store.DiscoverAsync(50, ct))
                {
                    using var activity = NotificationTelemetry.Activity.StartActivity("notification.process");
                    activity?.SetTag("notification.id", item.NotificationId);
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<INotificationWorkflow>().ProcessAsync(item, ct); NotificationTelemetry.Processed.Add(1);
                        await using var read = await store.OpenAsync(item.SchoolId, ct);
                        var current = await read.ReadAsync(item.NotificationId, ct);
                        if (current?.Delivery.Status is Domain.NotificationStatus.Failed or Domain.NotificationStatus.DeliveryUnknown)
                            logger.LogWarning("Notification requires investigation: {NotificationId}; status {DeliveryStatus}", item.NotificationId, current.Delivery.Status);
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        NotificationTelemetry.Errors.Add(1);
                        logger.LogWarning("Notification work deferred: {NotificationId}; exception type {ExceptionType}", item.NotificationId, e.GetType().Name);
                        await using var tx = await store.OpenAsync(item.SchoolId, ct); await tx.DeferAsync(item.NotificationId, 30, ct); await tx.CommitAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError("Notification worker unavailable: {ExceptionType}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), ct);
        }
    }
}
