namespace FinWallet.Application.Abstractions.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(
        Guid eventId,
        string eventType,
        string payload,
        CancellationToken cancellationToken = default);
}