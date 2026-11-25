using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using System.Text.Json;
using MimeKit;
using NUnit.Framework;
using Vairas.Notifications.Business;
using Vairas.Notifications.Domain;
using Vairas.Notifications.Integrations;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Unit")]
public sealed class GmailProviderTests
{
    private static NotificationSnapshot Snapshot => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, NotificationType.LessonScheduled,
        "Lesson &amp; details", "Open https://vairas.test/lesson", "recipient@example.test", "verified-contact", "Scheduling", Guid.NewGuid(), 1, Guid.NewGuid(), "intent", DateTimeOffset.UtcNow);
    private static DeliveryAttempt Attempt(NotificationSnapshot snapshot) => new(Guid.NewGuid(), snapshot.NotificationId, 1,
        NotificationDelivery.ProviderKey(snapshot.SchoolId, snapshot.NotificationId, 1), AttemptStatus.Started, DateTimeOffset.UtcNow);
    private static GmailOptions Options => new() { Enabled = true, Sender = "sender@gmail.com", ClientId = "synthetic-client", ClientSecret = "synthetic-secret", RefreshToken = "synthetic-refresh" };

    [Test]
    public async Task Gmail_send_uses_OAuth_and_one_encoded_MIME_submission_with_stable_identity()
    {
        var snapshot = Snapshot; var attempt = Attempt(snapshot); var requests = new List<string>();
        using var client = new HttpClient(new Handler(async request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.Host == "oauth2.googleapis.com")
            { Assert.That(await request.Content!.ReadAsStringAsync(), Does.Contain("grant_type=refresh_token")); return Json(new { access_token = "synthetic-access", token_type = "Bearer", expires_in = 3600 }); }
            Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo("synthetic-access"));
            if (request.RequestUri.AbsolutePath.EndsWith("profile", StringComparison.Ordinal)) return Json(new { emailAddress = "sender@gmail.com" });
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var raw = json.RootElement.GetProperty("raw").GetString()!; raw = raw.Replace('-', '+').Replace('_', '/'); raw = raw.PadRight((raw.Length + 3) / 4 * 4, '=');
            using var mime = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(raw)));
            Assert.That(mime.MessageId, Is.EqualTo(GmailEmailProvider.RfcMessageId(attempt.ProviderKey)));
            Assert.That(mime.To.Mailboxes.Single().Address, Is.EqualTo(snapshot.DestinationAddress)); Assert.That(mime.Subject, Is.EqualTo("Lesson & details"));
            Assert.That(mime.Headers["X-Vairas-Content-Hash"], Is.EqualTo(GmailEmailProvider.ContentHash(snapshot)));
            return Json(new { id = "gmail-accepted-id" });
        }));
        using var provider = new GmailEmailProvider(new Factory(client), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        var result = await provider.SubmitAsync(snapshot, attempt, default);
        Assert.That(result, Is.EqualTo(new ProviderResult(SubmissionOutcome.Accepted, "gmail-accepted-id")));
        Assert.That(requests.Count(x => x.EndsWith("messages/send", StringComparison.Ordinal)), Is.EqualTo(1));
    }

    [TestCase(HttpStatusCode.BadRequest, SubmissionOutcome.PermanentRejection)]
    [TestCase(HttpStatusCode.Unauthorized, SubmissionOutcome.RetryableRejection)]
    [TestCase(HttpStatusCode.Forbidden, SubmissionOutcome.RetryableRejection)]
    [TestCase(HttpStatusCode.TooManyRequests, SubmissionOutcome.RetryableRejection)]
    [TestCase(HttpStatusCode.InternalServerError, SubmissionOutcome.Unknown)]
    [TestCase(HttpStatusCode.RequestTimeout, SubmissionOutcome.Unknown)]
    public async Task Gmail_submission_classifies_definitive_rejection_separately_from_uncertainty(HttpStatusCode status, SubmissionOutcome outcome)
    {
        var snapshot = Snapshot;
        using var client = new HttpClient(new Handler(request => Task.FromResult(Prelude(request) ?? new HttpResponseMessage(status))));
        using var provider = new GmailEmailProvider(new Factory(client), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        Assert.That((await provider.SubmitAsync(snapshot, Attempt(snapshot), default)).Outcome, Is.EqualTo(outcome));
    }

    [Test]
    public async Task Lost_ack_is_unknown_and_authentication_failure_before_submission_is_retryable()
    {
        var snapshot = Snapshot;
        using var client = new HttpClient(new Handler(request => Task.FromResult(Prelude(request) ?? throw new HttpRequestException("synthetic acknowledgement lost"))));
        using var provider = new GmailEmailProvider(new Factory(client), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        Assert.That((await provider.SubmitAsync(snapshot, Attempt(snapshot), default)).Outcome, Is.EqualTo(SubmissionOutcome.Unknown));
        using var unavailable = new HttpClient(new Handler(_ => throw new HttpRequestException("synthetic token unavailable")));
        using var before = new GmailEmailProvider(new Factory(unavailable), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        Assert.That((await before.SubmitAsync(snapshot, Attempt(snapshot), default)).Outcome, Is.EqualTo(SubmissionOutcome.RetryableRejection));
    }

    [Test]
    public async Task Empty_sent_search_is_not_permission_to_retry_and_positive_metadata_proves_acceptance()
    {
        var snapshot = Snapshot; var attempt = Attempt(snapshot); var accepted = false; var sendCalls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("messages/send", StringComparison.Ordinal)) sendCalls++;
            var prelude = Prelude(request); if (prelude is not null) return Task.FromResult(prelude);
            if (request.RequestUri.AbsolutePath.EndsWith("messages", StringComparison.Ordinal))
            {
                Assert.That(Uri.UnescapeDataString(request.RequestUri.Query), Does.Contain("rfc822msgid:" + GmailEmailProvider.RfcMessageId(attempt.ProviderKey)));
                return Task.FromResult(accepted ? Json(new { messages = new[] { new { id = "accepted-message" } } }) : Json(new { }));
            }
            return Task.FromResult(Json(new { labelIds = new[] { "SENT" }, payload = new { headers = new[]
            { new { name = "Message-ID", value = "<" + GmailEmailProvider.RfcMessageId(attempt.ProviderKey) + ">" }, new { name = "To", value = snapshot.DestinationAddress },
                new { name = "From", value = Options.Sender }, new { name = "X-Vairas-Content-Hash", value = GmailEmailProvider.ContentHash(snapshot) } } } }));
        }));
        using var provider = new GmailEmailProvider(new Factory(client), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        Assert.That((await provider.ReconcileAsync(snapshot, attempt, default)).Outcome, Is.EqualTo(SubmissionOutcome.Unknown));
        accepted = true;
        Assert.That((await provider.ReconcileAsync(snapshot, attempt, default)).Outcome, Is.EqualTo(SubmissionOutcome.Accepted));
        Assert.That(sendCalls, Is.Zero);
    }

    [Test]
    public async Task Wrong_sender_and_invalid_MIME_are_never_submitted()
    {
        var snapshot = Snapshot; var sends = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("messages/send", StringComparison.Ordinal)) sends++;
            return Task.FromResult(Prelude(request) ?? Json(new { id = "unexpected" }));
        }));
        using var wrong = new GmailEmailProvider(new Factory(client), Microsoft.Extensions.Options.Options.Create(withSender("other@gmail.com")), TimeProvider.System);
        Assert.That((await wrong.SubmitAsync(snapshot, Attempt(snapshot), default)).ErrorCode, Is.EqualTo("GMAIL_SENDER_MISMATCH"));
        using var invalid = new GmailEmailProvider(new Factory(client), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        Assert.That((await invalid.SubmitAsync(snapshot with { Subject = "&#13;Injected" }, Attempt(snapshot), default)).Outcome, Is.EqualTo(SubmissionOutcome.PermanentRejection));
        Assert.That(sends, Is.Zero);
    }
    [Test]
    public async Task Unauthorized_submission_invalidates_the_cached_access_token_before_the_next_attempt()
    {
        var snapshot = Snapshot; var tokens = 0; var sends = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com") { tokens++; return Task.FromResult(Json(new { access_token = "access-" + tokens, token_type = "Bearer", expires_in = 3600 })); }
            if (request.RequestUri.AbsolutePath.EndsWith("profile", StringComparison.Ordinal)) return Task.FromResult(Json(new { emailAddress = "sender@gmail.com" }));
            sends++; return Task.FromResult(sends == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(new { id = "accepted-after-refresh" }));
        }));
        using var provider = new GmailEmailProvider(new Factory(client), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        Assert.That((await provider.SubmitAsync(snapshot, Attempt(snapshot), default)).Outcome, Is.EqualTo(SubmissionOutcome.RetryableRejection));
        var second = Attempt(snapshot) with { Number = 2, ProviderKey = NotificationDelivery.ProviderKey(snapshot.SchoolId, snapshot.NotificationId, 2) };
        Assert.That((await provider.SubmitAsync(snapshot, second, default)).Outcome, Is.EqualTo(SubmissionOutcome.Accepted));
        Assert.That(tokens, Is.EqualTo(2));
    }
    private static GmailOptions withSender(string sender) { var options = Options; options.Sender = sender; return options; }
    private static HttpResponseMessage? Prelude(HttpRequestMessage request) => request.RequestUri!.Host == "oauth2.googleapis.com"
        ? Json(new { access_token = "synthetic-access", token_type = "Bearer", expires_in = 3600 }) : request.RequestUri.AbsolutePath.EndsWith("profile", StringComparison.Ordinal)
            ? Json(new { emailAddress = "sender@gmail.com" }) : null;
    [Test]
    public async Task Development_endpoint_overrides_route_token_profile_and_send_to_the_local_double()
    {
        var snapshot = Snapshot; var hosts = new List<string>();
        var options = Options; options.ApiBaseUrl = "https://localhost:8443/gmail/v1/users/me/"; options.TokenUrl = "https://localhost:8443/token";
        using var client = new HttpClient(new Handler(request =>
        {
            hosts.Add(request.RequestUri!.GetLeftPart(UriPartial.Path));
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/token" => Json(new { access_token = "local-access", token_type = "Bearer", expires_in = 3600 }),
                "/gmail/v1/users/me/profile" => Json(new { emailAddress = "sender@gmail.com" }),
                _ => Json(new { id = "local-id" })
            });
        }));
        using var provider = new GmailEmailProvider(new Factory(client), Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
        Assert.That(await provider.SubmitAsync(snapshot, Attempt(snapshot), default), Is.EqualTo(new ProviderResult(SubmissionOutcome.Accepted, "local-id")));
        Assert.That(hosts, Is.EqualTo(new[] { "https://localhost:8443/token", "https://localhost:8443/gmail/v1/users/me/profile", "https://localhost:8443/gmail/v1/users/me/messages/send" }));
    }

    private sealed class Environment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name; public string ApplicationName { get; set; } = "Vairas.Notifications";
        public string ContentRootPath { get; set; } = ""; public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
    private static Microsoft.Extensions.Options.ValidateOptionsResult ValidateGmail(string environment, params (string Key, string Value)[] overrides)
    {
        var options = new GmailOptions();
        foreach (var (key, value) in overrides)
        {
            if (key == "Gmail:ApiBaseUrl") options.ApiBaseUrl = value;
            if (key == "Gmail:TokenUrl") options.TokenUrl = value;
        }
        return new Vairas.Notifications.Configuration.Options.GmailOptionsValidator(new Environment(environment)).Validate(null, options);
    }

    [Test]
    public void Gmail_endpoint_overrides_are_accepted_only_in_Development_and_only_as_HTTPS()
    {
        var local = new[] { ("Gmail:ApiBaseUrl", "https://localhost:8443/gmail/v1/users/me/"), ("Gmail:TokenUrl", "https://localhost:8443/token") };
        Assert.That(ValidateGmail("Production").Succeeded, Is.True);
        Assert.That(ValidateGmail("Development", local).Succeeded, Is.True);
        Assert.That(ValidateGmail("Production", local).Failed, Is.True);
        Assert.That(ValidateGmail("Staging", local).Failed, Is.True);
        Assert.That(ValidateGmail("Development", ("Gmail:TokenUrl", "http://localhost:8443/token")).Failed, Is.True);
        Assert.That(ValidateGmail("Development", ("Gmail:ApiBaseUrl", "https://localhost:8443/gmail")).Failed, Is.True);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request); }
}
