using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Vairas.Notifications.Business;
using Microsoft.Extensions.Options;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Integrations;

public interface INotificationActorFactory
{
    Task<NotificationActor> CreateAsync(HttpContext http, Guid school, bool administrator, CancellationToken ct);
}
