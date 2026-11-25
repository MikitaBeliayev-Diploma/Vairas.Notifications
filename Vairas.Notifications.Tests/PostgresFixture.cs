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

public sealed class PostgresFixture : IAsyncDisposable
{
    private PostgreSqlContainer? container;
    private string admin = "", database = "", runtimeRole = "", workerRole = "";
    private readonly RSA rsa = RSA.Create(2048);
    public string OwnerConnection { get; private set; } = "";
    public string RuntimeConnection { get; private set; } = "";
    public string WorkerConnection { get; private set; } = "";
    public string AdminConnection => admin;
    public ApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public AuthorityTransport Authority { get; } = new();
    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
    public async Task StartAsync()
    {
        admin = Environment.GetEnvironmentVariable("VAIRAS_TEST_ADMIN_CONNECTION") ?? "";
        if (admin.Length == 0)
        {
            container = new PostgreSqlBuilder("postgres:17").Build();
            await container.StartAsync(); admin = container.GetConnectionString();
        }
        var suffix = Guid.NewGuid().ToString("N");
        database = "notifications_test_" + suffix; runtimeRole = "nt_runtime_" + suffix;
        workerRole = "nt_worker_" + suffix;
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await Sql(admin, $"CREATE ROLE {runtimeRole} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
        await Sql(admin, $"CREATE ROLE {workerRole} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
        await Sql(admin, $"CREATE DATABASE {database}");
        OwnerConnection = new NpgsqlConnectionStringBuilder(admin) { Database = database, Pooling = false }.ConnectionString;
        RuntimeConnection = new NpgsqlConnectionStringBuilder(OwnerConnection) { Username = runtimeRole, Password = password }.ConnectionString;
        WorkerConnection = new NpgsqlConnectionStringBuilder(OwnerConnection) { Username = workerRole, Password = password }.ConnectionString;
        await DatabaseMigrator.MigrateAsync(OwnerConnection);
        await DatabaseMigrator.MigrateAsync(OwnerConnection);
        await Sql(OwnerConnection, (await File.ReadAllTextAsync(Path.Combine(Root, "database/runtime-grants.sql"))).Replace("v_notifications_runtime", runtimeRole));
        await Sql(OwnerConnection, (await File.ReadAllTextAsync(Path.Combine(Root, "database/worker-grants.sql"))).Replace("v_notifications_worker", workerRole));
        Factory = new(this, new RsaSecurityKey(rsa) { KeyId = "notifications-test" });
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
    }

    public string Token(string audience = "vairas-notifications", bool session = true)
    {
        var claims = new List<Claim> { new("sub", Authority.Identity.ToString("D")) };
        if (session) claims.Add(new("sid", Authority.Session.ToString("D")));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("https://identity.test", audience, claims, DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10), new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "notifications-test" }, SecurityAlgorithms.RsaSha256)));
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body = null, Guid? operation = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: NotificationService.Json);
        if (operation is not null) request.Headers.Add("Idempotency-Key", operation.Value.ToString("D"));
        return await Client.SendAsync(request);
    }

    public async Task SeedNotificationAsync(Guid school, Guid id, Guid recipient, string status = "Pending", Guid? template = null)
    {
        await using var connection = new NpgsqlConnection(OwnerConnection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO notifications.notification(school_id,notification_id,recipient_user_id,notification_type,subject,body,destination_address,contact_revision,
                source_context,source_id,source_revision,source_event_id,dispatch_key,scheduled_at,status,attempt_count,created_at,updated_at,template_id,template_revision)
            VALUES(@school,@id,@user,'LessonScheduled','Immutable subject','Immutable body','synthetic@example.test','contact-1','Scheduling',@id,1,@id,@dispatch,
                now(),@status,CASE WHEN @status='Pending' THEN 0 ELSE 1 END,now(),now(),@template,CASE WHEN @template IS NULL THEN NULL ELSE 1 END)
            """, connection);
        command.Parameters.AddWithValue("school", school); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("user", recipient);
        command.Parameters.AddWithValue("dispatch", id.ToString("D")); command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("template", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)template ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task Sql(string connection, string sql)
    { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync(); }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose(); if (Factory is not null) await Factory.DisposeAsync();
        if (database.Length > 0) await Sql(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        if (runtimeRole.Length > 0) await Sql(admin, $"DROP ROLE IF EXISTS {runtimeRole}");
        if (workerRole.Length > 0) await Sql(admin, $"DROP ROLE IF EXISTS {workerRole}");
        if (container is not null) await container.DisposeAsync(); rsa.Dispose();
    }
}
