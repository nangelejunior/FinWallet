using RabbitMQ.Client;
using FinWallet.Application.Abstractions.Messaging;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "Broker")]
public sealed class BrokerRecoveryTests
{
    internal static async Task SeedTransfer()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await h.Wallet(customer); var b = await h.Wallet(Guid.NewGuid());
        await h.Deposit(a, 100, "seed");
        await h.Transfer(new(customer, a, b, 10, "transfer"));
        // These transport regression cases isolate the legacy route. New routes are exercised by DailySummaryTests.
        await using var db = FinancialHarness.Db();
        await db.OutboxMessages.Where(x => x.Type != "TransferCompletedEvent")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SharedPublisherSerializesConcurrentCallsAndReconnectsAfterTransportLoss()
    {
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var proxy = new FailureProxy(broker.Options.HostName, broker.Options.Port);
        var publisher = broker.Publisher(proxy.Port);
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(ids.Select(id => publisher.PublishAsync(id, "TransferCompletedEvent", "{}")));
        var first = await broker.Drain();
        Assert.Equal(ids.Order(), first.Select(x => Guid.Parse(x.BasicProperties.MessageId!)).Order());
        proxy.DisconnectClients();
        var finalId = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            try { await publisher.PublishAsync(finalId, "TransferCompletedEvent", "{}", timeout.Token); break; }
            catch (Exception) when (!timeout.IsCancellationRequested) { await Task.Delay(100, timeout.Token); }
        }
        var final = await broker.Drain();
        Assert.NotEmpty(final);
        Assert.All(final, message => Assert.Equal(finalId.ToString("D"), message.BasicProperties.MessageId));
    }

    [Fact]
    public async Task FiveFailuresThenAdministrativeHttpReplayDeliversLegacyEvent()
    {
        await SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var proxy = new FailureProxy(broker.Options.HostName, broker.Options.Port) { Offline = true };
        for (var i = 0; i < 5; i++)
        {
            await BrokerProbe.MakeEligible();
            Assert.Equal(0, await BrokerProbe.Process(broker.Publisher(proxy.Port), maxAttempts: 5));
        }
        proxy.Offline = false;
        Assert.Equal(0, await BrokerProbe.Process(broker.Publisher(), maxAttempts: 5));
        await using var db = FinancialHarness.Db();
        var original = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Type == "TransferCompletedEvent");
        Assert.Equal(5, original.AttemptCount); Assert.NotNull(original.DeadLetteredAt);
        var port = ChildProcess.FreePort();
        await using var api = new ChildProcess(true, environment: new()
        {
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["RabbitMq__QueueName"] = broker.Options.QueueName
        });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await api.Ready(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(Guid.NewGuid(), outboxAdmin: true));
        var request = new { requestId = Guid.NewGuid(), reason = "broker restored" };
        using var response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, $"/api/v1/admin/outbox/{original.Id}/replay", request);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        using var retry = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, $"/api/v1/admin/outbox/{original.Id}/replay", request);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, retry.StatusCode);
        await using var worker = new ChildProcess(false, environment: SummaryHarness.EnvironmentFor(
            SummaryExecutable.Worker, ChildProcess.FreePort(), broker.Options.QueueName), summaryExecutable: SummaryExecutable.Worker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await db.OutboxMessages.AnyAsync(x => x.Id == original.Id && x.ProcessedAt != null, timeout.Token)) await Task.Delay(100, timeout.Token);
        var delivered = Assert.Single(await broker.Drain());
        Assert.Equal(original.Id.ToString("D"), delivered.BasicProperties.MessageId);
        Assert.Equal(original.Payload, System.Text.Encoding.UTF8.GetString(delivered.Body.Span));
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task B01_PersistentMessage_IsConfirmedBeforeMarkingProcessed()
    {
        await SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        Assert.Equal(1, await BrokerProbe.Process(broker.Publisher()));
        var message = Assert.Single(await broker.Drain());
        await using var db = FinancialHarness.Db();
        var stored = await db.OutboxMessages.SingleAsync(x => x.Type == "TransferCompletedEvent");
        Assert.NotNull(stored.ProcessedAt);
        Assert.Equal(stored.Id.ToString("D"), message.BasicProperties.MessageId);
        Assert.Equal("application/json", message.BasicProperties.ContentType);
        Assert.Equal(RabbitMQ.Client.DeliveryModes.Persistent, message.BasicProperties.DeliveryMode);
        Assert.Equal(stored.Payload, System.Text.Encoding.UTF8.GetString(message.Body.Span));
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task B02_B03_OfflineBroker_DoesNotPreventCommit_ThenDrains()
    {
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var proxy = new FailureProxy(broker.Options.HostName, broker.Options.Port) { Offline = true };
        await SeedTransfer();
        var publisher = broker.Publisher(proxy.Port);
        Assert.Equal(0, await BrokerProbe.Process(publisher));
        await using (var db = FinancialHarness.Db())
        {
            var pending = await db.OutboxMessages.SingleAsync(x => x.Type == "TransferCompletedEvent");
            Assert.Null(pending.ProcessedAt); Assert.Equal(1, pending.AttemptCount);
            Assert.NotNull(pending.NextAttemptAt);
        }
        await FinancialHarness.Reconcile();
        proxy.Offline = false;
        await BrokerProbe.MakeEligible();
        Assert.Equal(1, await BrokerProbe.Process(publisher));
        Assert.Single(await broker.Drain());
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task B04_O06_Exhaustion_DeadLettersWithoutReversingTransfer()
    {
        await SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var proxy = new FailureProxy(broker.Options.HostName, broker.Options.Port) { Offline = true };
        for (var i = 0; i < 3; i++)
        {
            await BrokerProbe.MakeEligible();
            Assert.Equal(0, await BrokerProbe.Process(broker.Publisher(proxy.Port)));
        }
        proxy.Offline = false;
        Assert.Equal(0, await BrokerProbe.Process(broker.Publisher(proxy.Port)));
        await using var db = FinancialHarness.Db();
        var failed = await db.OutboxMessages.SingleAsync(x => x.Type == "TransferCompletedEvent");
        Assert.Equal(3, failed.AttemptCount); Assert.NotNull(failed.DeadLetteredAt); Assert.Null(failed.ProcessedAt);
        Assert.Empty(await broker.Drain());
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task B05_FailureAfterBrokerConfirm_RepeatsSameMessageId()
    {
        await SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        // The broker has really confirmed, but the processor never reaches its DB confirmation.
        Assert.Equal(0, await BrokerProbe.Process(new FailAfterConfirm(broker.Publisher())));
        var first = Assert.Single(await broker.Drain());
        await using (var db = FinancialHarness.Db()) Assert.Null((await db.OutboxMessages.SingleAsync(x => x.Type == "TransferCompletedEvent")).ProcessedAt);
        await BrokerProbe.MakeEligible();
        Assert.Equal(1, await BrokerProbe.Process(broker.Publisher()));
        var second = Assert.Single(await broker.Drain());
        Assert.Equal(first.BasicProperties.MessageId, second.BasicProperties.MessageId);
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task B07_ReturnedUnroutableMessage_IsNeverMarkedProcessed()
    {
        await SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var proxy = new FailureProxy(broker.Options.HostName, broker.Options.Port)
        {
            BeforeQueueDeclareOk = async () => { await broker.Channel.QueueDeleteAsync(broker.Options.QueueName); }
        };
        Assert.Equal(0, await BrokerProbe.Process(broker.Publisher(proxy.Port)));
        await proxy.CutReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var db = FinancialHarness.Db();
        var stored = await db.OutboxMessages.SingleAsync(x => x.Type == "TransferCompletedEvent");
        Assert.Null(stored.ProcessedAt); Assert.Equal(1, stored.AttemptCount);
        Assert.Contains("PublishReturnException", stored.LastError!, StringComparison.Ordinal);
        // Recreate for orderly cleanup, then recover the same event.
        await broker.Channel.QueueDeclareAsync(broker.Options.QueueName, true, false, false);
        await BrokerProbe.MakeEligible();
        Assert.Equal(1, await BrokerProbe.Process(broker.Publisher()));
        Assert.Single(await broker.Drain());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B07_TimeoutOrConnectionClosed_IsNotSuccess(bool blackhole)
    {
        await SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var proxy = new FailureProxy(broker.Options.HostName, broker.Options.Port)
        { BlackholeResponses = blackhole, Offline = !blackhole };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (blackhole)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrokerProbe.Process(broker.Publisher(proxy.Port), ct: timeout.Token));
        else Assert.Equal(0, await BrokerProbe.Process(broker.Publisher(proxy.Port), ct: timeout.Token));
        await using var db = FinancialHarness.Db();
        Assert.Null((await db.OutboxMessages.SingleAsync(x => x.Type == "TransferCompletedEvent")).ProcessedAt);
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task O05_LeaseExpiresDuringPublish_DuplicatesTransportButRejectsOldConfirmation()
    {
        await SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = BrokerProbe.Process(new DelegatePublisher(async (id, type, payload, ct) =>
        {
            publishing.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await broker.Publisher().PublishAsync(id, type, payload, ct);
        }));
        await publishing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await BrokerProbe.MakeEligible();
            Assert.Equal(1, await BrokerProbe.Process(broker.Publisher()));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await first); // Real publish succeeded but stale token cannot confirm it.
        var deliveries = await broker.Drain();
        Assert.Equal(2, deliveries.Length);
        Assert.Single(deliveries.Select(x => x.BasicProperties.MessageId).Distinct());
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task B07_RealBrokerNack_IsPersistedAsFailure()
    {
        await SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var channel = await broker.Connection.CreateChannelAsync(new RabbitMQ.Client.CreateChannelOptions(true, true));
        var queue = broker.Options.QueueName + ".reject";
        await channel.QueueDeclareAsync(queue, true, false, false, new Dictionary<string, object?>
        { ["x-max-length"] = 0, ["x-overflow"] = "reject-publish" });
        RabbitMQ.Client.Exceptions.PublishException? rejection = null;
        try
        {
            var processed = await BrokerProbe.Process(new DelegatePublisher(async (id, type, payload, ct) =>
            {
                try
                {
                    await channel.BasicPublishAsync("", queue, true,
                        new RabbitMQ.Client.BasicProperties { MessageId = id.ToString() },
                        System.Text.Encoding.UTF8.GetBytes(payload), ct);
                }
                catch (RabbitMQ.Client.Exceptions.PublishException ex) { rejection = ex; throw; }
            }));
            Assert.Equal(0, processed); Assert.NotNull(rejection); Assert.False(rejection.IsReturn);
            await using var db = FinancialHarness.Db();
            var stored = await db.OutboxMessages.SingleAsync(x => x.Type == "TransferCompletedEvent");
            Assert.Null(stored.ProcessedAt); Assert.Equal(1, stored.AttemptCount);
        }
        finally { await channel.QueueDeleteAsync(queue); }
    }

    private sealed class DelegatePublisher(Func<Guid, string, string, CancellationToken, Task> action) : IEventPublisher
    {
        public Task PublishAsync(Guid id, string type, string payload, CancellationToken cancellationToken = default)
            => action(id, type, payload, cancellationToken);
    }

    private sealed class FailAfterConfirm(IEventPublisher inner) : IEventPublisher
    {
        public async Task PublishAsync(Guid id, string type, string payload, CancellationToken cancellationToken = default)
        {
            await inner.PublishAsync(id, type, payload, cancellationToken);
            throw new IOException("Injected: response received from broker; database confirmation unavailable");
        }
    }
}
