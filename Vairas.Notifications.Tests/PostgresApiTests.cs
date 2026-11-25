using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Vairas.Notifications.Business;
using Vairas.Notifications.Domain;
using Vairas.Notifications.Persistance;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Integration"), NonParallelizable]
public sealed class PostgresApiTests
{
    private PostgresFixture fixture = null!;
    private Guid school;
    private string Templates => $"/api/v1/schools/{school:D}/notification-templates";
    private static CreateTemplateRequest Create => new(NotificationType.LessonScheduled, "Lesson at {{startsAt}}", "Open {{appLink}} for {{schoolName}}");

    [OneTimeSetUp] public async Task Start() { fixture = new(); await fixture.StartAsync(); }
    [OneTimeTearDown] public async Task Stop() { if (fixture is not null) await fixture.DisposeAsync(); }
    [SetUp] public void Reset()
    {
        school = Guid.NewGuid(); fixture.Authority.Reset();
        fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token());
        fixture.Client.DefaultRequestHeaders.Remove("X-Correlation-ID");
    }

    [Test]
    public async Task Template_lifecycle_persists_and_original_create_replays_after_updates_and_archive()
    {
        var operation = Guid.NewGuid();
        using var created = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var original = await Read<CommandResponse<TemplateResponse>>(created);
        Assert.That(created.Headers.Location!.ToString(), Does.EndWith($"/notification-templates/{original.Resource.TemplateId}"));
        Assert.That(original.Resource.Type, Is.EqualTo(NotificationType.LessonScheduled));
        using var updated = await fixture.SendAsync(HttpMethod.Put, $"{Templates}/{original.Resource.TemplateId}", new UpdateTemplateRequest("Changed", "Changed body", 1), Guid.NewGuid());
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Read<CommandResponse<TemplateResponse>>(updated)).Resource.Revision, Is.EqualTo(2));
        using var archived = await fixture.SendAsync(HttpMethod.Post, $"{Templates}/{operation}/archive", new RevisionRequest(2), Guid.NewGuid());
        Assert.That(archived.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var replay = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
        var replayed = await Read<CommandResponse<TemplateResponse>>(replay);
        Assert.That(replayed.Resource, Is.EqualTo(original.Resource));
        Assert.That(replayed.Replayed, Is.True);
        using var current = await fixture.Client.GetAsync($"{Templates}/{operation}");
        Assert.That((await Read<TemplateResponse>(current)).Status, Is.EqualTo(TemplateStatus.Archived));
        using var listed = await fixture.Client.GetAsync(Templates);
        Assert.That((await Read<TemplateResponse[]>(listed)).Length, Is.EqualTo(1));
        // Archival permits a new active template of this type.
        using var replacement = await fixture.SendAsync(HttpMethod.Post, Templates, Create, Guid.NewGuid());
        Assert.That(replacement.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public async Task Duplicate_active_template_and_stale_revision_roll_back_receipts()
    {
        var operation = Guid.NewGuid();
        using var first = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var duplicateOperation = Guid.NewGuid();
        using var duplicate = await fixture.SendAsync(HttpMethod.Post, Templates, Create, duplicateOperation);
        await Problem(duplicate, 409, "RESOURCE_CONFLICT");
        using var stale = await fixture.SendAsync(HttpMethod.Put, $"{Templates}/{operation}", new UpdateTemplateRequest("changed", "body", 9), Guid.NewGuid());
        await Problem(stale, 409, "REVISION_CONFLICT");
        await using var source = NpgsqlDataSource.Create(fixture.RuntimeConnection);
        await using var session = await new NotificationStore(source).OpenAsync(school, false, default);
        Assert.That(await session.ReceiptAsync(duplicateOperation, default), Is.Null);
        Assert.That((await session.TemplateAsync(operation, default))!.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task Operation_reuse_with_changed_payload_command_or_actor_is_rejected()
    {
        var operation = Guid.NewGuid();
        using var created = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        using var changed = await fixture.SendAsync(HttpMethod.Post, Templates, Create with { SubjectTemplate = "Changed" }, operation);
        await Problem(changed, 409, "IDEMPOTENCY_CONFLICT");
        using var command = await fixture.SendAsync(HttpMethod.Post, $"{Templates}/{operation}/archive", new RevisionRequest(1), operation);
        await Problem(command, 409, "IDEMPOTENCY_CONFLICT");
        var previousUser = fixture.Authority.User;
        try
        {
            fixture.Authority.User = Guid.NewGuid();
            using var actor = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
            await Problem(actor, 409, "IDEMPOTENCY_CONFLICT");
        }
        finally { fixture.Authority.User = previousUser; }
    }

    [Test]
    public async Task Concurrent_same_operation_creates_one_template_and_one_replay()
    {
        var operation = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => fixture.SendAsync(HttpMethod.Post, Templates, Create, operation)));
        try { Assert.That(responses.Select(x => (int)x.StatusCode).Order(), Is.EqualTo(new[] { 200, 201 })); }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Test]
    public async Task Cancellation_is_atomic_replayable_and_hides_private_content()
    {
        var id = Guid.NewGuid();
        await fixture.SeedNotificationAsync(school, id, fixture.Authority.User);
        var route = $"/api/v1/schools/{school}/notifications/{id}";
        using var status = await fixture.Client.GetAsync(route);
        Assert.That((await Read<NotificationStatusResponse>(status)).Status, Is.EqualTo(NotificationStatus.Pending));
        Assert.That(await status.Content.ReadAsStringAsync(), Does.Not.Contain("example.test").And.Not.Contain("Immutable"));
        var operation = Guid.NewGuid();
        using var cancelled = await fixture.SendAsync(HttpMethod.Post, route + "/cancel", new RevisionRequest(1), operation);
        Assert.That((await Read<CommandResponse<NotificationStatusResponse>>(cancelled)).Resource.Status, Is.EqualTo(NotificationStatus.Cancelled));
        using var replay = await fixture.SendAsync(HttpMethod.Post, route + "/cancel", new RevisionRequest(1), operation);
        Assert.That((await Read<CommandResponse<NotificationStatusResponse>>(replay)).Replayed, Is.True);
        using var late = await fixture.SendAsync(HttpMethod.Post, route + "/cancel", new RevisionRequest(2), Guid.NewGuid());
        await Problem(late, 409, "CANCELLATION_TOO_LATE");
    }

    [TestCase("Sending")]
    [TestCase("DeliveryUnknown")]
    public async Task In_flight_and_unknown_cannot_be_cancelled(string state)
    {
        var id = Guid.NewGuid(); await fixture.SeedNotificationAsync(school, id, fixture.Authority.User, state);
        using var response = await fixture.SendAsync(HttpMethod.Post, $"/api/v1/schools/{school}/notifications/{id}/cancel", new RevisionRequest(1), Guid.NewGuid());
        await Problem(response, 409, "CANCELLATION_TOO_LATE");
    }

    [Test]
    public async Task Recipient_reads_own_status_but_foreign_recipient_and_school_are_concealed()
    {
        var own = Guid.NewGuid(); var other = Guid.NewGuid(); var foreign = Guid.NewGuid();
        await fixture.SeedNotificationAsync(school, own, fixture.Authority.User);
        await fixture.SeedNotificationAsync(school, other, Guid.NewGuid());
        await fixture.SeedNotificationAsync(Guid.NewGuid(), foreign, fixture.Authority.User);
        fixture.Authority.Administrator = false;
        using var allowed = await fixture.Client.GetAsync($"/api/v1/schools/{school}/notifications/{own}");
        Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        foreach (var id in new[] { other, foreign, Guid.NewGuid() })
        {
            using var denied = await fixture.Client.GetAsync($"/api/v1/schools/{school}/notifications/{id}");
            await Problem(denied, 404, "NOT_FOUND");
        }
        using var templates = await fixture.Client.GetAsync(Templates);
        await Problem(templates, 403, "ACCESS_DENIED");
    }

    [Test]
    public async Task Retry_reauthorizes_and_global_or_ended_memberships_do_not_grant_admin()
    {
        var operation = Guid.NewGuid();
        using var created = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        fixture.Authority.MembershipUser = Guid.NewGuid();
        using var foreignAdmin = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
        await Problem(foreignAdmin, 403, "ACCESS_DENIED");
        fixture.Authority.MembershipUser = null;
        fixture.Authority.MembershipEnd = DateTimeOffset.UtcNow.AddSeconds(-1);
        using var ended = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
        await Problem(ended, 403, "ACCESS_DENIED");
    }

    [TestCase("session", 401, "SESSION_INVALID")]
    [TestCase("profile", 403, "USER_INACTIVE")]
    [TestCase("school", 403, "SCHOOL_INACTIVE")]
    [TestCase("dependency", 503, "DEPENDENCY_UNAVAILABLE")]
    [TestCase("binding", 503, "DEPENDENCY_UNAVAILABLE")]
    public async Task Current_authorities_fail_closed(string failure, int status, string code)
    {
        fixture.Authority.ActiveSession = failure != "session";
        fixture.Authority.ActiveProfile = failure != "profile";
        fixture.Authority.ActiveSchool = failure != "school";
        fixture.Authority.Unavailable = failure == "dependency";
        fixture.Authority.WrongSessionBinding = failure == "binding";
        using var response = await fixture.SendAsync(HttpMethod.Post, Templates, Create, Guid.NewGuid());
        await Problem(response, status, code);
    }

    [Test]
    public async Task Jwt_authentication_rejects_missing_wrong_audience_and_missing_session_tokens()
    {
        fixture.Client.DefaultRequestHeaders.Authorization = null;
        using var missing = await fixture.Client.GetAsync(Templates);
        await Problem(missing, 401, "AUTHENTICATION_REQUIRED");
        fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token("vairas-notifications-internal"));
        using var workload = await fixture.Client.GetAsync(Templates);
        await Problem(workload, 401, "AUTHENTICATION_REQUIRED");
        fixture.Client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token(session: false));
        using var session = await fixture.Client.GetAsync(Templates);
        await Problem(session, 401, "INVALID_IDENTITY");
    }

    [Test]
    public async Task Invalid_json_unknown_members_enum_numbers_missing_key_and_empty_values_are_400()
    {
        using var noKey = await fixture.SendAsync(HttpMethod.Post, Templates, Create);
        await Problem(noKey, 400, "VALIDATION_FAILED");
        foreach (var json in new[]
        {
            "{\"type\":1,\"subjectTemplate\":\"a\",\"bodyTemplate\":\"b\"}",
            "{\"type\":\"LessonScheduled\",\"subjectTemplate\":\"a\",\"bodyTemplate\":\"b\",\"channel\":\"Email\"}",
            "{\"type\":\"LessonScheduled\",\"subjectTemplate\":\"a\",\"subjectTemplate\":\"b\",\"bodyTemplate\":\"b\"}",
            "{\"type\":\"LessonScheduled\"}", "null"
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Templates) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using var response = await fixture.Client.SendAsync(request);
            Assert.That((int)response.StatusCode, Is.EqualTo(400), json);
        }
        using var invalidTemplate = await fixture.SendAsync(HttpMethod.Post, Templates, Create with { SubjectTemplate = "{{unknown}}" }, Guid.NewGuid());
        await Problem(invalidTemplate, 400, "UNKNOWN_PLACEHOLDER");
    }

    [Test]
    public async Task Runtime_RLS_permissions_and_immutable_receipts_are_enforced_by_PostgreSQL()
    {
        var operation = Guid.NewGuid();
        using var created = await fixture.SendAsync(HttpMethod.Post, Templates, Create, operation);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnection); await connection.OpenAsync();
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM notifications.notification_template", connection))
            Assert.That(await count.ExecuteScalarAsync(), Is.EqualTo(0L));
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.school_id',@school,false)", connection))
        { scope.Parameters.AddWithValue("school", school.ToString()); await scope.ExecuteNonQueryAsync(); }
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM notifications.notification_template", connection))
            Assert.That(await count.ExecuteScalarAsync(), Is.EqualTo(1L));
        await using (var update = new NpgsqlCommand("UPDATE notifications.notification_operation SET outcome_code='tampered'", connection))
            Assert.That(Assert.ThrowsAsync<PostgresException>(async () => await update.ExecuteNonQueryAsync())!.SqlState, Is.EqualTo("42501"));
        var immutable = Assert.ThrowsAsync<PostgresException>(async () => await PostgresFixture.Sql(fixture.OwnerConnection, $"UPDATE notifications.notification_operation SET outcome_code='tampered' WHERE operation_id='{operation}'"));
        Assert.That(immutable!.SqlState, Is.EqualTo("P0001"));
    }

    [Test]
    public async Task Stored_snapshots_cannot_be_rewritten_and_template_changes_do_not_change_history()
    {
        var template = Guid.NewGuid(); var notification = Guid.NewGuid();
        using var created = await fixture.SendAsync(HttpMethod.Post, Templates, Create, template);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        await fixture.SeedNotificationAsync(school, notification, fixture.Authority.User, template: template);
        using var changed = await fixture.SendAsync(HttpMethod.Put, $"{Templates}/{template}", new UpdateTemplateRequest("New", "New body", 1), Guid.NewGuid());
        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var error = Assert.ThrowsAsync<PostgresException>(async () => await PostgresFixture.Sql(fixture.OwnerConnection,
            $"UPDATE notifications.notification SET subject='tampered',revision=revision+1 WHERE notification_id='{notification}'"));
        Assert.That(error!.SqlState, Is.EqualTo("P0001"));
    }

    [Test]
    public async Task Readiness_checks_real_runtime_permissions_and_OpenAPI_documents_real_surface()
    {
        using var ready = await fixture.Client.GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var live = await fixture.Client.GetAsync("/health/live");
        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        fixture.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "notification-test-01");
        using var document = await fixture.Client.GetAsync("/openapi/v1.json");
        Assert.That(document.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(document.Headers.GetValues("X-Correlation-ID").Single(), Is.EqualTo("notification-test-01"));
        var json = await document.Content.ReadAsStringAsync();
        Directory.CreateDirectory(Path.Combine(PostgresFixture.Root, "api"));
        await File.WriteAllTextAsync(Path.Combine(PostgresFixture.Root, "api/notifications.openapi.json"), json);
        using var parsed = JsonDocument.Parse(json);
        Assert.That(parsed.RootElement.GetProperty("paths").EnumerateObject().Count(), Is.EqualTo(5));
        var schemas = parsed.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var (schemaName, propertyName) in new[] { ("CreateTemplateRequest", "type"), ("NotificationStatusResponse", "status"), ("TemplateResponse", "status") })
        {
            var property = schemas.GetProperty(schemaName).GetProperty("properties").GetProperty(propertyName);
            if (property.TryGetProperty("$ref", out var reference)) property = schemas.GetProperty(reference.GetString()!.Split('/').Last());
            var values = property.GetProperty("enum");
            Assert.That(values.GetArrayLength(), Is.GreaterThan(1), $"{schemaName}.{propertyName}");
            Assert.That(values.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String), Is.True);
        }
        Assert.That(json, Does.Contain("bearerAuth").And.Contain("DeliveryUnknown").And.Contain("Idempotency-Key"));
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(NotificationService.Json))!;
    }
    private static async Task Problem(HttpResponseMessage response, int status, string code)
    {
        Assert.That((int)response.StatusCode, Is.EqualTo(status), await response.Content.ReadAsStringAsync());
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/problem+json"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("code").GetString(), Is.EqualTo(code));
        Assert.That(json.RootElement.GetProperty("correlationId").GetString(), Is.EqualTo(response.Headers.GetValues("X-Correlation-ID").Single()));
    }
}
