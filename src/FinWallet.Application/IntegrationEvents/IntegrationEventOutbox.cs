using System.Text.Json;
using FinWallet.Application.Transfers.Events;
using FinWallet.Domain.Entities;

namespace FinWallet.Application.IntegrationEvents;

internal static class IntegrationEventOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static OutboxMessage Create(PostedEvent message) =>
        Create(PostedEvent.Type, message, message.PostedAt.UtcDateTime);

    internal static OutboxMessage Create(WalletRegistered message, DateTime occurredAt) =>
        Create(WalletRegistered.Type, message, occurredAt);

    internal static OutboxMessage Create(TransferCompletedEvent message) =>
        Create(nameof(TransferCompletedEvent), message, message.OccurredAt);

    private static OutboxMessage Create<T>(string type, T message, DateTime occurredAt) =>
        OutboxMessage.Create(type, JsonSerializer.Serialize(message, Json), occurredAt);
}
