namespace Vairas.Notifications.Domain;

public sealed record NotificationTemplate(Guid SchoolId, Guid TemplateId, NotificationType Type,
    string SubjectTemplate, string BodyTemplate, TemplateStatus Status, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static NotificationTemplate Create(Guid school, Guid id, NotificationType type, string subject, string body, DateTimeOffset now)
    {
        if (school == Guid.Empty || id == Guid.Empty || !Enum.IsDefined(type)) throw new NotificationRuleException("VALIDATION_FAILED");
        NotificationRenderer.Validate(type, subject, body);
        return new(school, id, type, subject, body, TemplateStatus.Active, 1, now, now);
    }

    public NotificationTemplate Update(string subject, string body, long expectedRevision, DateTimeOffset now)
    {
        RequireEditable(expectedRevision, now);
        NotificationRenderer.Validate(Type, subject, body);
        return this with { SubjectTemplate = subject, BodyTemplate = body, Revision = Revision + 1, UpdatedAt = now };
    }

    public NotificationTemplate Archive(long expectedRevision, DateTimeOffset now)
    {
        RequireEditable(expectedRevision, now);
        return this with { Status = TemplateStatus.Archived, Revision = Revision + 1, UpdatedAt = now };
    }

    private void RequireEditable(long expected, DateTimeOffset now)
    {
        if (expected <= 0 || expected != Revision) throw new NotificationRuleException("REVISION_CONFLICT");
        if (Status != TemplateStatus.Active) throw new NotificationRuleException("TEMPLATE_ARCHIVED");
        if (now < UpdatedAt) throw new NotificationRuleException("INVALID_TIME");
    }
}
