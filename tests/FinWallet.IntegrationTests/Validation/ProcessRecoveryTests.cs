using FinWallet.Application.Transfers.TransferWallet;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "Recovery")]
public sealed class ProcessRecoveryTests
{
    [Fact]
    public async Task F03_KilledProcessAfterSaveBeforeCommit_LeavesNoPartialOperation()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await h.Wallet(customer); var b = await h.Wallet(Guid.NewGuid());
        await h.Deposit(a, 100, "seed");
        await using (var process = new ChildProcess(false, ["before-commit", customer.ToString(), a.ToString(), b.ToString(), "crashed"]))
            await process.Checkpoint(); // Dispose kills the actual child, holding an uncommitted transfer.
        var result = await h.Transfer(new(customer, a, b, 10, "crashed"));
        Assert.False(result.Replayed);
        Assert.Equal(90, await ConcurrencyTests.Balance(a));
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task F05_PostgresCommitReplyLost_ResolvesBySameKey()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var seed = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await seed.Wallet(customer); var b = await seed.Wallet(Guid.NewGuid());
        await seed.Deposit(a, 100, "seed");
        var connection = new NpgsqlConnectionStringBuilder(ValidationEnvironment.ConnectionString);
        await using var proxy = new FailureProxy(connection.Host!, connection.Port) { DropCommitResponse = true };
        connection.Port = proxy.Port; connection.Host = "127.0.0.1"; connection.Pooling = false; connection.SslMode = SslMode.Disable;
        await using var faulty = new FinancialHarness(connection.ConnectionString);
        var command = new TransferWalletCommand(customer, a, b, 10, "uncertain");
        await Assert.ThrowsAnyAsync<Exception>(() => faulty.Transfer(command));
        await proxy.CutReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // COMMIT CommandComplete was received by the proxy, never by Npgsql.
        var replay = await seed.Transfer(command);
        Assert.True(replay.Replayed);
        Assert.Equal(90, await ConcurrencyTests.Balance(a));
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task O04_KilledWorker_LeaseIsRecoveredByAnotherInstance()
    {
        await BrokerRecoveryTests.SeedTransfer();
        await using (var process = new ChildProcess(false, ["reserve"])) await process.Checkpoint();
        await using var broker = new BrokerProbe(); await broker.Open();
        Assert.Equal(0, await BrokerProbe.Process(broker.Publisher())); // Lease still belongs to dead worker.
        await BrokerProbe.MakeEligible(); // Explicitly advance lease data; no fragile timer sleep.
        Assert.Equal(1, await BrokerProbe.Process(broker.Publisher()));
        Assert.Single(await broker.Drain());
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task B06_KilledWorkerAfterConfirm_RedeliversSameMessage()
    {
        await BrokerRecoveryTests.SeedTransfer();
        await using var broker = new BrokerProbe(); await broker.Open();
        await using (var process = new ChildProcess(false, ["publish"], new()
        { ["FINWALLET_TEST_QUEUE"] = broker.Options.QueueName })) await process.Checkpoint();
        var first = Assert.Single(await broker.Drain());
        await BrokerProbe.MakeEligible();
        Assert.Equal(1, await BrokerProbe.Process(broker.Publisher()));
        var second = Assert.Single(await broker.Drain());
        Assert.Equal(first.BasicProperties.MessageId, second.BasicProperties.MessageId);
        await FinancialHarness.Reconcile();
    }
}
