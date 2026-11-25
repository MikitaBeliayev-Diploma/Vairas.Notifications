using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vairas.Notifications.Business;
using Microsoft.Extensions.Options;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Integrations;

public sealed class NotificationOwners(IDependencyTransport transport, IOptions<DependencyOptions> optionsSettings, TimeProvider clock) : INotificationOwners
{
    private readonly DependencyOptions options = optionsSettings.Value;
    public async Task<SourceFact?> SourceAsync(string producer, Guid school, Guid source, CancellationToken ct)
    {
        var (origin, route) = producer switch
        {
            "scheduling" => (options.SchedulingBaseUrl, $"internal/v1/notifications/lessons/{source:D}?schoolId={school:D}"),
            "learning" => (options.LearningBaseUrl, $"internal/v1/notifications/assessments/{source:D}?schoolId={school:D}"),
            _ => throw new BadRequestException("INVALID_PRODUCER")
        };
        using var response = await transport.RequestAsync(origin, route, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && await ExplicitMissing(response, ct)) return null;
        return await Read<SourceFact>(response, ct);
    }
    public async Task<RecipientScope> RecipientsAsync(SourceFact source, CancellationToken ct)
    {
        using var response = await transport.RequestAsync(options.SchoolBaseUrl, "internal/v1/notifications/recipients",
            new { source.SchoolId, source.EnrollmentIds, source.TeacherId, source.Kind }, ct);
        return await Read<RecipientScope>(response, ct);
    }
    public async Task<ContactEvidence> ContactAsync(Guid user, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        using var bindingResponse = await transport.RequestAsync(options.UserManagementBaseUrl, $"internal/v1/users/{user:D}/notification-binding", null, ct);
        if (bindingResponse.StatusCode == HttpStatusCode.NotFound && await ExplicitMissing(bindingResponse, ct)) return new(user, false, null, "removed", started.AddSeconds(30));
        var binding = await Read<Binding>(bindingResponse, ct);
        if (binding.UserId != user || binding.IdentityId == Guid.Empty || binding.Revision < 1) throw NotificationErrors.Dependency();
        if (!binding.Active) return new(user, false, null, "inactive", started.AddSeconds(30));
        using var emailResponse = await transport.RequestAsync(options.AuthBaseUrl, $"internal/v1/identities/{binding.IdentityId:D}/verified-email", null, ct);
        var email = await Read<VerifiedEmail>(emailResponse, ct);
        if (email.IdentityId != binding.IdentityId || email.ValidUntil <= clock.GetUtcNow() || email.ValidUntil > clock.GetUtcNow().AddSeconds(31)
            || email.Revision.Length != 64) throw NotificationErrors.Dependency();
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{binding.IdentityId:D}|{email.Revision}")));
        return new(user, email.Eligible, email.Email, revision, email.ValidUntil < started.AddSeconds(30) ? email.ValidUntil : started.AddSeconds(30));
    }
    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw NotificationErrors.Dependency();
        try { return await response.Content.ReadFromJsonAsync<T>(NotificationService.Json, ct) ?? throw NotificationErrors.Dependency(); }
        catch (JsonException) { throw NotificationErrors.Dependency(); }
    }
    private sealed record Binding(Guid UserId, Guid IdentityId, bool Active, long Revision);
    private sealed record VerifiedEmail(Guid IdentityId, bool Eligible, string? Email, string Revision, DateTimeOffset ValidUntil);
    private static async Task<bool> ExplicitMissing(HttpResponseMessage response, CancellationToken ct)
    {
        try { return (await response.Content.ReadFromJsonAsync<ProblemCode>(NotificationService.Json, ct))?.Code == "NOT_FOUND"; }
        catch (JsonException) { return false; }
    }
    private sealed record ProblemCode(string Code);
}
