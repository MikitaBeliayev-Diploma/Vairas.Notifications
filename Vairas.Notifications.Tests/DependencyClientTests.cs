using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Vairas.Notifications.Business;
using Vairas.Notifications.Integrations;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Unit")]
public sealed class DependencyClientTests
{
    [Test]
    public async Task OAuth_uses_client_credentials_and_caches_valid_tokens()
    {
        var calls = 0;
        var handler = new Handler(async request =>
        {
            calls++;
            Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo("/connect/token"));
            Assert.That(await request.Content!.ReadAsStringAsync(), Does.Contain("grant_type=client_credentials").And.Contain("scope=identity.validate"));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "synthetic-token", token_type = "Bearer", expires_in = 60 }) };
        });
        using var client = new HttpClient(handler);
        using var tokens = new WorkloadTokens(new Factory(client), Microsoft.Extensions.Options.Options.Create(new DependencyOptions { ClientSecret = "synthetic-secret" }), TimeProvider.System);
        Assert.That(await tokens.GetAsync(default), Is.EqualTo("synthetic-token"));
        Assert.That(await tokens.GetAsync(default), Is.EqualTo("synthetic-token"));
        Assert.That(calls, Is.EqualTo(1));
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public void OAuth_denials_are_dependencies_not_authority(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
        using var tokens = new WorkloadTokens(new Factory(client), Microsoft.Extensions.Options.Options.Create(new DependencyOptions()), TimeProvider.System);
        Assert.That(Business.ThrowsBusinessAsync(async () => await tokens.GetAsync(default))!.HttpStatus(), Is.EqualTo(503));
    }

    [Test]
    public async Task Transport_uses_distinct_workload_and_user_bearers_without_shared_headers()
    {
        var seen = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            seen.Add(request.Headers.Authorization!.Parameter!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Response(true)) });
        }));
        var transport = new DependencyTransport(new Factory(client), new Tokens());
        await transport.SendAsync<Response>("https://dependency.test/", "internal/v1/check", new { userId = Guid.NewGuid() }, default);
        await transport.SendAsync<Response>("https://dependency.test/", "api/v1/users/me", null, default, "synthetic-user");
        Assert.That(seen, Is.EqualTo(new[] { "synthetic-workload", "synthetic-user" }));
        Assert.That(client.DefaultRequestHeaders.Authorization, Is.Null);
    }

    [TestCase(HttpStatusCode.Forbidden, 403)]
    [TestCase(HttpStatusCode.NotFound, 403)]
    [TestCase(HttpStatusCode.Unauthorized, 401)]
    [TestCase(HttpStatusCode.ServiceUnavailable, 503)]
    public void Dependency_status_mapping_is_explicit(HttpStatusCode status, int expected)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
        var transport = new DependencyTransport(new Factory(client), new Tokens());
        Assert.That(Business.ThrowsBusinessAsync(async () => await transport.SendAsync<Response>("https://dependency.test/", "status", null, default, "synthetic-user"))!.HttpStatus(), Is.EqualTo(expected));
    }

    [Test]
    public void Incomplete_or_invalid_authority_payload_and_network_failure_fail_closed()
    {
        foreach (var body in new[] { "{}", "not-json", "null" })
        {
            using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
            var transport = new DependencyTransport(new Factory(client), new Tokens());
            Assert.That(Business.ThrowsBusinessAsync(async () => await transport.SendAsync<Response>("https://dependency.test/", "status", null, default))!.HttpStatus(), Is.EqualTo(503));
        }
        using var failedClient = new HttpClient(new Handler(_ => throw new HttpRequestException("synthetic network failure")));
        var failed = new DependencyTransport(new Factory(failedClient), new Tokens());
        Assert.That(Business.ThrowsBusinessAsync(async () => await failed.SendAsync<Response>("https://dependency.test/", "status", null, default))!.HttpStatus(), Is.EqualTo(503));
    }

    [Test]
    public async Task Correlation_handler_reads_current_request_even_when_reused()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var seen = new List<string>();
        var inner = new Handler(request =>
        {
            seen.Add(request.Headers.GetValues("X-Correlation-ID").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(new DependencyCorrelationHandler(accessor) { InnerHandler = inner });
        foreach (var id in new[] { "first", "second" })
        {
            accessor.HttpContext.Items["CorrelationId"] = id;
            using var response = await client.GetAsync("https://dependency.test/");
        }
        Assert.That(seen, Is.EqualTo(new[] { "first", "second" }));
    }

    private sealed record Response(bool Active);
    private sealed class Tokens : IWorkloadTokens { public Task<string> GetAsync(CancellationToken ct) => Task.FromResult("synthetic-workload"); }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request); }
}
