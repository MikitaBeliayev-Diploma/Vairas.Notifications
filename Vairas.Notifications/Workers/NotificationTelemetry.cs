using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vairas.Notifications.Business;
using Microsoft.Extensions.Options;

namespace Vairas.Notifications.Workers;


public static class NotificationTelemetry
{
    public const string Name = "Vairas.Notifications";
    public static readonly ActivitySource Activity = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Processed = Meter.CreateCounter<long>("notifications.work.processed");
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("notifications.work.errors");
    public static readonly Counter<long> Events = Meter.CreateCounter<long>("notifications.events.applied");
    public static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>("notifications.events.dead_lettered");
    public static readonly Counter<long> Deferrals = Meter.CreateCounter<long>("notifications.events.deferred");
}
