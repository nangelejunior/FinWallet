using FinWallet.Application.Security;
using FinWallet.Application.Wallets.CreateWallet;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
namespace FinWallet.IntegrationTests.Validation;
[Collection("PostgreSQL")]
public sealed class WalletCreationTests
{
    [Fact]
    public async Task UniqueIndexRaceRollsBackLosingCreation()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var barrier = new SaveBarrier();
        await using var harness = new FinancialHarness(interceptors: [barrier]);
        var owner = Guid.NewGuid();
        async Task<bool> Create() => await harness.Run(async p =>
        {
            p.GetRequiredService<TestActor>().CustomerId = owner;
            try { await p.GetRequiredService<CreateWalletHandler>().HandleAsync(new(owner,"brl")); return true; }
            catch (OperationConflictException e) { Assert.Equal("wallet_already_exists",e.Code); return false; }
        });
        var results = await Task.WhenAll(Create(),Create());
        Assert.Single(results, x=>x);
        await using var db = FinancialHarness.Db();
        Assert.Equal(1,await db.Wallets.CountAsync()); Assert.Equal(1,await db.LedgerAccounts.CountAsync());
        Assert.Equal(1,await db.AccountBalances.CountAsync()); Assert.Equal(1,await db.OutboxMessages.CountAsync());
    }
    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int arrivals;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrivals)==2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }
}
