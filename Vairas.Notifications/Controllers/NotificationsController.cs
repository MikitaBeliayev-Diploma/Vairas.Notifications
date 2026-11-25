using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vairas.Notifications.Business;
using Vairas.Notifications.Integrations;

namespace Vairas.Notifications.Controllers;

[Authorize, Route("api/v1/schools/{schoolId:guid}")]
public sealed class NotificationsController(INotificationService service, INotificationActorFactory actors) : BaseApiController
{
    /// <summary>List school templates under current school administrator authority.</summary>
    [HttpGet("notification-templates", Name = "getNotificationTemplates")]
    public async Task<ActionResult<IReadOnlyList<TemplateResponse>>> Templates(Guid schoolId, CancellationToken ct) =>
        Ok(await service.TemplatesAsync(schoolId, await actors.CreateAsync(HttpContext, schoolId, true, ct), ct));

    /// <summary>Read one school template.</summary>
    [HttpGet("notification-templates/{templateId:guid}", Name = "getNotificationTemplate")]
    public async Task<ActionResult<TemplateResponse>> Template(Guid schoolId, Guid templateId, CancellationToken ct) =>
        Ok(await service.TemplateAsync(schoolId, templateId, await actors.CreateAsync(HttpContext, schoolId, true, ct), ct));

    /// <summary>Create an active template. One active template per school and type.</summary>
    [HttpPost("notification-templates", Name = "createNotificationTemplate")]
    [ProducesResponseType<CommandResponse<TemplateResponse>>(201)]
    [ProducesResponseType<CommandResponse<TemplateResponse>>(200)]
    public async Task<ActionResult<CommandResponse<TemplateResponse>>> Create(Guid schoolId, CreateTemplateRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid operation, CancellationToken ct)
    {
        var result = await service.CreateTemplateAsync(schoolId, operation, request, await actors.CreateAsync(HttpContext, schoolId, true, ct), ct);
        return result.Replayed ? Ok(result) : CreatedAtAction(nameof(Template), new { schoolId, templateId = result.Resource.TemplateId }, result);
    }

    /// <summary>Update template content with its expected revision.</summary>
    [HttpPut("notification-templates/{templateId:guid}", Name = "updateNotificationTemplate")]
    public async Task<ActionResult<CommandResponse<TemplateResponse>>> Update(Guid schoolId, Guid templateId, UpdateTemplateRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid operation, CancellationToken ct) =>
        Ok(await service.UpdateTemplateAsync(schoolId, templateId, operation, request, await actors.CreateAsync(HttpContext, schoolId, true, ct), ct));

    /// <summary>Archive an active template without changing historical notifications.</summary>
    [HttpPost("notification-templates/{templateId:guid}/archive", Name = "archiveNotificationTemplate")]
    public async Task<ActionResult<CommandResponse<TemplateResponse>>> Archive(Guid schoolId, Guid templateId, RevisionRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid operation, CancellationToken ct) =>
        Ok(await service.ArchiveTemplateAsync(schoolId, templateId, operation, request, await actors.CreateAsync(HttpContext, schoolId, true, ct), ct));

    /// <summary>Read status as the recipient or a current school administrator.</summary>
    [HttpGet("notifications/{notificationId:guid}", Name = "getNotificationStatus")]
    public async Task<ActionResult<NotificationStatusResponse>> Status(Guid schoolId, Guid notificationId, CancellationToken ct) =>
        Ok(await service.StatusAsync(schoolId, notificationId, await actors.CreateAsync(HttpContext, schoolId, false, ct), ct));

    /// <summary>Cancel a Pending notification. Sending and DeliveryUnknown cannot be recalled.</summary>
    [HttpPost("notifications/{notificationId:guid}/cancel", Name = "cancelPendingNotification")]
    public async Task<ActionResult<CommandResponse<NotificationStatusResponse>>> Cancel(Guid schoolId, Guid notificationId, RevisionRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid operation, CancellationToken ct) =>
        Ok(await service.CancelAsync(schoolId, notificationId, operation, request, await actors.CreateAsync(HttpContext, schoolId, true, ct), ct));
}
