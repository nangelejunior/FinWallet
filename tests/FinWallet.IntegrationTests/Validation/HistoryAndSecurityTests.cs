using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinWallet.Application.Security;
using FinWallet.Application.Transfers.TransferWallet;
using FinWallet.Application.Wallets.GetHistoricalBalance;
using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
public sealed class HistoryAndSecurityTests
{
    [Fact]
    public async Task History_UsesInclusivePostingTime_CustomerAccountsAndCommittedSnapshot()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var a = await h.Wallet(owner); var b = await h.Wallet(other);
        await h.Deposit(a, 100, "seed");
        await h.Transfer(new(owner, a, b, 25, "out"));
        await h.Transfer(new(other, b, a, 5, "back"));
        var cut = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
        await using (var db = FinancialHarness.Db())
        {
            foreach (var wallet in await db.Wallets.ToListAsync())
                db.Entry(wallet).Property(x => x.CreatedAt).CurrentValue = cut.AddDays(-1);
            foreach (var tx in await db.LedgerTransactions.ToListAsync())
                db.Entry(tx).Property(x => x.PostedAt).CurrentValue = tx.Type == TransactionType.Credit ? cut.AddSeconds(-1) : cut;
            await db.SaveChangesAsync();
        }
        async Task<HistoricalBalanceResult?> Query(Guid wallet, DateTimeOffset at, Guid customer) => await h.Run(async p =>
        {
            p.GetRequiredService<TestActor>().CustomerId = customer;
            return await p.GetRequiredService<GetHistoricalBalanceQueryHandler>().HandleAsync(wallet, at);
        });
        Assert.Equal(0m, (await Query(a, cut.AddDays(-2), owner))!.PostedBalance);
        Assert.Equal(0m, (await Query(a, cut.AddSeconds(-2), owner))!.PostedBalance);
        Assert.Equal(100m, (await Query(a, cut.AddTicks(-10), owner))!.PostedBalance);
        Assert.Equal(80m, (await Query(a, cut, owner))!.PostedBalance);
        Assert.Equal(80m, (await Query(a, cut.ToOffset(TimeSpan.FromHours(-3)), owner))!.PostedBalance);
        Assert.Equal(20m, (await Query(b, cut, other))!.PostedBalance);
        Assert.Null(await Query(a, cut, other));
        Assert.Null(await Query(Guid.NewGuid(), cut, owner));
        // Pending/rejected records do not contribute even if they have entries and a posting timestamp.
        await using (var db = FinancialHarness.Db())
        {
            var account = await db.LedgerAccounts.SingleAsync(x => x.WalletId == a && x.Type == AccountType.Customer);
            foreach (var status in new[] { TransactionStatus.Pending, TransactionStatus.Rejected })
            {
                var tx = LedgerTransaction.Create(owner, status.ToString(), "test", TransactionType.Credit, 999, "BRL");
                db.LedgerTransactions.Add(tx);
                db.Entry(tx).Property(x => x.Status).CurrentValue = status;
                db.Entry(tx).Property(x => x.PostedAt).CurrentValue = cut;
                db.LedgerEntries.Add(LedgerEntry.Create(tx.Id, account.Id, EntryType.Credit, 999));
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(80m, (await Query(a, cut, owner))!.PostedBalance);
        // An in-flight posting with PostedAt <= cut appears only after commit.
        await using (var db = FinancialHarness.Db())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var account = await db.LedgerAccounts.SingleAsync(x => x.WalletId == a && x.Type == AccountType.Customer);
            var posting = LedgerTransaction.Create(owner, "in-flight", "test", TransactionType.Credit, 7, "BRL");
            posting.Post(); db.LedgerTransactions.Add(posting);
            db.Entry(posting).Property(x => x.PostedAt).CurrentValue = cut;
            db.LedgerEntries.Add(LedgerEntry.Create(posting.Id, account.Id, EntryType.Credit, 7));
            await db.SaveChangesAsync();
            Assert.Equal(80m, (await Query(a, cut, owner))!.PostedBalance);
            await tx.RollbackAsync();
        }
        Assert.Equal(80m, (await Query(a, DateTimeOffset.UtcNow, owner))!.PostedBalance);
        // Remove synthetic incomplete records before checking financial invariants.
        await using (var db = FinancialHarness.Db())
        {
            var excluded = db.LedgerTransactions.Where(x => x.Status != TransactionStatus.Posted);
            await db.LedgerEntries.Where(x => excluded.Select(t => t.Id).Contains(x.TransactionId)).ExecuteDeleteAsync();
            await excluded.ExecuteDeleteAsync();
        }
        await FinancialHarness.Reconcile();
        // Observe a committed, backdated operation on a repeated query.
        var committed = await h.Deposit(a, 7, "committed-later");
        await using (var db = FinancialHarness.Db())
            await db.LedgerTransactions.Where(x => x.Id == committed.TransactionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.PostedAt, cut));
        Assert.Equal(87m, (await Query(a, cut, owner))!.PostedBalance);
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task Http_ValidatesTokens_IsolatesResources_AndRestrictsDeposits()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var port = ChildProcess.FreePort();
        await using var api = new ChildProcess(true, environment: new() { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await api.Ready(client);
        var aOwner = Guid.NewGuid(); var bOwner = Guid.NewGuid();
        using (var absent = await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}")) Assert.Equal(HttpStatusCode.Unauthorized, absent.StatusCode);
        foreach (var token in new[] { "invalid", TestTokens.Issue(aOwner, expired: true),
            TestTokens.Issue(aOwner, issuer: "wrong"), TestTokens.Issue(aOwner, audience: "wrong"),
            TestTokens.Issue(aOwner, includeCustomer: false), TestTokens.Issue(Guid.Empty) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        var valid = TestTokens.Issue(aOwner);
        // Preserve payload and corrupt the signature.
        var parts = valid.Split('.'); parts[2] = (parts[2][0] == 'a' ? "b" : "a") + parts[2][1..];
        client.DefaultRequestHeaders.Authorization = new("Bearer", string.Join('.', parts));
        using (var invalidSignature = await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}"))
            Assert.Equal(HttpStatusCode.Unauthorized, invalidSignature.StatusCode);
        var a = await LoadScenario.Wallet(client, aOwner); var b = await LoadScenario.Wallet(client, bOwner);
        TestTokens.Authenticate(client, aOwner);
        foreach (var suffix in new[] { "", "/balance", "/balance/history?at=2026-01-01T00:00:00Z" })
        {
            using var response = await client.GetAsync($"/api/v1/wallets/{b}{suffix}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        foreach (var at in new[] { "", "?at=bad", "?at=2026-01-01T00:00:00", "?at=2999-01-01T00:00:00Z" })
        {
            using var response = await client.GetAsync($"/api/v1/wallets/{a}/balance/history{at}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var missing = await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}/balance/history?at=2026-01-01T00:00:00Z"))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var forbidden = await client.PostAsJsonAsync($"/api/v1/wallets/{a}/deposits", new { amount = 100, idempotencyKey = "seed" }))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        TestTokens.Authenticate(client, aOwner, technical: true);
        using (var deposit = await client.PostAsJsonAsync($"/api/v1/wallets/{a}/deposits", new { amount = 100, idempotencyKey = "seed" }))
            deposit.EnsureSuccessStatusCode();
        // A different operator replays within the wallet owner's idempotency scope.
        TestTokens.Authenticate(client, bOwner, technical: true);
        using (var replay = await client.PostAsJsonAsync($"/api/v1/wallets/{a}/deposits", new { amount = 100, idempotencyKey = "seed" }))
            Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        TestTokens.Authenticate(client, aOwner);
        using var transfer = await client.PostAsJsonAsync("/api/v1/transfers/", new { sourceWalletId = a, destinationWalletId = b, amount = 10, idempotencyKey = "transfer" });
        transfer.EnsureSuccessStatusCode();
        var id = (await transfer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transactionId").GetGuid();
        TestTokens.Authenticate(client, bOwner);
        using (var hidden = await client.GetAsync($"/api/v1/transfers/{id}")) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using (var stolen = await client.PostAsJsonAsync("/api/v1/transfers/", new { customerId = aOwner, sourceWalletId = a, destinationWalletId = b, amount = 10, idempotencyKey = "transfer" }))
            Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        TestTokens.Authenticate(client, aOwner);
        using (var forbiddenReplay = await client.PostAsJsonAsync($"/api/v1/wallets/{a}/deposits", new { amount = 100, idempotencyKey = "seed" }))
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenReplay.StatusCode);
        using (var history = await client.GetAsync($"/api/v1/wallets/{a}/balance/history?at={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}"))
            Assert.Equal(90m, (await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("postedBalance").GetDecimal());
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task Application_AuthorizesBeforeReplay_WithoutHttp()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var owner = Guid.NewGuid(); var a = await h.Wallet(owner); var b = await h.Wallet(Guid.NewGuid());
        await h.Deposit(a, 100, "seed");
        var command = new TransferWalletCommand(owner, a, b, 10, "transfer");
        await h.Transfer(command);
        await h.Run(async p =>
        {
            p.GetRequiredService<TestActor>().CustomerId = Guid.NewGuid();
            await Assert.ThrowsAsync<KeyNotFoundException>(() => p.GetRequiredService<TransferWalletHandler>().HandleAsync(command));
            p.GetRequiredService<TestActor>().CanDeposit = false;
            await Assert.ThrowsAsync<OperationForbiddenException>(() => p.GetRequiredService<FinWallet.Application.Wallets.DepositWallet.DepositWalletHandler>().HandleAsync(new(a, 100, "seed")));
            return true;
        });
    }
    [Fact]
    public async Task History_IsolatesCurrencies_AndCreationUsesTrustedActor()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var owner = Guid.NewGuid();
        var brl = await h.Wallet(owner);
        var usd = await h.Run(async p =>
        {
            p.GetRequiredService<TestActor>().CustomerId = owner;
            return await p.GetRequiredService<FinWallet.Application.Wallets.CreateWallet.CreateWalletHandler>()
                .HandleAsync(new(Guid.NewGuid(), "USD")); // Body/command identity cannot override the actor.
        });
        await h.Deposit(brl, 100, "brl-seed");
        await h.Deposit(usd, 17, "usd-seed");
        await h.Run(async p =>
        {
            p.GetRequiredService<TestActor>().CustomerId = owner;
            var query = p.GetRequiredService<GetHistoricalBalanceQueryHandler>();
            var at = DateTimeOffset.UtcNow;
            var dollar = await query.HandleAsync(usd, at);
            Assert.Equal("USD", dollar!.Currency);
            Assert.Equal(17m, dollar.PostedBalance);
            Assert.Equal(100m, (await query.HandleAsync(brl, at))!.PostedBalance);
            var current = await p.GetRequiredService<FinWallet.Application.Wallets.GetWalletBalance.GetWalletBalanceQueryHandler>().HandleAsync(usd);
            Assert.Equal(current!.PostedBalance, dollar.PostedBalance);
            var wallet = await p.GetRequiredService<FinWallet.Application.Wallets.GetWallet.GetWalletQueryHandler>().HandleAsync(usd);
            Assert.Equal(owner, wallet!.CustomerId);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                p.GetRequiredService<IHistoricalBalanceReader>().ReadAsync(usd, at, cancelled.Token));
            return true;
        });
        await FinancialHarness.Reconcile();
    }

}
