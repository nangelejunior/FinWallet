using FinWallet.Application.Abstractions.Messaging;
using FinWallet.Application.Outbox;
using FinWallet.Infrastructure.Messaging;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.Infrastructure.Randomness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FinWallet.Validation;

public sealed class BrokerProbe : IAsyncDisposable
{
    public RabbitMqOptions Options { get; } = ValidationEnvironment.Rabbit();
    public IConnection Connection { get; private set; } = null!;
    public IChannel Channel { get; private set; } = null!;

    public async Task Open()
    {
        Connection = await new ConnectionFactory
        {
            HostName = Options.HostName, Port = Options.Port, UserName = Options.UserName,
            Password = Options.Password, AutomaticRecoveryEnabled = false
        }.CreateConnectionAsync();
        Channel = await Connection.CreateChannelAsync();
        await Channel.QueueDeclareAsync(Options.QueueName, true, false, false);
        await Channel.QueueDeclareAsync(Options.QueueName + ".daily.v1", true, false, false);
    }
    private readonly List<RabbitMqEventPublisher> _publishers = [];
    public IEventPublisher Publisher(int? port = null)
    {
        var publisher = new RabbitMqEventPublisher(
            Microsoft.Extensions.Options.Options.Create(new RabbitMqOptions
            {
                HostName = Options.HostName, Port = port ?? Options.Port, UserName = Options.UserName,
                Password = Options.Password, QueueName = Options.QueueName
            }), NullLogger<RabbitMqEventPublisher>.Instance);
        _publishers.Add(publisher);
        return publisher;
    }

    public async Task<BasicGetResult[]> Drain(string? queueName = null)
    {
        var results = new List<BasicGetResult>();
        BasicGetResult? message;
        foreach (var queue in queueName is null ? new[] { Options.QueueName, Options.QueueName + ".daily.v1" } : new[] { queueName })
            while ((message = await Channel.BasicGetAsync(queue, true)) is not null) results.Add(message);
        return results.ToArray();
    }
    public static async Task<int> Process(IEventPublisher publisher, int maxAttempts = 3, CancellationToken ct = default)
    {
        await using var db = FinancialHarness.Db();
        var options = new OutboxProcessorOptions
        {
            BatchSize = 100, MaxAttempts = maxAttempts, LockDuration = TimeSpan.FromSeconds(10),
            InitialRetryDelay = TimeSpan.FromMilliseconds(20), MaxRetryDelay = TimeSpan.FromMilliseconds(80)
        };
        return await new OutboxProcessor(new OutboxRepository(db), publisher, Microsoft.Extensions.Options.Options.Create(options),
            new DefaultRandomProvider(), NullLogger<OutboxProcessor>.Instance).ProcessBatchAsync(ct);
    }
    public static async Task MakeEligible()
    {
        await using var db = FinancialHarness.Db();
        await db.OutboxMessages.Where(x => x.ProcessedAt == null && x.DeadLetteredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1))
                .SetProperty(x => x.LockedUntil, DateTime.UtcNow.AddMinutes(-1)));
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var publisher in _publishers) await publisher.DisposeAsync();
        if (Channel is not null)
        {
            if (Channel.IsOpen)
            {
                await Channel.QueueDeleteAsync(Options.QueueName);
                await Channel.QueueDeleteAsync(Options.QueueName + ".daily.v1");
            }
            await Channel.DisposeAsync();
        }
        if (Connection is not null) await Connection.DisposeAsync();
    }
}
