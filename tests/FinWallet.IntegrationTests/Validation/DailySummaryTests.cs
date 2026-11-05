using FinWallet.Application.DailySummary.Processing;
using FinWallet.Application.DailySummary.GetDailySummary;
using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using FinWallet.Infrastructure.BackgroundServices.DailySummary;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinWallet.Application.IntegrationEvents;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "DailySummary")]
public sealed class DailySummaryTests
{
    [Fact]
    public async Task BatchedProjectionRollsBackInboxAndEarlierPositionOnMetadataConflict()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var summary = new SummaryHarness(); await summary.Reset(); await summary.Rebuild();
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var owner = Guid.NewGuid();
        var seedId = Guid.NewGuid();
        var seed = new PostedEvent(seedId, seedId, DateTimeOffset.UtcNow, [new(second, owner, "BRL", 20, 0)]);
        await summary.Store.Apply(PostedEvent.Type, JsonSerializer.Serialize(seed, DailySummaryRepository.Json));
        var id = Guid.NewGuid();
        var invalid = new PostedEvent(id, id, DateTimeOffset.UtcNow,
            [new(first, owner, "BRL", 10, 0), new(second, Guid.NewGuid(), "BRL", 0, 10)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => summary.Store.Apply(PostedEvent.Type, JsonSerializer.Serialize(invalid, DailySummaryRepository.Json)));
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Null(await summary.Reader.Read(first, owner, day, default));
        Assert.Equal(20, (await summary.Reader.Read(second, owner, day, default))!.ClosingBalance);
        await using var c = await summary.Source.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM summary_inbox WHERE event_id=@id", c);
        cmd.Parameters.AddWithValue("id", id);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MixedCaseFlowAndFinancialReplayConvergeWithoutDuplicateEffects()
    {
        await PostgresFixture.ResetDatabaseAsync(); await using var financial = new FinancialHarness();
        var owner = Guid.NewGuid();
        var wallet = await financial.Run(p =>
        {
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<TestActor>(p).CustomerId = owner;
            return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<FinWallet.Application.Wallets.CreateWallet.CreateWalletHandler>(p)
                .HandleAsync(new(owner, "bRl"));
        });
        var otherOwner = Guid.NewGuid(); var other = await financial.Wallet(otherOwner);
        await financial.Deposit(wallet, 100, "seed"); await financial.Transfer(new(owner, wallet, other, 30, "transfer"));
        await using var db = FinancialHarness.Db();
        Assert.All(await db.Wallets.ToListAsync(), x => Assert.Equal("BRL", x.Currency));
        Assert.All(await db.LedgerAccounts.ToListAsync(), x => Assert.Equal("BRL", x.Currency));
        Assert.All(await db.LedgerTransactions.ToListAsync(), x => Assert.Equal("BRL", x.Currency));
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var proxy = new FailureProxy(broker.Options.HostName, broker.Options.Port) { Offline = true };
        for (var i = 0; i < 5; i++) { await BrokerProbe.MakeEligible(); Assert.Equal(0, await BrokerProbe.Process(broker.Publisher(proxy.Port), maxAttempts: 5)); }
        proxy.Offline = false;
        Assert.Equal(0, await BrokerProbe.Process(broker.Publisher(), maxAttempts: 5));
        var messages = await db.OutboxMessages.AsNoTracking().ToListAsync();
        foreach (var message in messages)
        {
            Assert.NotNull(message.DeadLetteredAt);
            await new FinWallet.Infrastructure.Persistence.Repositories.OutboxRepository(db).ReplayAsync(message.Id, Guid.NewGuid(), "broker restored", "operator", "c");
        }
        Assert.Equal(messages.Count, await BrokerProbe.Process(broker.Publisher(), maxAttempts: 5));
        await using var summary = new SummaryHarness(); await summary.Reset();
        var deliveries = await broker.Drain(); Assert.Equal(messages.Count, deliveries.Length);
        foreach (var delivery in deliveries.Where(x => x.RoutingKey.EndsWith(".daily.v1", StringComparison.Ordinal)))
        {
            var original = messages.Single(x => x.Id.ToString("D") == delivery.BasicProperties.MessageId);
            Assert.Equal(original.Type, delivery.BasicProperties.Type);
            Assert.Equal(original.Payload, System.Text.Encoding.UTF8.GetString(delivery.Body.Span));
            Assert.True(await summary.Store.Apply(original.Type, original.Payload));
            Assert.False(await summary.Store.Apply(original.Type, original.Payload));
        }
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var before = (await summary.Reader.Read(wallet, owner, day, default))!;
        Assert.Equal(100, before.Credits); Assert.Equal(30, before.Debits); Assert.Equal(70, before.ClosingBalance);
        Assert.Equal(30, (await summary.Reader.Read(other, otherOwner, day, default))!.ClosingBalance);
        await summary.Rebuild();
        var after = (await summary.Reader.Read(wallet, owner, day, default))!;
        Assert.Equal(before.Credits, after.Credits); Assert.Equal(before.Debits, after.Debits);
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task InvalidBootstrapPreservesGeneration()
    {
        await PostgresFixture.ResetDatabaseAsync(); await using var financial = new FinancialHarness();
        var owner = Guid.NewGuid(); var wallet = await financial.Wallet(owner);
        await using var summary = new SummaryHarness(); await summary.Reset(); await summary.Rebuild();
        await using var db = FinancialHarness.Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE wallets SET \"Currency\"='123' WHERE \"Id\"={wallet}");
        await Assert.ThrowsAsync<InvalidDataException>(() => summary.Rebuild());
        Assert.Equal("BRL", (await summary.Reader.Read(wallet, owner, DateOnly.FromDateTime(DateTime.UtcNow), default))!.Currency);
    }

    [Fact]
    public async Task ExhaustedTransientDeliveryIsQuarantinedAndReplayCommitsOnce()
    {
        await PostgresFixture.ResetDatabaseAsync();await using var financial=new FinancialHarness();
        var owner=Guid.NewGuid();var a=await financial.Wallet(owner);var targetOwner=Guid.NewGuid();var b=await financial.Wallet(targetOwner);
        await financial.Deposit(a,100,"initial");
        await using var summary=new SummaryHarness();await summary.Reset();await summary.Rebuild();
        await financial.Transfer(new(owner,a,b,30,"transient"));
        await using var db=FinancialHarness.Db();
        var payload=await db.OutboxMessages.Where(x=>x.Type==PostedEvent.Type).OrderByDescending(x=>x.OccurredAt).Select(x=>x.Payload).FirstAsync();
        await using var broker=new BrokerProbe();await broker.Open();var messageId=Guid.NewGuid();
        await broker.Publisher().PublishAsync(messageId,PostedEvent.Type,payload);
        var configuration=new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string,string?>{
                ["RabbitMq:HostName"]=broker.Options.HostName,["RabbitMq:Port"]=broker.Options.Port.ToString(),
                ["RabbitMq:UserName"]=broker.Options.UserName,["RabbitMq:Password"]=broker.Options.Password,
                ["RabbitMq:QueueName"]=broker.Options.QueueName}).Build();
        var failingStore=new DailySummaryRepository(summary.Source,_=>throw new IOException("Injected transient before commit"));
        using var worker=new DailySummaryBackgroundService(new SummaryProcessor(failingStore),configuration,Microsoft.Extensions.Logging.Abstractions.NullLogger<DailySummaryBackgroundService>.Instance);
        await worker.StartAsync(default);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var c=await summary.Source.OpenConnectionAsync(timeout.Token);
        while(true)
        {
            await using var cmd=new NpgsqlCommand("SELECT attempts FROM summary_quarantine WHERE id=@id",c);cmd.Parameters.AddWithValue("id",messageId);
            var attempts=await cmd.ExecuteScalarAsync(timeout.Token);
            if(attempts is int count){Assert.Equal(5,count);break;}
            await Task.Delay(100,timeout.Token);
        }
        await worker.StopAsync(timeout.Token);
        var day=DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(100,(await summary.Reader.Read(a,owner,day,default))!.ClosingBalance);
        Assert.Equal(0,(await summary.Reader.Read(b,targetOwner,day,default))!.ClosingBalance);
        await summary.Store.Replay(messageId,"operator","exhaustion-replay",default);
        Assert.Equal(70,(await summary.Reader.Read(a,owner,day,default))!.ClosingBalance);
        Assert.Equal(30,(await summary.Reader.Read(b,targetOwner,day,default))!.ClosingBalance);
    }

    [Fact]
    public async Task DepositRollbackDoesNotLeakPostedEvent()
    {
        await PostgresFixture.ResetDatabaseAsync();await using var seed=new FinancialHarness();
        var wallet=await seed.Wallet(Guid.NewGuid());
        await using var failing=new FinancialHarness(interceptors:[new RejectCommit()]);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>failing.Deposit(wallet,10,"rollback-deposit"));
        await using var db=FinancialHarness.Db();
        Assert.False(await db.OutboxMessages.AnyAsync(x=>x.Type==PostedEvent.Type));
        Assert.Empty(await db.LedgerTransactions.ToListAsync());
        Assert.Equal(0,await ConcurrencyTests.Balance(wallet));
    }
    private sealed class RejectCommit : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result,CancellationToken cancellationToken=default)
            =>throw new InvalidOperationException("Injected commit failure");
    }

    [Fact]
    public async Task UnifiedApiExposesFinancialAndSummaryRoutesWithRestrictedAdministration()
    {
        await PostgresFixture.ResetDatabaseAsync();await using var summary=new SummaryHarness();await summary.Reset();
        var port=ChildProcess.FreePort();
        var environment=SummaryHarness.EnvironmentFor(SummaryExecutable.AdminApi,port,"unused");
        environment["Logging__LogLevel__Default"]="Information";
        await using var admin=new ChildProcess(false,environment:environment,summaryExecutable:SummaryExecutable.AdminApi);
        using var client=new HttpClient{BaseAddress=new($"http://127.0.0.1:{port}")};await admin.Ready(client,"/health/live");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}/daily-summary")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/summary/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsync("/api/v1/admin/rebuild",null)).StatusCode);
        using(var ordinary=TestTokens.Request(HttpMethod.Post,"/api/v1/admin/rebuild",Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(ordinary)).StatusCode);
        using(var authorized=new HttpRequestMessage(HttpMethod.Post,"/api/v1/admin/rebuild"))
        {
            authorized.Headers.Authorization=new("Bearer",TestTokens.Issue(Guid.NewGuid(),summaryAdmin:true));
            using var response=await client.SendAsync(authorized);response.EnsureSuccessStatusCode();
        }
        using(var auditTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while(!admin.Logs.Contains("Result=403",StringComparison.Ordinal))await Task.Delay(20,auditTimeout.Token);
        await using var c=new NpgsqlConnection(SummaryHarness.RuntimeConnection(SummaryExecutable.Api));await c.OpenAsync();
        await using var forbidden=new NpgsqlCommand("DELETE FROM summary_days",c);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,(await Assert.ThrowsAsync<PostgresException>(()=>forbidden.ExecuteNonQueryAsync())).SqlState);
        await using var ledger=new NpgsqlConnection(SummaryHarness.LedgerReadOnlyConnection);await ledger.OpenAsync();
        await using var financialWrite=new NpgsqlCommand("DELETE FROM ledger_entries",ledger);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,(await Assert.ThrowsAsync<PostgresException>(()=>financialWrite.ExecuteNonQueryAsync())).SqlState);
    }

    [Theory]
    [InlineData("before-commit")]
    [InlineData("after-commit")]
    public async Task KilledConsumerRedeliveryHasNoPartialOrDuplicateEffect(string stage)
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var owner=Guid.NewGuid(); var a=await h.Wallet(owner); var bOwner=Guid.NewGuid(); var b=await h.Wallet(bOwner);
        await h.Deposit(a,100,"seed");
        await using var summary=new SummaryHarness();await summary.Reset();await summary.Rebuild();
        await using var broker=new BrokerProbe();await broker.Open();
        await h.Transfer(new(owner,a,b,30,"kill-test"));
        await using var db=FinancialHarness.Db();
        var payload=await db.OutboxMessages.Where(x=>x.Type==PostedEvent.Type).OrderByDescending(x=>x.OccurredAt).Select(x=>x.Payload).FirstAsync();
        await broker.Publisher().PublishAsync(Guid.NewGuid(),PostedEvent.Type,payload);
        await using(var child=new ChildProcess(false,["summary-checkpoint",stage],new(){["RabbitMq__QueueName"]=broker.Options.QueueName}))
            await child.Checkpoint();
        var day=DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(stage=="before-commit"?100:70,(await summary.Reader.Read(a,owner,day,default))!.ClosingBalance);
        Assert.Equal(stage=="before-commit"?0:30,(await summary.Reader.Read(b,bOwner,day,default))!.ClosingBalance);
        var port=ChildProcess.FreePort();
        await using var consumer=new ChildProcess(false,environment:SummaryHarness.EnvironmentFor(SummaryExecutable.Worker,port,broker.Options.QueueName),summaryExecutable:SummaryExecutable.Worker);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while((await summary.Reader.Read(a,owner,day,timeout.Token))!.ClosingBalance!=70)
            await Task.Delay(50,timeout.Token);
        // Wait for ack, including duplicate fast path in the after-commit case.
        while((await broker.Channel.QueueDeclarePassiveAsync(broker.Options.QueueName+".daily.v1",timeout.Token)).MessageCount>0)
            await Task.Delay(50,timeout.Token);
        Assert.Equal(30,(await summary.Reader.Read(b,bOwner,day,default))!.ClosingBalance);
    }

    [Fact]
    public async Task DuplicateConcurrentReorderedDeliveriesAndLateDaysHaveOneEffect()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var summary = new SummaryHarness(); await summary.Reset(); await summary.Rebuild();
        var wallet = Guid.NewGuid(); var owner = Guid.NewGuid();
        var yesterday = DateTimeOffset.UtcNow.Date.AddDays(-1);
        var early = new PostedEvent(Guid.NewGuid(), Guid.Empty, yesterday, [new(wallet, owner, "BRL", 100, 0)]);
        early = early with { TransactionId = early.EventId };
        var late = new PostedEvent(Guid.NewGuid(), Guid.Empty, yesterday.AddDays(1), [new(wallet, owner, "BRL", 20, 0)]);
        late = late with { TransactionId = late.EventId };
        await summary.Store.Apply(PostedEvent.Type, JsonSerializer.Serialize(late, DailySummaryRepository.Json));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => summary.Store.Apply(PostedEvent.Type, JsonSerializer.Serialize(early, DailySummaryRepository.Json))));
        var today = (await summary.Reader.Read(wallet, owner, DateOnly.FromDateTime(DateTime.UtcNow), default))!;
        Assert.Equal(100, today.OpeningBalance); Assert.Equal(20, today.Credits); Assert.Equal(120, today.ClosingBalance);
        Assert.Null(await summary.Reader.Read(wallet, Guid.NewGuid(), today.Date, default));
        await summary.Store.Quarantine(early.EventId, PostedEvent.Type, JsonSerializer.Serialize(early, DailySummaryRepository.Json), "transient exhausted", 5, default);
        await summary.Store.Replay(early.EventId, "operator", "test", default);
        Assert.Equal(today.ClosingBalance, (await summary.Reader.Read(wallet, owner, today.Date, default))!.ClosingBalance);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => summary.Store.Replay(early.EventId, "operator", "test", default));
    }

    [Fact]
    public async Task BootstrapRepeatedWithLiveEventsConvergesAndPreservesEmptyWallets()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness(); await using var summary = new SummaryHarness(); await summary.Reset();
        var owner = Guid.NewGuid(); var a = await h.Wallet(owner); var bOwner = Guid.NewGuid(); var b = await h.Wallet(bOwner);
        var emptyOwner = Guid.NewGuid(); var empty = await h.Wallet(emptyOwner);
        await h.Deposit(a, 100, "deposit"); await h.Transfer(new(owner, a, b, 30, "transfer"));
        await summary.Rebuild();
        await using var db = FinancialHarness.Db();
        var events = await db.OutboxMessages.Where(x => x.Type != "TransferCompletedEvent").ToArrayAsync();
        // Events already incorporated by snapshot must be harmless; wallet metadata is idempotent too.
        foreach (var e in events.Reverse()) await summary.Store.Apply(e.Type, e.Payload);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var first = (await summary.Reader.Read(a, owner, day, default))!;
        Assert.Equal(100, first.Credits); Assert.Equal(30, first.Debits); Assert.Equal(70, first.ClosingBalance);
        Assert.Equal(30, (await summary.Reader.Read(b, bOwner, day, default))!.ClosingBalance);
        Assert.Equal(0, (await summary.Reader.Read(empty, emptyOwner, day, default))!.ClosingBalance);
        // Writes overlap a rebuild; replay all captured live events after swap to prove the snapshot boundary is safe.
        await Task.WhenAll(summary.Rebuild(), h.Deposit(a, 7, "during-rebuild"));
        var all = await db.OutboxMessages.AsNoTracking().Where(x => x.Type == PostedEvent.Type).ToArrayAsync();
        foreach (var e in all) await summary.Store.Apply(e.Type, e.Payload);
        Assert.Equal(77, (await summary.Reader.Read(a, owner, day, default))!.ClosingBalance);
        await summary.Rebuild();
        Assert.Equal(77, (await summary.Reader.Read(a, owner, day, default))!.ClosingBalance);
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task CombinedWorkerStartsConsumerWhenFinancialDatabaseIsUnavailable()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var summary = new SummaryHarness(); await summary.Reset(); await summary.Rebuild();
        await using var broker = new BrokerProbe(); await broker.Open();
        var port = ChildProcess.FreePort();
        var environment = SummaryHarness.EnvironmentFor(SummaryExecutable.Worker, port, broker.Options.QueueName);
        var unavailable = new NpgsqlConnectionStringBuilder(ValidationEnvironment.ConnectionString)
        { Port = ChildProcess.FreePort(), Timeout = 1, CommandTimeout = 1, Pooling = false };
        environment["ConnectionStrings__FinWallet"] = unavailable.ConnectionString;
        await using var worker = new ChildProcess(false, environment: environment, summaryExecutable: SummaryExecutable.Worker);
        using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}") };
        await worker.Ready(client, "/health/live");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await broker.Channel.QueueDeclarePassiveAsync(broker.Options.QueueName + ".daily.v1", deadline.Token)).ConsumerCount == 0)
            await Task.Delay(50, deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task StoppedWorkerRetainsHttpOperationsAndRestartDrainsOutbox()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var summary = new SummaryHarness(); await summary.Reset(); await summary.Rebuild();
        await using var broker = new BrokerProbe(); await broker.Open();
        var apiPort = ChildProcess.FreePort();
        await using var api = new ChildProcess(false, environment: SummaryHarness.EnvironmentFor(
            SummaryExecutable.Api, apiPort, broker.Options.QueueName), summaryExecutable: SummaryExecutable.Api);
        using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{apiPort}") };
        await api.Ready(client, "/health/live");
        var owner = Guid.NewGuid();
        var wallet = await LoadScenario.Wallet(client, owner);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        async Task Deposit(decimal amount, string key)
        {
            using var request = TestTokens.Request(HttpMethod.Post, $"/api/v1/wallets/{wallet}/deposits", owner,
                new { amount, idempotencyKey = key }, technical: true);
            using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        }
        async Task Drain(decimal expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                await using var db = FinancialHarness.Db();
                if (!await db.OutboxMessages.AnyAsync(x => x.ProcessedAt == null, deadline.Token)
                    && (await summary.Reader.Read(wallet, owner, day, deadline.Token))?.ClosingBalance == expected) return;
                await Task.Delay(50, deadline.Token);
            }
        }
        ChildProcess StartWorker() => new(false, environment: SummaryHarness.EnvironmentFor(
            SummaryExecutable.Worker, ChildProcess.FreePort(), broker.Options.QueueName), summaryExecutable: SummaryExecutable.Worker);
        await Deposit(10, "before-start");
        await using (var db = FinancialHarness.Db())
            Assert.Equal(2, await db.OutboxMessages.CountAsync(x => x.ProcessedAt == null));
        await using (var worker = StartWorker()) await Drain(10);
        await Deposit(7, "while-stopped");
        await using (var db = FinancialHarness.Db())
            Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.ProcessedAt == null));
        Assert.Equal(10, (await summary.Reader.Read(wallet, owner, day, default))!.ClosingBalance);
        await using (var restarted = StartWorker()) await Drain(17);
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task BrokerDeliveryAndAuthorizationWorkAcrossIndependentProcesses()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var summary = new SummaryHarness(); await summary.Reset(); await summary.Rebuild();
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var h = new FinancialHarness();
        var owner = Guid.NewGuid(); var a = await h.Wallet(owner); var bOwner = Guid.NewGuid(); var b = await h.Wallet(bOwner);
        await h.Deposit(a, 100, "seed"); await h.Transfer(new(owner, a, b, 30, "move"));
        // The API must leave these events pending until the independent worker starts.
        var apiPort = ChildProcess.FreePort();
        await using var api = new ChildProcess(false, environment: SummaryHarness.EnvironmentFor(SummaryExecutable.Api, apiPort, broker.Options.QueueName), summaryExecutable: SummaryExecutable.Api);
        using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{apiPort}") };
        await api.Ready(client, "/health/live");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/status")).StatusCode);
        await using (var pending = FinancialHarness.Db())
            Assert.Equal(5, await pending.OutboxMessages.CountAsync(x => x.ProcessedAt == null));
        // No consumer: the read model has no wallet yet.
        var consumerPort = ChildProcess.FreePort();
        await using var consumer = new ChildProcess(false, environment: SummaryHarness.EnvironmentFor(SummaryExecutable.Worker, consumerPort, broker.Options.QueueName), summaryExecutable: SummaryExecutable.Worker);
        using var consumerClient = new HttpClient { BaseAddress = new($"http://127.0.0.1:{consumerPort}") };
        await consumer.Ready(consumerClient, "/health/live");
        Assert.Equal(HttpStatusCode.NotFound, (await consumerClient.GetAsync("/api/v1/admin/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await consumerClient.GetAsync($"/api/v1/wallets/{a}/daily-summary?date={DateTime.UtcNow:yyyy-MM-dd}")).StatusCode);
        await api.Ready(client, "/health/summary/ready");
        await consumer.Ready(consumerClient, "/health/ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        DailySummaryResult? result;
        do
        {
            await Task.Delay(50, timeout.Token);
            result = await summary.Reader.Read(a, owner, DateOnly.FromDateTime(DateTime.UtcNow), timeout.Token);
        } while (result?.ClosingBalance != 70);
        var path = $"/api/v1/wallets/{a}/daily-summary?date={DateTime.UtcNow:yyyy-MM-dd}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        using (var request = TestTokens.Request(HttpMethod.Get, path, Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        using (var request = TestTokens.Request(HttpMethod.Get, path, owner))
        {
            using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
            Assert.Equal(70, (await response.Content.ReadFromJsonAsync<DailySummaryResult>())!.ClosingBalance);
        }
        using (var request = TestTokens.Request(HttpMethod.Get, $"/api/v1/wallets/{a}/daily-summary?date=2099-01-01", owner))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        // Inject invalid input onto the same durable queue; it must be quarantined without blocking valid traffic.
        await broker.Publisher().PublishAsync(Guid.NewGuid(), PostedEvent.Type, "{bad json");
        await h.Deposit(a,3,"after-invalid");
        await using var connection = await summary.Source.OpenConnectionAsync();
        while (true)
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM summary_quarantine", connection);
            if ((long)(await count.ExecuteScalarAsync(timeout.Token))! == 1 &&
                (await summary.Reader.Read(a,owner,DateOnly.FromDateTime(DateTime.UtcNow),timeout.Token))?.ClosingBalance==73) break;
            await Task.Delay(50, timeout.Token);
        }
    }
}
