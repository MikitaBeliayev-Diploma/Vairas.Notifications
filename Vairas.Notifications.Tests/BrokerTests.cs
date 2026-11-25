using Azure.Messaging.ServiceBus;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Vairas.Notifications.Business;
using Vairas.Notifications.Persistance;
using Vairas.Notifications.Workers;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Broker"), NonParallelizable]
public sealed class BrokerTests
{
    [Test]
    public async Task Azure_SDK_consumption_deduplicates_and_dead_letters_poison_without_acknowledging_outages()
    {
        var connection = Environment.GetEnvironmentVariable("VAIRAS_TEST_SERVICEBUS_CONNECTION");
        Assert.That(connection, Is.Not.Null.And.Not.Empty, "Run scripts/test-broker.ps1 with the configured emulator or dedicated test namespace.");
        await using var pg = new PostgresFixture(); await pg.StartAsync();
        await using var data = NpgsqlDataSource.Create(pg.WorkerConnection); var store = new DeliveryStore(data);
        var school = Guid.NewGuid(); var source = Guid.NewGuid(); var user = Guid.NewGuid(); var teacher = Guid.NewGuid(); var enrollment = Guid.NewGuid();
        var owners = new DeliveryWorkflowTests.Owners { Fact = new(school, source, 1, "Lesson", "Scheduled", DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(2).AddHours(1), [enrollment], teacher, null, null),
            Scope = new(school, "Synthetic broker school", 1, [user], DateTimeOffset.UtcNow.AddSeconds(30)), Contact = new(user, true, "broker@example.test", "verified-contact", DateTimeOffset.UtcNow.AddSeconds(30)) };
        var provider = new DeliveryWorkflowTests.Provider();
        long acknowledged = 0, quarantined = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        { if (instrument.Meter.Name == NotificationTelemetry.Name && instrument.Name is "notifications.events.applied" or "notifications.events.dead_lettered") observer.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, count, _, _) =>
        { if (instrument.Name == "notifications.events.applied") Interlocked.Add(ref acknowledged, count); else Interlocked.Add(ref quarantined, count); });
        listener.Start();
        await using var services = new ServiceCollection().AddSingleton<IDeliveryStore>(store).AddSingleton<INotificationOwners>(owners).AddSingleton<IEmailProvider>(provider)
            .AddSingleton(new DeliveryOptions()).AddSingleton(TimeProvider.System).AddScoped<INotificationWorkflow, NotificationWorkflow>().BuildServiceProvider();
        var options = new NotificationBusOptions { Enabled = true, ConnectionString = connection!, MaxDeliveryCount = 3 };
        InboxWorker CreateWorker() => new(services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(options), NullLogger<InboxWorker>.Instance);
        var worker = CreateWorker();
        await worker.StartAsync(default);
        await using var client = new ServiceBusClient(connection); await using var sender = client.CreateSender(options.SchedulingTopic);
        var message = new SourceMessage(Guid.NewGuid(), "LessonScheduled", 1, "scheduling", school, source, 1, DateTimeOffset.UtcNow, "broker-fixture",
            System.Text.Json.JsonSerializer.Serialize(new { schoolId = school, lessonId = source, lessonKind = "Practical", teacherId = teacher, enrollmentId = enrollment, vehicleId = source,
                startsAt = owners.Fact.StartsAt, endsAt = owners.Fact.EndsAt }, NotificationService.Json));
        try
        {
            await sender.SendMessageAsync(ToMessage(message));
            await Wait(async () => { await using var tx = await store.OpenAsync(school, default); return await tx.InboxAsync(message, default) is not null; });
            await Wait(() => Task.FromResult(Interlocked.Read(ref acknowledged) == 1));
            await sender.SendMessageAsync(ToMessage(message));
            await Wait(() => Task.FromResult(Interlocked.Read(ref acknowledged) == 2));
            await Wait(async () => (await store.StatsAsync(default)).Pending == 2);
            var changed = message with { CorrelationId = "changed-envelope" };
            await sender.SendMessageAsync(ToMessage(changed));
            await InspectDeadLetter(1);
            var poison = ToMessage(message with { EventId = Guid.NewGuid() }); poison.ApplicationProperties["producer"] = "learning";
            await sender.SendMessageAsync(poison);
            await InspectDeadLetter(2);
            owners.Unavailable = true;
            var unavailable = message with { EventId = Guid.NewGuid() }; await sender.SendMessageAsync(ToMessage(unavailable));
            await Wait(() => Task.FromResult(Interlocked.Read(ref quarantined) == 3));
            await worker.StopAsync(default);
            await using (var tx = await store.OpenAsync(school, default)) Assert.That(await tx.InboxAsync(unavailable, default), Is.Null);
            Assert.That(provider.Submissions, Is.Zero); Assert.That((await store.StatsAsync(default)).Pending, Is.EqualTo(2));
            // Redrive the exact original message after repairing its dependency; receipt commits before ACK.
            await using (var dead = client.CreateReceiver(options.SchedulingTopic, options.Subscription, new() { SubQueue = SubQueue.DeadLetter }))
            {
                var rejected = await dead.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.That(rejected, Is.Not.Null);
                Assert.That(rejected!.MessageId, Is.EqualTo(unavailable.EventId.ToString("D")));
                owners.Unavailable = false; await sender.SendMessageAsync(ToMessage(unavailable)); await dead.CompleteMessageAsync(rejected);
            }
            await RestartWorker();
            await Wait(async () => { await using var tx = await store.OpenAsync(school, default); return await tx.InboxAsync(unavailable, default) is not null; });
            await Wait(() => Task.FromResult(Interlocked.Read(ref acknowledged) == 3));
            Assert.That((await store.StatsAsync(default)).Pending, Is.EqualTo(2));
        }
        finally { await worker.StopAsync(default); worker.Dispose(); }

        async Task RestartWorker()
        { worker.Dispose(); worker = CreateWorker(); await worker.StartAsync(default); }

        async Task InspectDeadLetter(long expected)
        {
            await Wait(() => Task.FromResult(Interlocked.Read(ref quarantined) == expected));
            // The local emulator faults its message cursor when active and DLQ receivers overlap.
            // Inspect retained messages with the consumer stopped; live Azure concurrency is a release check.
            await worker.StopAsync(default);
            await using (var dead = client.CreateReceiver(options.SchedulingTopic, options.Subscription, new() { SubQueue = SubQueue.DeadLetter }))
            {
                var rejected = await dead.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.That(rejected, Is.Not.Null);
                Assert.That(rejected!.DeadLetterReason, Is.EqualTo("NotificationsProcessingFailed")); await dead.CompleteMessageAsync(rejected);
            }
            await RestartWorker();
        }
    }
    public static ServiceBusMessage ToMessage(SourceMessage message)
    {
        var result = new ServiceBusMessage(BinaryData.FromString(message.Payload)) { MessageId = message.EventId.ToString("D"), Subject = message.EventType, CorrelationId = message.CorrelationId, ContentType = "application/json" };
        result.ApplicationProperties["eventId"] = message.EventId.ToString("D"); result.ApplicationProperties["eventType"] = message.EventType; result.ApplicationProperties["eventVersion"] = message.EventVersion;
        result.ApplicationProperties["producer"] = message.Producer; result.ApplicationProperties["aggregateId"] = message.SourceId.ToString("D"); result.ApplicationProperties["aggregateVersion"] = message.SourceRevision;
        result.ApplicationProperties["occurredAt"] = message.OccurredAt.ToString("O"); result.ApplicationProperties["correlationId"] = message.CorrelationId; return result;
    }
    public static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < until) { if (await condition()) return; await Task.Delay(200); }
        Assert.Fail("Expected broker processing outcome was not observed.");
    }
}
