using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MimeKit;
using Vairas.Notifications.Business;
using Vairas.Notifications.Domain;
using Microsoft.Extensions.Options;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Integrations;

/// <summary>No HTTP retry handler may be attached to this client: messages.send is not idempotent.</summary>
public sealed class GmailEmailProvider(IHttpClientFactory clients, IOptions<GmailOptions> optionsSettings, TimeProvider clock) : IEmailProvider, IDisposable
{
    private readonly GmailOptions options = optionsSettings.Value;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expires;
    private string Base => options.ApiBaseUrl;

    public async Task<ProviderResult> SubmitAsync(NotificationSnapshot snapshot, DeliveryAttempt attempt, CancellationToken ct)
    {
        string token;
        try
        {
            token = await TokenAsync(ct);
            using var profile = await Send(HttpMethod.Get, Base + "profile", token, null, ct);
            if (!profile.IsSuccessStatusCode)
            { if (profile.StatusCode == HttpStatusCode.Unauthorized) Interlocked.Exchange(ref accessToken, null); return new(SubmissionOutcome.RetryableRejection, ErrorCode: "GMAIL_PROFILE_UNAVAILABLE"); }
            var mailbox = await profile.Content.ReadFromJsonAsync<Profile>(ct);
            if (mailbox is null || !string.Equals(mailbox.EmailAddress, options.Sender, StringComparison.OrdinalIgnoreCase))
                return new(SubmissionOutcome.PermanentRejection, ErrorCode: "GMAIL_SENDER_MISMATCH");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or BusinessException or OperationCanceledException)
        { return new(SubmissionOutcome.RetryableRejection, ErrorCode: "GMAIL_NOT_SUBMITTED"); }
        string raw;
        try { raw = BuildMime(snapshot, attempt, options.Sender); }
        catch (BusinessException) { return new(SubmissionOutcome.PermanentRejection, ErrorCode: "INVALID_EMAIL_CONTENT"); }
        try
        {
            using var response = await Send(HttpMethod.Post, Base + "messages/send", token, new { raw }, ct);
            if (response.IsSuccessStatusCode)
            {
                var message = await response.Content.ReadFromJsonAsync<Message>(ct);
                return !string.IsNullOrWhiteSpace(message?.Id) && message.Id.Length <= 200 ? new(SubmissionOutcome.Accepted, message.Id)
                    : new(SubmissionOutcome.Unknown, ErrorCode: "GMAIL_RESPONSE_INCOMPLETE");
            }
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                return new(SubmissionOutcome.PermanentRejection, ErrorCode: "GMAIL_REJECTED");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            { if (response.StatusCode == HttpStatusCode.Unauthorized) Interlocked.Exchange(ref accessToken, null); return new(SubmissionOutcome.RetryableRejection, ErrorCode: "GMAIL_REJECTED"); }
            return new(SubmissionOutcome.Unknown, ErrorCode: "GMAIL_OUTCOME_UNKNOWN");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
        { return new(SubmissionOutcome.Unknown, ErrorCode: "GMAIL_ACKNOWLEDGEMENT_LOST"); }
    }

    public async Task<ProviderResult> ReconcileAsync(NotificationSnapshot snapshot, DeliveryAttempt attempt, CancellationToken ct)
    {
        try
        {
            var token = await TokenAsync(ct);
            var id = RfcMessageId(attempt.ProviderKey);
            using var search = await Send(HttpMethod.Get, Base + "messages?maxResults=10&q=" + Uri.EscapeDataString($"in:sent rfc822msgid:{id}"), token, null, ct);
            if (!search.IsSuccessStatusCode) return new(SubmissionOutcome.Unknown, ErrorCode: "GMAIL_RECONCILIATION_UNAVAILABLE");
            var matches = await search.Content.ReadFromJsonAsync<Matches>(ct);
            foreach (var match in matches?.Messages ?? [])
            {
                if (string.IsNullOrWhiteSpace(match.Id)) continue;
                using var result = await Send(HttpMethod.Get, Base + "messages/" + Uri.EscapeDataString(match.Id) + "?format=metadata&metadataHeaders=Message-ID&metadataHeaders=To&metadataHeaders=From&metadataHeaders=X-Vairas-Content-Hash", token, null, ct);
                if (!result.IsSuccessStatusCode) continue;
                var message = await result.Content.ReadFromJsonAsync<Metadata>(ct);
                string? Header(string name) => message?.Payload?.Headers?.SingleOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
                if (message?.LabelIds?.Contains("SENT", StringComparer.Ordinal) == true && Header("Message-ID")?.Trim('<', '>') == id
                    && MailboxAddress.TryParse(Header("From"), out var from) && from.Address.Equals(options.Sender, StringComparison.OrdinalIgnoreCase)
                    && MailboxAddress.TryParse(Header("To"), out var to) && to.Address.Equals(snapshot.DestinationAddress, StringComparison.OrdinalIgnoreCase)
                    && Header("X-Vairas-Content-Hash") == ContentHash(snapshot)) return new(SubmissionOutcome.Accepted, match.Id);
            }
            // Search indexing and missing responses do not prove non-acceptance. Never retry on an empty search.
            return new(SubmissionOutcome.Unknown, ErrorCode: "GMAIL_RECONCILIATION_INCONCLUSIVE");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or BusinessException or OperationCanceledException or InvalidOperationException)
        { return new(SubmissionOutcome.Unknown, ErrorCode: "GMAIL_RECONCILIATION_UNAVAILABLE"); }
    }

    public static string RfcMessageId(string providerKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(providerKey))) + "@notifications.vairas.invalid";
    public static string ContentHash(NotificationSnapshot snapshot) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { snapshot.SchoolId, snapshot.NotificationId, snapshot.DestinationAddress, snapshot.Subject, snapshot.Body }, NotificationService.Json)));
    public static string BuildMime(NotificationSnapshot snapshot, DeliveryAttempt attempt, string sender)
    {
        var subject = WebUtility.HtmlDecode(snapshot.Subject);
        if (subject.Any(char.IsControl) || !MailboxAddress.TryParse(sender, out var from) || !MailboxAddress.TryParse(snapshot.DestinationAddress, out var recipient))
            throw new BadRequestException("INVALID_EMAIL_CONTENT");
        var message = new MimeMessage { Subject = subject, MessageId = RfcMessageId(attempt.ProviderKey), Date = attempt.StartedAt,
            Body = new TextPart("plain") { Text = WebUtility.HtmlDecode(snapshot.Body) } };
        message.From.Add(from); message.To.Add(recipient); message.Headers.Add("X-Vairas-Content-Hash", ContentHash(snapshot));
        using var stream = new MemoryStream(); message.WriteTo(stream);
        return Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (!options.Enabled) throw new ServiceUnavailableException("GMAIL_DISABLED");
        await tokenGate.WaitAsync(ct);
        try
        {
            if (accessToken is not null && expires > clock.GetUtcNow().AddSeconds(30)) return accessToken;
            using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenUrl)
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret, ["refresh_token"] = options.RefreshToken }) };
            using var response = await clients.CreateClient("gmail").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new ServiceUnavailableException("GMAIL_AUTHORIZATION_REQUIRED");
            var token = await response.Content.ReadFromJsonAsync<Token>(ct);
            if (token is null || token.ExpiresIn <= 30 || string.IsNullOrWhiteSpace(token.AccessToken) || !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new ServiceUnavailableException("GMAIL_AUTHORIZATION_REQUIRED");
            accessToken = token.AccessToken; expires = clock.GetUtcNow().AddSeconds(Math.Min(token.ExpiresIn, 3600)); return accessToken;
        }
        finally { tokenGate.Release(); }
    }
    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string token, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await clients.CreateClient("gmail").SendAsync(request, ct);
    }
    public void Dispose() => tokenGate.Dispose();
    private sealed record Token([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("token_type")] string TokenType, [property: JsonPropertyName("expires_in")] int ExpiresIn);
    private sealed record Profile(string EmailAddress);
    private sealed record Message(string Id);
    private sealed record Matches(Message[]? Messages);
    private sealed record Metadata(string[]? LabelIds, Payload? Payload);
    private sealed record Payload(Header[]? Headers);
    private sealed record Header(string Name, string Value);
}
