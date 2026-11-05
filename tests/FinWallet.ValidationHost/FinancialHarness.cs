using System.Text.Json;
using FinWallet.Application;
using FinWallet.Application.Wallets.CreateWallet;
using FinWallet.Application.Wallets.DepositWallet;
using FinWallet.Application.Transfers.TransferWallet;
using FinWallet.Domain.Enums;
using FinWallet.Infrastructure;
using FinWallet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinWallet.Validation;

public sealed class FinancialHarness : IAsyncDisposable
{
    public ServiceProvider Services { get; }

    public FinancialHarness(string? connection = null, IInterceptor[]? interceptors = null, Action<IServiceCollection>? configure = null)
    {
        ValidationEnvironment.EnsureSafeDatabase(connection);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinWallet"] = connection ?? ValidationEnvironment.ConnectionString
        }).Build();
        var services = new ServiceCollection().AddLogging().AddApplication(configuration).AddInfrastructure(configuration);
        if (interceptors is { Length: > 0 })
            services.AddDbContext<FinWalletDbContext>(options => options.AddInterceptors(interceptors));
        services.AddScoped<TestActor>();
        services.AddScoped<FinWallet.Application.Security.ICurrentActor>(p => p.GetRequiredService<TestActor>());
        configure?.Invoke(services);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public async Task<T> Run<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider).WaitAsync(TimeSpan.FromSeconds(30));
    }

    public Task<Guid> Wallet(Guid customer) => Run(p =>
    {
        p.GetRequiredService<TestActor>().CustomerId = customer;
        return p.GetRequiredService<CreateWalletHandler>().HandleAsync(new(customer, "BRL"));
    });
    public Task<DepositWalletResult> Deposit(Guid wallet, decimal amount, string key) => Run(p =>
        p.GetRequiredService<DepositWalletHandler>().HandleAsync(new(wallet, amount, key)));
    public Task<TransferWalletResult> Transfer(TransferWalletCommand command) => Run(p =>
    {
        p.GetRequiredService<TestActor>().CustomerId = command.CustomerId;
        return p.GetRequiredService<TransferWalletHandler>().HandleAsync(command);
    });

    public static FinWalletDbContext Db(string? connection = null, params IInterceptor[] interceptors)
    {
        ValidationEnvironment.EnsureSafeDatabase(connection);
        return new(new DbContextOptionsBuilder<FinWalletDbContext>()
            .UseNpgsql(connection ?? ValidationEnvironment.ConnectionString).AddInterceptors(interceptors).Options);
    }

    public static async Task Reconcile()
    {
        await using var db = Db();
        var accounts = await db.LedgerAccounts.AsNoTracking().ToListAsync();
        var balances = await db.AccountBalances.AsNoTracking().ToListAsync();
        var transactions = await db.LedgerTransactions.AsNoTracking().ToListAsync();
        var entries = await db.LedgerEntries.AsNoTracking().ToListAsync();
        var outbox = await db.OutboxMessages.AsNoTracking().ToListAsync();
        Require(accounts.Count == balances.Count, "Cada conta deve possuir saldo");
        foreach (var account in accounts)
        {
            var balance = balances.Single(x => x.AccountId == account.Id);
            var postings = entries.Where(x => x.AccountId == account.Id).ToArray();
            var expected = postings.Sum(x => x.Type == EntryType.Credit ? x.Amount : -x.Amount);
            Require(expected == balance.PostedBalance, $"Saldo divergente da conta {account.Id}");
            if (account.Type == AccountType.Customer)
                Require(balance.AvailableBalance >= 0, "Saldo de cliente negativo");
        }
        foreach (var transaction in transactions)
        {
            var postings = entries.Where(x => x.TransactionId == transaction.Id).ToArray();
            Require(transaction.Status == TransactionStatus.Posted && postings.Length >= 2, "Operação parcial");
            Require(postings.Sum(x => x.Type == EntryType.Credit ? x.Amount : 0) == transaction.Amount, "Créditos divergentes");
            Require(postings.Sum(x => x.Type == EntryType.Debit ? x.Amount : 0) == transaction.Amount, "Débitos divergentes");
            Require(postings.All(x => accounts.Single(a => a.Id == x.AccountId).Currency == transaction.Currency), "Moeda divergente");
            var events = outbox.Count(x => JsonDocument.Parse(x.Payload).RootElement.TryGetProperty("transactionId", out var tid) && tid.GetGuid() == transaction.Id);
            Require(events == (transaction.Type == TransactionType.Transfer ? 2 : 1), "Quantidade incorreta de eventos");
        }
        Require(entries.All(x => transactions.Any(t => t.Id == x.TransactionId)), "Partida órfã");
        Require(transactions.GroupBy(x => (x.CustomerId, x.IdempotencyKey)).All(g => g.Count() == 1), "Chave duplicada");
        foreach (var message in outbox)
        {
            var json = JsonDocument.Parse(message.Payload).RootElement;
            if (message.Type == FinWallet.Application.IntegrationEvents.WalletRegistered.Type)
                Require(await db.Wallets.AnyAsync(w => w.Id == json.GetProperty("walletId").GetGuid()), "Evento de carteira órfão");
            else
                Require(transactions.Any(t => t.Id == json.GetProperty("transactionId").GetGuid()), "Evento financeiro órfão");
        }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
