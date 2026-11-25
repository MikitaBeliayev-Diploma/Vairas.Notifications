using NUnit.Framework;
using Vairas.Notifications.Business;
using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Tests;

[TestFixture, Category("Unit")]
public sealed class BusinessAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    [Test]
    public async Task Admission_uses_application_clock_independently_of_database_clock()
    {
        var school = Guid.NewGuid(); var store = new EmptyStore();
        var service = new NotificationService(store, new Clock());
        var actor = new NotificationActor(school, Guid.NewGuid(), true, Now.AddSeconds(20), "verified-session-and-membership");
        Assert.That(await service.TemplatesAsync(school, actor, default), Is.Empty);
        var missing = Business.ThrowsBusinessAsync(async () => await service.TemplateAsync(school, Guid.NewGuid(), actor, default));
        Assert.That(missing!.HttpStatus(), Is.EqualTo(404));
        var missingStatus = Business.ThrowsBusinessAsync(async () => await service.StatusAsync(school, Guid.NewGuid(), actor, default));
        Assert.That(missingStatus!.HttpStatus(), Is.EqualTo(404));
        Assert.That(store.DatabaseClockReads, Is.Zero, "Authority TTL must not depend on another machine's clock.");
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(31)]
    public void Expired_or_unbounded_admissions_are_denied(int seconds)
    {
        var school = Guid.NewGuid(); var service = new NotificationService(new EmptyStore(), new Clock());
        var actor = new NotificationActor(school, Guid.NewGuid(), true, Now.AddSeconds(seconds), "verified");
        Assert.That(Business.ThrowsBusinessAsync(async () => await service.TemplatesAsync(school, actor, default))!.Code, Is.EqualTo("AUTHORITY_EXPIRED"));
    }

    [TestCase("school")]
    [TestCase("user")]
    [TestCase("admin")]
    [TestCase("basis")]
    public void Invalid_actor_admission_never_opens_a_transaction(string failure)
    {
        var school = Guid.NewGuid(); var store = new EmptyStore(); var service = new NotificationService(store, new Clock());
        var actor = new NotificationActor(failure == "school" ? Guid.NewGuid() : school, failure == "user" ? Guid.Empty : Guid.NewGuid(),
            failure != "admin", Now.AddSeconds(20), failure == "basis" ? "" : "verified");
        Assert.That(Business.ThrowsBusinessAsync(async () => await service.TemplatesAsync(school, actor, default))!.HttpStatus(), Is.EqualTo(403));
        Assert.That(store.Opens, Is.Zero);
    }

    [Test]
    public void Empty_resource_and_operation_ids_are_validation_errors()
    {
        var school = Guid.NewGuid(); var store = new EmptyStore(); var service = new NotificationService(store, new Clock());
        var actor = new NotificationActor(school, Guid.NewGuid(), true, Now.AddSeconds(20), "verified");
        Assert.That(Business.ThrowsBusinessAsync(async () => await service.TemplateAsync(school, Guid.Empty, actor, default))!.HttpStatus(), Is.EqualTo(400));
        Assert.That(Business.ThrowsBusinessAsync(async () => await service.CreateTemplateAsync(school, Guid.Empty, new(NotificationType.LessonReminder, "a", "b"), actor, default))!.HttpStatus(), Is.EqualTo(400));
        Assert.That(Business.ThrowsBusinessAsync(async () => await service.TemplatesAsync(Guid.Empty, actor, default))!.HttpStatus(), Is.EqualTo(400));
        Assert.That(store.Opens, Is.Zero);
    }

    [Test]
    public void Domain_template_validation_is_translated_at_the_application_boundary()
    {
        var school = Guid.NewGuid();
        var service = new NotificationService(new EmptyStore(), new Clock());
        var actor = new NotificationActor(school, Guid.NewGuid(), true, Now.AddSeconds(20), "verified");
        var error = Assert.ThrowsAsync<BadRequestException>(async () => await service.CreateTemplateAsync(school, Guid.NewGuid(),
            new(NotificationType.LessonReminder, "", ""), actor, default));
        Assert.That(error!.Code, Is.EqualTo("INVALID_TEMPLATE"));
        Assert.That(error.InnerException, Is.TypeOf<NotificationRuleException>());
    }

    private sealed class EmptyStore : INotificationStore, INotificationSession
    {
        public int Opens { get; private set; }
        public int DatabaseClockReads { get; private set; }
        public Task<INotificationSession> OpenAsync(Guid school, bool write, CancellationToken ct) { Opens++; return Task.FromResult<INotificationSession>(this); }
        public Task<DateTimeOffset> NowAsync(CancellationToken ct) { DatabaseClockReads++; return Task.FromResult(Now.AddMinutes(-5)); }
        public Task<IReadOnlyList<NotificationTemplate>> TemplatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<NotificationTemplate>>([]);
        public Task<NotificationTemplate?> TemplateAsync(Guid id, CancellationToken ct) => Task.FromResult<NotificationTemplate?>(null);
        public Task<StoredNotification?> NotificationAsync(Guid id, CancellationToken ct) => Task.FromResult<StoredNotification?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task InsertTemplateAsync(NotificationTemplate template, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateTemplateAsync(NotificationTemplate template, long expectedRevision, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateDeliveryAsync(Guid id, NotificationDelivery delivery, long expectedRevision, CancellationToken ct) => throw new NotSupportedException();
        public Task<OperationReceipt?> ReceiptAsync(Guid operation, CancellationToken ct) => Task.FromResult<OperationReceipt?>(null);
        public Task InsertReceiptAsync(OperationReceipt receipt, CancellationToken ct) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
