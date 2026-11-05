using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "Http")]
public sealed class HttpGuaranteeTests
{
    [Fact]
    public async Task CurrencyAndDuplicateAndReplayHttpContracts()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var port = ChildProcess.FreePort();
        await using var api = new ChildProcess(true, environment: new() { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await api.Ready(client);
        var owner = Guid.NewGuid(); TestTokens.Authenticate(client, owner);
        foreach (var currency in new[] { "123", "B$L", " BR", "BŔL" })
        {
            using var invalid = await client.PostAsJsonAsync("/api/v1/wallets/", new { currency });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        await using var db = FinancialHarness.Db();
        Assert.False(await db.Wallets.AnyAsync()); Assert.False(await db.LedgerAccounts.AnyAsync());
        Assert.False(await db.AccountBalances.AnyAsync()); Assert.False(await db.OutboxMessages.AnyAsync());
        using var created = await client.PostAsJsonAsync("/api/v1/wallets/", new { currency = "bRl" }); created.EnsureSuccessStatusCode();
        foreach (var currency in new[] { "BRL", "brl" })
        {
            using var duplicate = await client.PostAsJsonAsync("/api/v1/wallets/", new { currency });
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
        Assert.Equal("BRL", (await db.Wallets.SingleAsync()).Currency);
        var id = Guid.NewGuid(); var request = new { requestId = Guid.NewGuid(), reason = "broker restored" };
        var path = $"/api/v1/admin/outbox/{id}/replay";
        client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await client.PostAsJsonAsync(path, request); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        TestTokens.Authenticate(client, owner);
        using var forbidden = await client.PostAsJsonAsync(path, request); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(owner, outboxAdmin: true));
        using var missing = await client.PostAsJsonAsync(path, request); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task F04_RealHttpLostResponse_ReplaysWithoutNewEffect_AndRoutesReturnRealData()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var broker = new BrokerProbe(); await broker.Open();
        var port = ChildProcess.FreePort();
        await using var api = new ChildProcess(true, environment: new()
        {
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["RabbitMq__QueueName"] = broker.Options.QueueName,
            ["OutboxWorker__PollingInterval"] = "00:00:00.100"
        });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await api.Ready(client);
        var customer = Guid.NewGuid();
        var a = await LoadScenario.Wallet(client, customer); var b = await LoadScenario.Wallet(client, Guid.NewGuid());
        TestTokens.Authenticate(client, customer, technical: true);
        using var deposit = await client.PostAsJsonAsync($"/api/v1/wallets/{a}/deposits", new { amount = 100, idempotencyKey = "seed" });
        deposit.EnsureSuccessStatusCode();
        TestTokens.Authenticate(client, customer);
        await using var proxy = new FailureProxy("127.0.0.1", port) { DropHttpResponse = true };
        using var unreliable = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{proxy.Port}") };
        TestTokens.Authenticate(unreliable, customer);
        var command = new { sourceWalletId = a, destinationWalletId = b, amount = 10, idempotencyKey = "lost-http" };
        await Assert.ThrowsAsync<HttpRequestException>(() => unreliable.PostAsJsonAsync("/api/v1/transfers/", command));
        await proxy.CutReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var response = await client.PostAsJsonAsync("/api/v1/transfers/", command);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("replayed").GetBoolean());
        var id = result.GetProperty("transactionId").GetGuid();
        using var transfer = await client.GetAsync($"/api/v1/transfers/{id}"); transfer.EnsureSuccessStatusCode();
        using var wallet = await client.GetAsync($"/api/v1/wallets/{a}"); wallet.EnsureSuccessStatusCode();
        var balance = await client.GetFromJsonAsync<JsonElement>($"/api/v1/wallets/{a}/balance");
        Assert.Equal(90, balance.GetProperty("postedBalance").GetDecimal());
        using var missing = await client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}"); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var conflict = await client.PostAsJsonAsync("/api/v1/transfers/", new
        { sourceWalletId = a, destinationWalletId = b, amount = 20, idempotencyKey = "lost-http" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var insufficient = await client.PostAsJsonAsync("/api/v1/transfers/", new
        { sourceWalletId = a, destinationWalletId = b, amount = 1000, idempotencyKey = "insufficient" });
        Assert.Equal(HttpStatusCode.Conflict, insufficient.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/v1/transfers/", new
        { sourceWalletId = a, destinationWalletId = b, amount = .00001m, idempotencyKey = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await FinancialHarness.Reconcile();
        await using var db = FinancialHarness.Db();
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.Type == "TransferCompletedEvent"));
    }
}
