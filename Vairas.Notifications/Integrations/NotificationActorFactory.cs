using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Vairas.Notifications.Business;
using Microsoft.Extensions.Options;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Integrations;

/// <summary>Resolve IdentityId to canonical UserId and check fresh session, profile and school membership.</summary>
public sealed class NotificationActorFactory(IDependencyTransport transport, IOptions<DependencyOptions> optionsSettings, TimeProvider clock) : INotificationActorFactory
{
    private readonly DependencyOptions options = optionsSettings.Value;
    public async Task<NotificationActor> CreateAsync(HttpContext http, Guid school, bool administrator, CancellationToken ct)
    {
        if (school == Guid.Empty) throw NotificationErrors.Invalid();
        if (http.User.Identity?.IsAuthenticated != true || !Guid.TryParse(http.User.FindFirstValue("sub"), out var identity) || identity == Guid.Empty
            || !Guid.TryParse(http.User.FindFirstValue("sid"), out var sid) || sid == Guid.Empty)
            throw new UnauthorizedException("INVALID_IDENTITY");
        var started = clock.GetUtcNow();
        var session = await transport.SendAsync<Session>(options.AuthBaseUrl, "internal/v1/sessions/validate", new { identityId = identity, sessionId = sid }, ct);
        if (session.IdentityId != identity || session.SessionId != sid || session.ValidUntil <= clock.GetUtcNow()) throw NotificationErrors.Dependency();
        if (!session.Active) throw new UnauthorizedException("SESSION_INVALID");
        var raw = http.Request.Headers.Authorization.ToString();
        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedException("INVALID_IDENTITY");
        var token = raw[7..];
        var profile = await transport.SendAsync<Profile>(options.UserManagementBaseUrl, "api/v1/users/me", null, ct, token);
        if (profile.UserId == Guid.Empty || profile.Status != "Active") throw new ForbiddenException("USER_INACTIVE");
        var metadata = await transport.SendAsync<School>(options.SchoolBaseUrl, $"api/v1/schools/{school:D}", null, ct, token);
        if (metadata.SchoolId != school) throw NotificationErrors.Dependency();
        if (metadata.Status != "Active") throw new ForbiddenException("SCHOOL_INACTIVE");

        using var response = await transport.RequestAsync(options.SchoolBaseUrl, $"api/v1/schools/{school:D}/memberships", null, ct, token);
        var isAdmin = false;
        if (response.IsSuccessStatusCode)
        {
            Membership[] memberships;
            try { memberships = await response.Content.ReadFromJsonAsync<Membership[]>(NotificationService.Json, ct) ?? throw NotificationErrors.Dependency(); }
            catch (JsonException) { throw NotificationErrors.Dependency(); }
            var now = clock.GetUtcNow();
            isAdmin = memberships.Any(x => x.UserId == profile.UserId && x.Role == "Administrator" && x.StartedAt <= now && (x.EndedAt is null || x.EndedAt > now));
        }
        else if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound)) throw NotificationErrors.Dependency();
        if (administrator && !isAdmin) throw new ForbiddenException("ACCESS_DENIED");
        var until = session.ValidUntil < started.AddSeconds(30) ? session.ValidUntil : started.AddSeconds(30);
        if (until <= clock.GetUtcNow()) throw new ServiceUnavailableException("AUTHORITY_EXPIRED");
        return new(school, profile.UserId, isAdmin, until, $"session:{sid:D};school:{school:D};membership:{(isAdmin ? "administrator" : "recipient")}");
    }

    private sealed record Session(bool Active, Guid IdentityId, Guid SessionId, DateTimeOffset ValidUntil);
    private sealed record Profile(Guid UserId, string Status);
    private sealed record School(Guid SchoolId, string Status);
    private sealed record Membership(Guid UserId, string Role, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
}
