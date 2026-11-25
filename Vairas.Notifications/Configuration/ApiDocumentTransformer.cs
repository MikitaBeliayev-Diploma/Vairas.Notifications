using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Vairas.Notifications.Configuration;

public sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info.Title = "Vairas Notifications API";
        document.Info.Version = "0.2.0";
        document.Info.Description = "School templates, authorized status and Pending cancellation. Durable Scheduling/Learning consumption, current owner/contact revalidation and Gmail API delivery run through configured background workers. Workers default to disabled until external credentials are supplied.";
        document.Components ??= new();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        { ["bearerAuth"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" } };
        foreach (var item in document.Paths.Values)
            foreach (var operation in item.Operations!.Values)
            {
                operation.Security = [new() { [new OpenApiSecuritySchemeReference("bearerAuth", document)] = [] }];
                foreach (var parameter in operation.Parameters ?? [])
                    if (parameter is OpenApiParameter p && p.Name == "Idempotency-Key")
                    { p.Required = true; p.Description = "Nonempty UUID. School-scoped, bound to actor, command, target and payload. Replays reauthorize and return the original resource."; }
                operation.Responses ??= new();
                foreach (var status in new[] { "400", "401", "403", "404", "409", "415", "500", "503" })
                    operation.Responses.TryAdd(status, new OpenApiResponse { Description = "ProblemDetails with stable code and correlationId.",
                        Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object } } } });
                if (operation.Responses.TryGetValue("201", out var response) && response is OpenApiResponse created)
                    created.Headers = new Dictionary<string, IOpenApiHeader> { ["Location"] = new OpenApiHeader { Description = "Created template URL", Schema = new OpenApiSchema { Type = JsonSchemaType.String } } };
            }
        return Task.CompletedTask;
    }
}
