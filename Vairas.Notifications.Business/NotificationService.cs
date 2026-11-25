using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public sealed class NotificationService(INotificationStore store, TimeProvider clock) : INotificationService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    public async Task<IReadOnlyList<TemplateResponse>> TemplatesAsync(Guid school, NotificationActor actor, CancellationToken ct)
    {
        RequireActor(school, actor, true);
        await using var session = await store.OpenAsync(school, false, ct);
        Fresh(actor, clock.GetUtcNow());
        return (await session.TemplatesAsync(ct)).Select(Response).ToArray();
    }

    public async Task<TemplateResponse> TemplateAsync(Guid school, Guid template, NotificationActor actor, CancellationToken ct)
    {
        Id(template); RequireActor(school, actor, true);
        await using var session = await store.OpenAsync(school, false, ct);
        Fresh(actor, clock.GetUtcNow());
        return Response(await session.TemplateAsync(template, ct) ?? throw NotificationErrors.Missing());
    }

    public Task<CommandResponse<TemplateResponse>> CreateTemplateAsync(Guid school, Guid operation, CreateTemplateRequest request, NotificationActor actor, CancellationToken ct) =>
        Mutate(school, operation, operation, "CreateTemplate", request, actor, async (session, now) =>
        {
            var template = NotificationTemplate.Create(school, operation, request.Type, request.SubjectTemplate, request.BodyTemplate, now);
            await session.InsertTemplateAsync(template, ct);
            return (Response(template), template.Revision);
        }, ct);

    public Task<CommandResponse<TemplateResponse>> UpdateTemplateAsync(Guid school, Guid template, Guid operation, UpdateTemplateRequest request, NotificationActor actor, CancellationToken ct) =>
        Mutate(school, template, operation, "UpdateTemplate", request, actor, async (session, now) =>
        {
            var existing = await session.TemplateAsync(template, ct) ?? throw NotificationErrors.Missing();
            var updated = existing.Update(request.SubjectTemplate, request.BodyTemplate, request.ExpectedRevision, now);
            await session.UpdateTemplateAsync(updated, existing.Revision, ct);
            return (Response(updated), updated.Revision);
        }, ct);

    public Task<CommandResponse<TemplateResponse>> ArchiveTemplateAsync(Guid school, Guid template, Guid operation, RevisionRequest request, NotificationActor actor, CancellationToken ct) =>
        Mutate(school, template, operation, "ArchiveTemplate", request, actor, async (session, now) =>
        {
            var existing = await session.TemplateAsync(template, ct) ?? throw NotificationErrors.Missing();
            var updated = existing.Archive(request.ExpectedRevision, now);
            await session.UpdateTemplateAsync(updated, existing.Revision, ct);
            return (Response(updated), updated.Revision);
        }, ct);

    public async Task<NotificationStatusResponse> StatusAsync(Guid school, Guid notification, NotificationActor actor, CancellationToken ct)
    {
        Id(notification); RequireActor(school, actor, false);
        await using var session = await store.OpenAsync(school, false, ct);
        Fresh(actor, clock.GetUtcNow());
        var row = await session.NotificationAsync(notification, ct) ?? throw NotificationErrors.Missing();
        if (!actor.Administrator && row.RecipientUserId != actor.UserId) throw NotificationErrors.Missing();
        return new(row.NotificationId, row.Delivery.Status, row.Delivery.Revision);
    }

    public Task<CommandResponse<NotificationStatusResponse>> CancelAsync(Guid school, Guid notification, Guid operation, RevisionRequest request, NotificationActor actor, CancellationToken ct) =>
        Mutate(school, notification, operation, "CancelPendingNotification", request, actor, async (session, now) =>
        {
            var existing = await session.NotificationAsync(notification, ct) ?? throw NotificationErrors.Missing();
            var updated = existing.Delivery.Cancel(request.ExpectedRevision, now);
            await session.UpdateDeliveryAsync(notification, updated, existing.Delivery.Revision, ct);
            return (new NotificationStatusResponse(notification, updated.Status, updated.Revision), updated.Revision);
        }, ct);

    private async Task<CommandResponse<T>> Mutate<T>(Guid school, Guid target, Guid operation, string command, object request, NotificationActor actor,
        Func<INotificationSession, DateTimeOffset, Task<(T Result, long Revision)>> change, CancellationToken ct)
    {
        Id(target); Id(operation); RequireActor(school, actor, true);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command, target, request }, Json))));
        await using var session = await store.OpenAsync(school, true, ct);
        Fresh(actor, clock.GetUtcNow());
        var receipt = await session.ReceiptAsync(operation, ct);
        if (receipt is not null)
        {
            if (receipt.ActorUserId != actor.UserId || receipt.CommandName != command || receipt.TargetId != target || receipt.RequestHash != hash)
                throw new ConflictException("IDEMPOTENCY_CONFLICT");
            var original = JsonSerializer.Deserialize<T>(receipt.ResponseJson, Json) ?? throw new InvalidOperationException("Invalid stored receipt.");
            return new(operation, original, true);
        }
        var now = await session.NowAsync(ct);
        T result; long revision;
        try { (result, revision) = await change(session, now); }
        catch (NotificationRuleException e) when (e.Code is "REVISION_CONFLICT" or "CANCELLATION_TOO_LATE" or "TEMPLATE_ARCHIVED")
        { throw new ConflictException(e.Code, innerException: e); }
        catch (NotificationRuleException e)
        { throw new BadRequestException(e.Code, innerException: e); }
        Fresh(actor, clock.GetUtcNow());
        await session.InsertReceiptAsync(new(school, operation, actor.UserId, command, target, hash, actor.AuthorityBasis, revision,
            JsonSerializer.Serialize(result, Json), now), ct);
        await session.CommitAsync(ct);
        return new(operation, result, false);
    }

    private static TemplateResponse Response(NotificationTemplate template) => new(template.TemplateId, template.Type, template.SubjectTemplate,
        template.BodyTemplate, template.Status, template.Revision, template.CreatedAt, template.UpdatedAt);

    private static void RequireActor(Guid school, NotificationActor actor, bool admin)
    {
        Id(school);
        if (actor.UserId == Guid.Empty || actor.SchoolId != school || string.IsNullOrWhiteSpace(actor.AuthorityBasis) || admin && !actor.Administrator)
            throw new ForbiddenException("ACCESS_DENIED");
    }
    private static void Fresh(NotificationActor actor, DateTimeOffset now)
    {
        if (actor.ValidUntil <= now || actor.ValidUntil > now.AddSeconds(30)) throw new ServiceUnavailableException("AUTHORITY_EXPIRED");
    }
    private static void Id(Guid id) { if (id == Guid.Empty) throw NotificationErrors.Invalid(); }
}
