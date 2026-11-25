extern alias SchoolTests;
extern alias NotificationsApi;
extern alias SchedulingApi;
extern alias LearningApi;
extern alias UserApi;
using System.Net;
using System.Data.Common;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using NUnit.Framework;
using Vairas.Notifications.Business;
using Vairas.Notifications.Integrations;
using Vairas.Notifications.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Vairas.Notifications.Persistance;
using Vairas.Scheduling.Persistance;
using Vairas.LearningTracking.Persistance;
using SchoolEnvironment = SchoolTests::Vairas.SchoolManagement.Tests.IntegrationEnvironment;
using SD = Vairas.Scheduling.Domain;
using LD = Vairas.LearningTracking.Domain;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Integration"), NonParallelizable]
public sealed class SixServiceTests
{
    [Test]
    public async Task Current_source_recipient_and_verified_contact_use_six_real_HTTP_pipelines_and_PostgreSQL()
    {
        await using var pg = new PostgresFixture(); await pg.StartAsync();
        await using var env = new SchoolEnvironment();
        env.ConfigureAuth = config =>
        {
            config["Authentication:WorkloadClients:notifications:SecretHash"] = config["Authentication:WorkloadClients:school-management:SecretHash"];
            var scopes = new DependencyOptions().Scope.Split(' ');
            for (var i = 0; i < scopes.Length; i++) config[$"Authentication:WorkloadClients:notifications:Scopes:{i}"] = scopes[i];
        };
        env.ConfigureUsers = services => services.Configure<UserApi::Vairas.UserManagement.Configuration.Options.JwtOptions>(o => o.WorkloadClients["notifications"] = ["profiles.contact.read"]);
        env.ConfigureSchool = config => config["Authentication:WorkloadClients:notifications:0"] = "school.notifications.read";
        await env.StartAsync(); var school = await env.SeedSchool(targetTeacher: true);
        var student = Guid.NewGuid(); var teacher = Guid.NewGuid(); var enrollment = Guid.NewGuid(); var group = Guid.NewGuid(); var branch = Guid.NewGuid();
        // Retained synthetic owner fixtures. Auth accounts/email verification were created through real handlers above.
        await SchoolEnvironment.Sql(env.UserOwner, $"INSERT INTO student_profile(student_id,user_id,created_at) VALUES('{student}','{env.Outsider.UserId}',clock_timestamp()); INSERT INTO teacher_profile(teacher_id,user_id,created_at) VALUES('{teacher}','{env.Target.UserId}',clock_timestamp())");
        using (var db = env.OwnerDb())
        {
            var op = await db.Set<Vairas.SchoolManagement.Domain.SchoolOperation>().SingleAsync(x => x.SchoolId == school);
            db.Add(new Vairas.SchoolManagement.Domain.SchoolMembership { MembershipId = Guid.NewGuid(), SchoolId = school, UserId = env.Outsider.UserId,
                Role = Vairas.SchoolManagement.Domain.SchoolRole.Student, StartedAt = DateTimeOffset.UtcNow.AddDays(-1), Revision = 1, LastOperationId = op.OperationId });
            db.Add(new Vairas.SchoolManagement.Domain.StudentEnrollment { EnrollmentId = enrollment, SchoolId = school, StudentId = student, CategoryCode = "B",
                StartedAt = DateTimeOffset.UtcNow.AddDays(-1), StatusChangedAt = DateTimeOffset.UtcNow.AddDays(-1), Status = Vairas.SchoolManagement.Domain.EnrollmentStatus.Active, Revision = 1, LastOperationId = op.OperationId });
            await db.SaveChangesAsync();
        }
        var suffix = Guid.NewGuid().ToString("N"); var sourceDatabases = new List<string>(); var roles = new List<string>();
        try
        {
            var scheduling = await SourceDatabase("sched", ["v_scheduling_runtime", "v_scheduling_reservation_authority", "v_scheduling_outbox"], "Vairas.Scheduling",
                ["database-roles.sql", "reliability-grants.sql", "review-grants.sql"], async (owner, roles) =>
                { await using var db = new Vairas.Scheduling.Persistance.SchedulingDbContext(new DbContextOptionsBuilder<Vairas.Scheduling.Persistance.SchedulingDbContext>().UseScheduling(owner).Options); await db.Database.MigrateAsync(); });
            var learning = await SourceDatabase("learn", ["v_learning_tracking_runtime", "v_learning_tracking_delivery"], "Vairas.LearningTracking",
                ["canonical-roles.sql", "reliability-grants.sql"], async (owner, roles) =>
                { await using var db = new Vairas.LearningTracking.Persistance.LearningDbContext(new DbContextOptionsBuilder<Vairas.LearningTracking.Persistance.LearningDbContext>().UseLearning(owner).AddInterceptors(new MigrationRoleMapping(roles)).Options); await db.Database.MigrateAsync(); });
            var lessonId = Guid.NewGuid(); var starts = DateTimeOffset.UtcNow.AddDays(2);
            var lesson = new SD.Lesson { Id = lessonId, SchoolId = school, Kind = SD.LessonKind.Theory, GroupId = group, GroupMembershipRevision = 1, BranchId = branch,
                TeacherId = teacher, CategoryCode = "B", StartsAt = starts, EndsAt = starts.AddHours(1), Status = SD.LessonStatus.Scheduled, Revision = 1, CreatedAt = DateTimeOffset.UtcNow };
            await using (var db = new Vairas.Scheduling.Persistance.SchedulingDbContext(new DbContextOptionsBuilder<Vairas.Scheduling.Persistance.SchedulingDbContext>().UseScheduling(scheduling.Owner).Options))
            {
                await db.Database.OpenConnectionAsync(); await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
                db.Add(lesson); db.Add(new SD.LessonParticipant { LessonId = lessonId, EnrollmentId = enrollment, StudentId = student, MembershipRevision = 1, IsActive = true }); await db.SaveChangesAsync();
            }
            var assessment = Guid.NewGuid(); var assessed = DateTimeOffset.UtcNow.AddMinutes(-1);
            await SeedLearning(learning.Owner, school, student, teacher, enrollment, assessment, assessed, env.Target.UserId);
            await using var schedulingFactory = new SourceFactory<SchedulingApi::Program>(Path.Combine(PostgresFixture.Root, "../Vairas.Scheduling/Vairas.Scheduling"), new()
            { ["ConnectionStrings:Scheduling"] = scheduling.Runtime, ["Dependencies:ClientSecret"] = env.Secret }, env);
            await using var learningFactory = new SourceFactory<LearningApi::Program>(Path.Combine(PostgresFixture.Root, "../Vairas.LearningTracking/Vairas.LearningTracking"), new()
            { ["ConnectionStrings:LearningTracking"] = learning.Runtime, ["Dependencies:ClientSecret"] = env.Secret }, env);
            env.Scheduling = schedulingFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost:7300") });
            env.Learning = learningFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost:7500") });
            await using var notificationFactory = new SourceFactory<NotificationsApi::Program>(Path.Combine(PostgresFixture.Root, "Vairas.Notifications"), new()
            {
                ["ConnectionStrings:Notifications"] = pg.RuntimeConnection, ["ConnectionStrings:Delivery"] = pg.WorkerConnection,
                ["Dependencies:ClientSecret"] = env.Secret, ["Delivery:ApplicationOrigin"] = "https://vairas.test"
            }, env, notifications: true);
            using var client = notificationFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost:7600") });
            using var scope = notificationFactory.Services.CreateScope(); var workflow = scope.ServiceProvider.GetRequiredService<INotificationWorkflow>();
            await using var workerData = NpgsqlDataSource.Create(pg.WorkerConnection); var store = new DeliveryStore(workerData);
            await using var broker = await BrokerHarness.CreateAsync(notificationFactory.Services, store);
            var scheduled = Vairas.Scheduling.Business.SchedulingEvents.Create(lesson, "LessonScheduled", DateTimeOffset.UtcNow);
            await broker.ConsumeAsync(new(scheduled.Id, scheduled.EventType, 1, "scheduling", school, lessonId, 1, scheduled.OccurredAt, "six-service-lesson", scheduled.Payload), workflow);
            var assessmentMessage = new SourceMessage(Guid.NewGuid(), "AssessmentPassed", 1, "learning", school, assessment, 1, DateTimeOffset.UtcNow, "six-service-assessment",
                JsonSerializer.Serialize(new { schoolId = school, enrollmentId = enrollment, assessmentId = assessment, assessedAt = assessed }, NotificationService.Json));
            await broker.ConsumeAsync(assessmentMessage, workflow);
            Directory.CreateDirectory(Path.Combine(PostgresFixture.Root, "evidence"));
            var lessonMessage = new SourceMessage(scheduled.Id, scheduled.EventType, 1, "scheduling", school, lessonId, 1, scheduled.OccurredAt, "six-service-lesson", scheduled.Payload);
            var samples = new[] { lessonMessage, assessmentMessage }.Select(m => new { headers = new { eventId = m.EventId, eventType = m.EventType, eventVersion = m.EventVersion, producer = m.Producer,
                aggregateId = m.SourceId, aggregateVersion = m.SourceRevision, occurredAt = m.OccurredAt, correlationId = m.CorrelationId }, payload = JsonDocument.Parse(m.Payload).RootElement.Clone() });
            await File.WriteAllTextAsync(Path.Combine(PostgresFixture.Root, "evidence/source-event-samples.json"), JsonSerializer.Serialize(new { origin = "six real HTTP pipelines and PostgreSQL, synthetic owner facts", events = samples }, NotificationService.Json));
            var ids = new List<Guid>();
            await using (var connection = new NpgsqlConnection(pg.OwnerConnection))
            {
                await connection.OpenAsync(); await using var command = new NpgsqlCommand("SELECT notification_id FROM notifications.notification WHERE school_id=@s", connection); command.Parameters.AddWithValue("s", school);
                await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
            }
            Assert.That(ids, Has.Count.EqualTo(5), "Student and teacher lesson notices/reminders, plus a student-only assessment notice.");
            await using (var tx = await store.OpenAsync(school, default))
            foreach (var id in ids)
            {
                var row = (await tx.ReadAsync(id, default))!;
                Assert.That(row.Snapshot.DestinationAddress, Does.EndWith("@example.test"), "Authentication verified this address; UM's optional profile email is null.");
                Assert.That(row.Snapshot.ContactRevision, Has.Length.EqualTo(64));
                Assert.That(row.Snapshot.RecipientUserId, Is.AnyOf(env.Outsider.UserId, env.Target.UserId));
            }
            foreach (var id in ids) await workflow.ProcessAsync(new(school, id), default);
            var capture = notificationFactory.Capture;
            Assert.That(capture.Submissions, Is.EqualTo(3), "Only immediate messages are due; reminders remain queued.");
            // Revocation in the real UM store affects the next fresh contact/relationship read.
            await SchoolEnvironment.Sql(env.UserOwner, $"UPDATE user_profile SET status='Inactive',revision=revision+1,updated_at=clock_timestamp() WHERE user_id='{env.Outsider.UserId}'");
            var contact = await scope.ServiceProvider.GetRequiredService<INotificationOwners>().ContactAsync(env.Outsider.UserId, default); Assert.That(contact.Eligible, Is.False);
            // A public human token cannot invoke any new internal projection, even with a matching public audience.
            foreach (var (http, route) in new[]
            {
                (env.Auth, $"/internal/v1/identities/{env.Target.Tokens.IdentityId}/verified-email"),
                (env.Users, $"/internal/v1/users/{env.Target.UserId}/notification-binding"),
                (env.Scheduling, $"/internal/v1/notifications/lessons/{lessonId}?schoolId={school}"),
                (env.Learning, $"/internal/v1/notifications/assessments/{assessment}?schoolId={school}")
            })
            {
                using var denied = await SchoolEnvironment.Send(http, HttpMethod.Get, route, env.Target.Tokens.AccessToken);
                Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            }
            using (var denied = await SchoolEnvironment.Send(env.Client, HttpMethod.Post, "/internal/v1/notifications/recipients", env.Target.Tokens.AccessToken,
                new { schoolId = school, enrollmentIds = new[] { enrollment }, teacherId = teacher, kind = "Lesson" })) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(await scope.ServiceProvider.GetRequiredService<INotificationOwners>().SourceAsync("scheduling", Guid.NewGuid(), lessonId, default), Is.Null);
            foreach (var (http, file) in new[] { (env.Auth, "authentication"), (env.Users, "user-management"), (env.Client, "school-management"), (env.Scheduling, "scheduling"), (env.Learning, "learning-tracking") })
            {
                using var response = await http.GetAsync("/openapi/v1.json"); Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());
                Directory.CreateDirectory(Path.Combine(PostgresFixture.Root, "api/providers")); await File.WriteAllTextAsync(Path.Combine(PostgresFixture.Root, $"api/providers/{file}.openapi.json"), await response.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            foreach (var name in sourceDatabases) await PostgresFixture.Sql(pg.AdminConnection, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            foreach (var role in roles) await PostgresFixture.Sql(pg.AdminConnection, $"DROP ROLE IF EXISTS {role}");
        }

        async Task<(string Owner, string Runtime)> SourceDatabase(string prefix, string[] canonicalRoles, string service, string[] scripts, Func<string, Dictionary<string, string>, Task> migrate)
        {
            var database = "nt_" + prefix + "_" + suffix; await PostgresFixture.Sql(pg.AdminConnection, $"CREATE DATABASE {database}"); sourceDatabases.Add(database);
            var owner = new NpgsqlConnectionStringBuilder(pg.AdminConnection) { Database = database, Pooling = false }.ConnectionString;
            var map = new Dictionary<string, string> { [prefix == "sched" ? "v_scheduling_migration" : "v_learning_tracking_migration"] = new NpgsqlConnectionStringBuilder(owner).Username! };
            foreach (var canonical in canonicalRoles)
            { var role = prefix + "_" + canonical.Split('_').Last() + "_" + suffix; map[canonical] = role; roles.Add(role); await PostgresFixture.Sql(pg.AdminConnection, $"CREATE ROLE {role} LOGIN PASSWORD '{suffix}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS"); }
            await migrate(owner, map);
            foreach (var file in scripts)
            {
                var sql = string.Join('\n', (await File.ReadAllLinesAsync(Path.Combine(PostgresFixture.Root, "..", service, "database", file))).Where(x => !x.TrimStart().StartsWith('\\')));
                foreach (var pair in map) sql = sql.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                await PostgresFixture.Sql(owner, sql);
            }
            return (owner, new NpgsqlConnectionStringBuilder(owner) { Username = map[canonicalRoles[0]], Password = suffix }.ConnectionString);
        }
    }

    // Canonical migration SQL names production roles; isolated fixtures use uniquely named roles.
    private sealed class MigrationRoleMapping(Dictionary<string, string> roles) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var pair in roles) command.CommandText = command.CommandText.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }

    private static async Task SeedLearning(string owner, Guid school, Guid student, Guid teacher, Guid enrollment, Guid assessment, DateTimeOffset at, Guid actor)
    {
        await using var db = new Vairas.LearningTracking.Persistance.LearningDbContext(new DbContextOptionsBuilder<Vairas.LearningTracking.Persistance.LearningDbContext>().UseLearning(owner).Options);
        await db.Database.OpenConnectionAsync(); await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL DEFERRED");
        var program = Guid.NewGuid(); var version = Guid.NewGuid(); var definition = Guid.NewGuid(); var definitionVersion = Guid.NewGuid();
        db.Add(new LD.TrainingProgram { Id = program, SchoolId = school, CategoryCode = "B", Name = "Synthetic", CreatedAt = at.AddDays(-2), Revision = 1 });
        await db.SaveChangesAsync();
        db.Add(new LD.TrainingProgramVersion { Id = version, SchoolId = school, ProgramId = program, Version = 1, Status = LD.ProgramStatus.Draft, PathwayCode = "synthetic", TheoryMinutesPerUnit = 60, PracticalMinutesPerUnit = 60, EffectiveFrom = new(2026, 1, 1), CreatedBy = actor, CreatedAt = at.AddDays(-2) });
        await db.SaveChangesAsync();
        db.Add(new LD.AssessmentDefinition { Id = definition, SchoolId = school, ProgramId = program, Code = "synthetic" });
        await db.SaveChangesAsync();
        db.Add(new LD.AssessmentDefinitionVersion { Id = definitionVersion, SchoolId = school, DefinitionId = definition, ProgramId = program, ProgramVersionId = version,
            Name = "Synthetic assessment", TrainingKind = LD.TrainingKind.Theory, Stage = LD.AssessmentStage.Midterm, MinimumMark = 0, MaximumMark = 10, PassingMark = 7 });
        await db.SaveChangesAsync();
        db.Add(new LD.StudentTraining { Id = enrollment, SchoolId = school, StudentId = student, CategoryCode = "B", ProgramVersionId = version,
            ProgramAssignedAt = at.AddDays(-1), ProgramAssignedBy = actor, DependencyRevision = 1, CreatedAt = at.AddDays(-1) });
        await db.SaveChangesAsync();
        db.Add(new LD.Assessment { Id = assessment, SchoolId = school, EnrollmentId = enrollment, ProgramVersionId = version, DefinitionVersionId = definitionVersion, AttemptNumber = 1, Revision = 1 });
        db.Add(new LD.AssessmentResultVersion { Id = Guid.NewGuid(), SchoolId = school, AssessmentId = assessment, EnrollmentId = enrollment, DefinitionVersionId = definitionVersion,
            Version = 1, CommandId = Guid.NewGuid(), Status = LD.ResultStatus.Recorded, AssessorId = teacher, AssessedAt = at, RecordedBy = actor, RecordedAt = at, Mark = 8, Outcome = LD.Outcome.Passed });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
    private sealed class SourceFactory<T>(string root, Dictionary<string, string?> config, SchoolEnvironment env, bool notifications = false) : WebApplicationFactory<T> where T : class
    {
        public DeliveryWorkflowTests.Provider Capture { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing").UseContentRoot(root); builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)); builder.ConfigureLogging(l => l.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("dependencies").ConfigurePrimaryHttpMessageHandler(() => new Bridge(env));
                foreach (var name in notifications ? new[] { JwtBearerDefaults.AuthenticationScheme } : ["User", "Workload"])
                    services.PostConfigure<JwtBearerOptions>(name, o => { o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>("https://localhost:7100/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(env.Auth) { RequireHttps = true }); });
                if (notifications) services.AddSingleton<IEmailProvider>(Capture);
            });
        }
    }
    private sealed class Bridge(SchoolEnvironment env) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var client = request.RequestUri!.Port switch { 7100 => env.Auth, 7001 => env.Users, 7200 => env.Client, 7300 => env.Scheduling, 7500 => env.Learning, _ => throw new HttpRequestException("Unexpected origin.") };
            using var forward = new HttpRequestMessage(request.Method, request.RequestUri); foreach (var header in request.Headers) forward.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null) { forward.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(ct)); foreach (var header in request.Content.Headers) forward.Content.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return await client!.SendAsync(forward, ct);
        }
    }
    private sealed class BrokerHarness(IDeliveryStore store, InboxWorker? worker, ServiceBusClient? client) : IAsyncDisposable
    {
        public static async Task<BrokerHarness> CreateAsync(IServiceProvider services, IDeliveryStore store)
        {
            var connection = Environment.GetEnvironmentVariable("VAIRAS_TEST_SERVICEBUS_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection)) return new(store, null, null);
            var worker = new InboxWorker(services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(new NotificationBusOptions { Enabled = true, ConnectionString = connection, Subscription = "notifications-six" }), NullLogger<InboxWorker>.Instance);
            await worker.StartAsync(default); return new(store, worker, new ServiceBusClient(connection));
        }
        public async Task ConsumeAsync(SourceMessage message, INotificationWorkflow workflow)
        {
            if (client is null) { await workflow.ConsumeAsync(message, default); return; }
            await using var sender = client.CreateSender(message.Producer == "scheduling" ? "vairas.scheduling.events.v1" : "vairas.learning.events.v1");
            await sender.SendMessageAsync(BrokerTests.ToMessage(message));
            await BrokerTests.Wait(async () => { await using var tx = await store.OpenAsync(message.SchoolId, default); return await tx.InboxAsync(message, default) is not null; });
        }
        public async ValueTask DisposeAsync()
        { if (worker is not null) { await worker.StopAsync(default); worker.Dispose(); } if (client is not null) await client.DisposeAsync(); }
    }
}
