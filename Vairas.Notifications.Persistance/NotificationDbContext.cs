using Microsoft.EntityFrameworkCore;
using Vairas.Notifications.Business;
using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Persistance;

/// <summary>Maps existing SQL-owned tables; native migrations remain authoritative.</summary>
internal sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        var templates = model.Entity<NotificationTemplate>();
        templates.ToTable("notification_template", "notifications");
        templates.HasKey(x => new { x.SchoolId, x.TemplateId });
        templates.Property(x => x.Type).HasColumnName("notification_type").HasConversion<string>();
        templates.Property(x => x.Status).HasConversion<string>();
        var receipts = model.Entity<OperationReceipt>();
        receipts.ToTable("notification_operation", "notifications");
        receipts.HasKey(x => new { x.SchoolId, x.OperationId });
        receipts.Property(x => x.ResponseJson).HasColumnType("jsonb");
        receipts.Property<string>("Channel");
        receipts.Property<string>("Outcome");
        receipts.Property<string>("OutcomeCode");
        var notifications = model.Entity<NotificationDeliveryRow>();
        notifications.ToTable("notification", "notifications");
        notifications.HasKey(x => new { x.SchoolId, x.NotificationId });
        notifications.Property(x => x.Status).HasConversion<string>();
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.Name != nameof(NotificationTemplate.Type))
                    property.SetColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant());
    }
}
