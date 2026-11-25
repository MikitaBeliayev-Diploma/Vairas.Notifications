using Vairas.Notifications.Domain;

namespace Vairas.Notifications.Business;

public interface INotificationOwners
{
    Task<SourceFact?> SourceAsync(string producer, Guid school, Guid source, CancellationToken ct);
    Task<RecipientScope> RecipientsAsync(SourceFact source, CancellationToken ct);
    Task<ContactEvidence> ContactAsync(Guid user, CancellationToken ct);
}
