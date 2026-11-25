using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Vairas.Notifications.Business;

namespace Vairas.Notifications.Integrations;

public interface IDependencyTransport
{
    Task<T> SendAsync<T>(string origin, string route, object? body, CancellationToken ct, string? userToken = null);
    Task<HttpResponseMessage> RequestAsync(string origin, string route, object? body, CancellationToken ct, string? userToken = null);
}
