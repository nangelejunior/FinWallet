using System.Text;
using FinWallet.Application.Abstractions.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FinWallet.Infrastructure.Messaging;

public sealed class RabbitMqEventPublisher(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqEventPublisher> logger) : IEventPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public async Task PublishAsync(Guid eventId, string eventType, string payload,
        CancellationToken cancellationToken = default)
    {
        // A channel is shared across batches; never publish concurrently on it.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection?.IsOpen != true || _channel?.IsOpen != true)
            {
                await ResetAsync();
                var factory = new ConnectionFactory
                {
                    HostName = _options.HostName, Port = _options.Port,
                    UserName = _options.UserName, Password = _options.Password,
                    // Failed sessions are replaced on the next outbox attempt.
                    AutomaticRecoveryEnabled = false
                };
                _connection = await factory.CreateConnectionAsync(cancellationToken);
                _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true), cancellationToken);
                foreach (var name in new[] { _options.QueueName, _options.QueueName + ".daily.v1" })
                    await _channel.QueueDeclareAsync(name, durable: true, exclusive: false,
                        autoDelete: false, arguments: null, cancellationToken: cancellationToken);
            }

            var queue = eventType is FinWallet.Application.IntegrationEvents.PostedEvent.Type
                or FinWallet.Application.IntegrationEvents.WalletRegistered.Type
                ? _options.QueueName + ".daily.v1" : _options.QueueName;
            await _channel!.BasicPublishAsync(exchange: string.Empty, routingKey: queue,
                mandatory: true, basicProperties: new BasicProperties
                {
                    MessageId = eventId.ToString("D"), Type = eventType,
                    ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent
                }, body: Encoding.UTF8.GetBytes(payload), cancellationToken: cancellationToken);
            logger.LogInformation("RabbitMQ confirmed Outbox event {EventId} of type {EventType}", eventId, eventType);
        }
        catch
        {
            // Delivery may have succeeded: preserve MessageId and let the outbox retry.
            await ResetAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async ValueTask ResetAsync()
    {
        var channel = _channel; var connection = _connection;
        _channel = null; _connection = null;
        try { if (channel is not null) await channel.DisposeAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to dispose publisher channel"); }
        try { if (connection is not null) await connection.DisposeAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to dispose publisher connection"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; await ResetAsync(); }
        finally { _gate.Release(); }
    }
}
