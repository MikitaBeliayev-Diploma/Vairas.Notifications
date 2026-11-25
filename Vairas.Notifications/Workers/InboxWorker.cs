using System.Globalization;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Vairas.Notifications.Business;
using Microsoft.Extensions.Options;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Workers;

public sealed class InboxWorker(IServiceScopeFactory scopes, IOptions<NotificationBusOptions> optionsSettings, ILogger<InboxWorker> logger) : BackgroundService
{
    private readonly NotificationBusOptions options = optionsSettings.Value;
    public static SourceMessage Decode(ServiceBusReceivedMessage message, string producer)
    {
        string Header(string key) => message.ApplicationProperties.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
        if (!Guid.TryParse(Header("eventId"), out var id) || id == Guid.Empty || message.MessageId != id.ToString("D") || Header("producer") != producer
            || message.Subject != Header("eventType") || !int.TryParse(Header("eventVersion"), out var version)
            || !Guid.TryParse(Header("aggregateId"), out var source) || !long.TryParse(Header("aggregateVersion"), out var revision)
            || !DateTimeOffset.TryParse(Header("occurredAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            || message.Body.ToMemory().Length > 65536) throw new BadRequestException("INVALID_BROKER_ENVELOPE");
        using var json = JsonDocument.Parse(message.Body.ToString());
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("schoolId", out var schoolValue)
            || schoolValue.ValueKind != JsonValueKind.String || !schoolValue.TryGetGuid(out var school)) throw new BadRequestException("INVALID_EVENT_SCHOOL");
        var result = new SourceMessage(id, message.Subject, version, producer, school, source, revision, at, Header("correlationId"), message.Body.ToString());
        SourceMessagePolicy.ValidateAndHash(result); return result;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        while (!ct.IsCancellationRequested)
        {
            try { await ReceiveAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError("Notifications broker connection deferred: {ExceptionType}", e.GetType().Name); }
            if (!ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
    private async Task ReceiveAsync(CancellationToken ct)
    {
        await using var client = string.IsNullOrWhiteSpace(options.ConnectionString) ? new ServiceBusClient(options.Namespace, new DefaultAzureCredential()) : new ServiceBusClient(options.ConnectionString);
        var processors = new List<ServiceBusProcessor>();
        try
        {
            foreach (var (topic, producer) in new[] { (options.SchedulingTopic, "scheduling"), (options.LearningTopic, "learning") })
            {
                var processor = client.CreateProcessor(topic, options.Subscription, new() { AutoCompleteMessages = false, MaxConcurrentCalls = 4, MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5) });
                processor.ProcessMessageAsync += async args =>
                {
                    try
                    {
                        var message = Decode(args.Message, producer);
                        using var activity = NotificationTelemetry.Activity.StartActivity("notification.consume");
                        activity?.AddBaggage("correlationId", message.CorrelationId);
                        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = message.CorrelationId, ["EventId"] = message.EventId });
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<INotificationWorkflow>().ConsumeAsync(message, args.CancellationToken);
                        await args.CompleteMessageAsync(args.Message, args.CancellationToken); NotificationTelemetry.Events.Add(1);
                    }
                    catch (Exception e) when (!args.CancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Notification consumption failed: {ExceptionType}; delivery {DeliveryCount}", e.GetType().Name, args.Message.DeliveryCount);
                        if (e is JsonException || e is BadRequestException or ConflictException or UnprocessableEntityException || args.Message.DeliveryCount >= options.MaxDeliveryCount)
                        {
                            await args.DeadLetterMessageAsync(args.Message, "NotificationsProcessingFailed", "Inspect event identifiers and restore authoritative dependencies before redrive.", args.CancellationToken);
                            NotificationTelemetry.DeadLetters.Add(1);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, args.Message.DeliveryCount))), args.CancellationToken);
                            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken); NotificationTelemetry.Deferrals.Add(1);
                        }
                    }
                };
                processor.ProcessErrorAsync += error => { logger.LogWarning("Notifications broker unavailable: {ExceptionType}", error.Exception.GetType().Name); return Task.CompletedTask; };
                processors.Add(processor); await processor.StartProcessingAsync(ct);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { foreach (var processor in processors) await processor.DisposeAsync(); }
    }
}
