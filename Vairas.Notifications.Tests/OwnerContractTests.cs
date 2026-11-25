using System.Net;
using NUnit.Framework;
using Vairas.Notifications.Business;
using Vairas.Notifications.Integrations;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Unit")]
public sealed class OwnerContractTests
{
    [TestCase("HTTP_ERROR", false)]
    [TestCase("NOT_FOUND", true)]
    public async Task Generic_route_404_is_not_mistaken_for_a_deleted_source(string code, bool missing)
    {
        var transport = new Transport(); transport.Responses.Enqueue(new(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { code }) });
        var owners = new NotificationOwners(transport, Microsoft.Extensions.Options.Options.Create(new DependencyOptions()), TimeProvider.System);
        if (missing) Assert.That(await owners.SourceAsync("scheduling", Guid.NewGuid(), Guid.NewGuid(), default), Is.Null);
        else Assert.That(Business.ThrowsBusinessAsync(async () => await owners.SourceAsync("scheduling", Guid.NewGuid(), Guid.NewGuid(), default))!.HttpStatus(), Is.EqualTo(503));
    }
    [Test]
    public async Task Contact_uses_verified_identity_email_and_ignores_unrelated_profile_revision_changes()
    {
        var user = Guid.NewGuid(); var identity = Guid.NewGuid();
        async Task<ContactEvidence> Resolve(long profileRevision)
        {
            var transport = new Transport();
            transport.Responses.Enqueue(Ok(new { userId = user, identityId = identity, active = true, revision = profileRevision }));
            transport.Responses.Enqueue(Ok(new { identityId = identity, eligible = true, email = "verified@example.test", revision = new string('a', 64), validUntil = DateTimeOffset.UtcNow.AddSeconds(30) }));
            return await new NotificationOwners(transport, Microsoft.Extensions.Options.Options.Create(new DependencyOptions()), TimeProvider.System).ContactAsync(user, default);
        }
        var first = await Resolve(1); var changedName = await Resolve(2);
        Assert.That(first.Address, Is.EqualTo("verified@example.test")); Assert.That(first.Revision, Is.EqualTo(changedName.Revision));
    }
    [Test]
    public void Missing_contact_route_is_unavailable_instead_of_ineligible()
    {
        var transport = new Transport(); transport.Responses.Enqueue(new(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { code = "HTTP_ERROR" }) });
        Assert.That(Business.ThrowsBusinessAsync(async () => await new NotificationOwners(transport, Microsoft.Extensions.Options.Options.Create(new DependencyOptions()), TimeProvider.System).ContactAsync(Guid.NewGuid(), default))!.HttpStatus(), Is.EqualTo(503));
    }
    [Test]
    public async Task Inactive_user_never_looks_up_or_uses_an_email()
    {
        var user = Guid.NewGuid(); var transport = new Transport(); transport.Responses.Enqueue(Ok(new { userId = user, identityId = Guid.NewGuid(), active = false, revision = 1 }));
        var result = await new NotificationOwners(transport, Microsoft.Extensions.Options.Options.Create(new DependencyOptions()), TimeProvider.System).ContactAsync(user, default);
        Assert.That(result.Eligible, Is.False); Assert.That(result.Address, Is.Null); Assert.That(transport.Calls, Is.EqualTo(1));
    }
    [Test]
    public void Identity_binding_mismatch_is_not_accepted_as_a_verified_contact()
    {
        var user = Guid.NewGuid(); var identity = Guid.NewGuid(); var transport = new Transport();
        transport.Responses.Enqueue(Ok(new { userId = user, identityId = identity, active = true, revision = 1 }));
        transport.Responses.Enqueue(Ok(new { identityId = Guid.NewGuid(), eligible = true, email = "other@example.test", revision = new string('a', 64), validUntil = DateTimeOffset.UtcNow.AddSeconds(30) }));
        Assert.That(Business.ThrowsBusinessAsync(async () => await new NotificationOwners(transport, Microsoft.Extensions.Options.Options.Create(new DependencyOptions()), TimeProvider.System).ContactAsync(user, default))!.HttpStatus(), Is.EqualTo(503));
    }
    private static HttpResponseMessage Ok(object data) => new(HttpStatusCode.OK) { Content = JsonContent.Create(data) };
    private sealed class Transport : IDependencyTransport
    {
        public Queue<HttpResponseMessage> Responses { get; } = new(); public int Calls { get; private set; }
        public Task<HttpResponseMessage> RequestAsync(string origin, string route, object? body, CancellationToken ct, string? userToken = null)
        { Calls++; return Task.FromResult(Responses.Dequeue()); }
        public Task<T> SendAsync<T>(string origin, string route, object? body, CancellationToken ct, string? userToken = null) => throw new NotSupportedException();
    }
}
