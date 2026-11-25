using Vairas.Notifications.Configuration;
using Vairas.Notifications.Middleware;
using Vairas.Notifications.Startup;

if (await StartupCommands.TryRunAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonLogging();
builder.Services
    .AddNotificationOptions(builder.Configuration)
    .AddNotificationDatabase()
    .AddNotificationServices()
    .AddDependencyHttpClients()
    .AddBackgroundWorkers()
    .AddStrictJsonSerialization()
    .AddExceptionHandling()
    .AddOpenApiDocument()
    .AddBearerAuthentication()
    .AddEntryAuthorization()
    .AddServiceHealthChecks()
    .AddServiceTelemetry(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(status => status.HttpContext.WriteProblemAsync(status.HttpContext.Response.StatusCode, "HTTP_ERROR", "The request could not be completed."));
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapServiceHealthChecks();
app.MapOpenApiDocument();
app.Run();

public partial class Program;
