using FinWallet.Application.DailySummary.Processing;
using FinWallet.Infrastructure.Messaging.DailySummary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FinWallet.Infrastructure.BackgroundServices.DailySummary;

public sealed class DailySummaryBackgroundService : BackgroundService
{
    private readonly SummaryProcessor _store;
    private readonly ILogger<DailySummaryBackgroundService> _logger;
    private readonly Func<CancellationToken, Task<RabbitMqDailySummaryTransport>> _open;
    private readonly string _queue;
    private readonly ushort _prefetch;
    private DailySummaryConsumerSession? _session;

    public DailySummaryBackgroundService(SummaryProcessor store, IConfiguration config, ILogger<DailySummaryBackgroundService> logger)
        : this(store, config, logger, ct => RabbitMqDailySummaryTransport.Open(config, ct)) { }

    // Internal transport factory permits real-channel failure injection in the test host.
    internal DailySummaryBackgroundService(SummaryProcessor store, IConfiguration config, ILogger<DailySummaryBackgroundService> logger,
        Func<CancellationToken, Task<RabbitMqDailySummaryTransport>> open)
    {
        _prefetch = config.GetValue<ushort>("DailySummary:PrefetchCount", 1);
        if (_prefetch is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(config), "PrefetchCount must be between 1 and 256");
        _store = store;
        _logger = logger;
        _open = open;
        _queue = (config["RabbitMq:QueueName"] ?? "finwallet.events") + ".daily.v1";
    }

    public bool Connected => Volatile.Read(ref _session)?.Connected is true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var session = new DailySummaryConsumerSession(stoppingToken);
                Volatile.Write(ref _session, session);
                try { await RunSession(session); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Daily consumer session failed"); }
                finally { session.End("session-disposed"); }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunSession(DailySummaryConsumerSession session)
    {
        await using var transport = await _open(session.Token);
        var connection = transport.Connection;
        var channel = transport.Channel;
        var consumer = new AsyncEventingBasicConsumer(channel);
        Task ConnectionClosed(object sender, ShutdownEventArgs args) => End("connection-closed");
        Task ChannelClosed(object sender, ShutdownEventArgs args) => End("channel-closed");
        Task ConsumerCancelled(object sender, ConsumerEventArgs args) => End("consumer-cancelled");
        Task End(string reason)
        {
            // Never dispose or wait for the dispatcher from one of its own callbacks.
            session.End(reason);
            return Task.CompletedTask;
        }

        connection.ConnectionShutdownAsync += ConnectionClosed;
        channel.ChannelShutdownAsync += ChannelClosed;
        // UnregisteredAsync covers server basic.cancel and client basic.cancel-ok in RabbitMQ.Client 7.2.
        consumer.UnregisteredAsync += ConsumerCancelled;
        consumer.ReceivedAsync += Receive;
        try
        {
            await channel.QueueDeclareAsync(_queue, true, false, false, cancellationToken: session.Token);
            await channel.BasicQosAsync(0, _prefetch, false, session.Token);
            var tag = await channel.BasicConsumeAsync(_queue, false, consumer, session.Token);
            if (session.TryActivate()) _logger.LogInformation("Daily consumer subscription active: {ConsumerTag}", tag);
            var reason = await session.Ended.WaitAsync(session.Token);
            _logger.LogWarning("Daily consumer session ended: {Reason} ConnectionOpen={ConnectionOpen} ChannelOpen={ChannelOpen}",
                reason, connection.IsOpen, channel.IsOpen);
        }
        finally
        {
            session.End("session-disposed");
            // Cancel DB/retry work before disposing the channel, which drains its delivery dispatcher.
            await session.CancelAsync();
            connection.ConnectionShutdownAsync -= ConnectionClosed;
            channel.ChannelShutdownAsync -= ChannelClosed;
            consumer.UnregisteredAsync -= ConsumerCancelled;
            consumer.ReceivedAsync -= Receive;
        }

        async Task Receive(object sender, BasicDeliverEventArgs delivery)
        {
            try { await ProcessDelivery(session, channel, delivery); }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        }
    }

    private async Task ProcessDelivery(DailySummaryConsumerSession session, IChannel channel, BasicDeliverEventArgs delivery)
    {
        var payload = Encoding.UTF8.GetString(delivery.Body.Span);
        var type = delivery.BasicProperties.Type ?? "unknown";
        var id = Guid.TryParse(delivery.BasicProperties.MessageId, out var parsed) ? parsed : Guid.NewGuid();
        for (var attempt = 1; !session.Ended.IsCompleted; attempt++)
        {
            try { await _store.Apply(type, payload, session.Token); }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (session.Ended.IsCompleted) return;
                _logger.LogWarning(ex, "Daily projection delivery {Id} attempt {Attempt}", id, attempt);
                if (ex is not InvalidDataException && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), session.Token);
                    continue;
                }
                try { await _store.Quarantine(id, type, payload, ex.Message, attempt, session.Token); }
                catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { return; }
                catch (Exception quarantineError)
                {
                    _logger.LogWarning(quarantineError, "Quarantine unavailable; delivery remains unacknowledged");
                    session.End("quarantine-unavailable");
                    return;
                }
            }

            // A failed ack is a transport failure, not a failed projection. Redelivery uses the inbox.
            if (session.Ended.IsCompleted) return;
            try { await channel.BasicAckAsync(delivery.DeliveryTag, false, session.Token); }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Daily delivery {Id} acknowledgement failed", id);
                session.End("ack-failed");
            }
            return;
        }
    }
}
