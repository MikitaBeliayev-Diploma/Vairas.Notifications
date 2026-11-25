extern alias NotificationsApi;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using Vairas.Notifications.Business;
using Vairas.Notifications.Integrations;
using Vairas.Notifications.Persistance;

namespace Vairas.Notifications.Tests;

public sealed class AuthorityTransport : IDependencyTransport
{
    public Guid Identity { get; } = Guid.NewGuid();
    public Guid Session { get; } = Guid.NewGuid();
    public Guid User { get; set; } = Guid.NewGuid();
    public bool Administrator { get; set; } = true;
    public bool ActiveSession { get; set; } = true;
    public bool ActiveProfile { get; set; } = true;
    public bool ActiveSchool { get; set; } = true;
    public bool Unavailable { get; set; }
    public Guid? MembershipUser { get; set; }
    public DateTimeOffset? MembershipEnd { get; set; }
    public bool WrongSessionBinding { get; set; }

    public void Reset()
    { Administrator = true; ActiveSession = true; ActiveProfile = true; ActiveSchool = true; Unavailable = false; MembershipUser = null; MembershipEnd = null; WrongSessionBinding = false; }

    public async Task<T> SendAsync<T>(string origin, string route, object? body, CancellationToken ct, string? userToken = null)
    {
        using var response = await RequestAsync(origin, route, body, ct, userToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) throw new ForbiddenException("ACCESS_DENIED");
        return (await response.Content.ReadFromJsonAsync<T>(NotificationService.Json, ct))!;
    }

    public Task<HttpResponseMessage> RequestAsync(string origin, string route, object? body, CancellationToken ct, string? userToken = null)
    {
        if (Unavailable) throw NotificationErrors.Dependency();
        object result;
        if (route == "internal/v1/sessions/validate") result = new { active = ActiveSession, identityId = WrongSessionBinding ? Guid.NewGuid() : Identity, sessionId = Session, validUntil = DateTimeOffset.UtcNow.AddMinutes(1) };
        else if (route == "api/v1/users/me") result = new { userId = User, status = ActiveProfile ? "Active" : "Inactive" };
        else if (route.EndsWith("/memberships", StringComparison.Ordinal))
        {
            if (!Administrator) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            result = new[] { new { userId = MembershipUser ?? User, role = "Administrator", startedAt = DateTimeOffset.UtcNow.AddDays(-1), endedAt = MembershipEnd } };
        }
        else result = new { schoolId = Guid.Parse(route.Split('/')[3]), status = ActiveSchool ? "Active" : "Inactive" };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: NotificationService.Json) });
    }
}
