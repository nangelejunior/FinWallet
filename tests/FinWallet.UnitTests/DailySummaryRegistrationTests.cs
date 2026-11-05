using FinWallet.Application.DailySummary.GetSummaryStatus;
using FinWallet.Application.DailySummary.RebuildDailySummary;
using FinWallet.Application.DailySummary.Processing;
using FinWallet.Application.DailySummary.GetDailySummary;
using FinWallet.Infrastructure.BackgroundServices.DailySummary;
using FinWallet.Application.DailySummary;
using FinWallet.Infrastructure.DailySummary;
using FinWallet.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FinWallet.UnitTests;

public sealed class DailySummaryRegistrationTests
{
    [Theory]
    [InlineData("api", false)]
    [InlineData("consumer", true)]
    [InlineData("admin", false)]
    public void SummaryCompositionsResolveWithoutFinancialServices(string mode, bool worker)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DailySummary"] = "Host=localhost;Database=summary",
            ["ConnectionStrings:LedgerReadOnly"] = "Host=localhost;Database=ledger"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        switch (mode)
        {
            case "api": services.AddDailySummaryQueries().AddDailySummaryReadInfrastructure(configuration); break;
            case "consumer": services.AddDailySummaryProcessing().AddDailySummaryWorkerInfrastructure(configuration); break;
            case "admin": services.AddDailySummaryAdministration().AddDailySummaryAdminInfrastructure(configuration); break;
        }
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Equal(mode != "consumer", provider.GetService<GetDailySummaryQueryHandler>() is not null);
        Assert.Equal(mode != "consumer", provider.GetService<GetSummaryStatusQueryHandler>() is not null);
        Assert.Null(provider.GetService<FinWalletDbContext>());
        Assert.Equal(worker ? 1 : 0, provider.GetServices<IHostedService>().Count());
        Assert.Equal(mode == "admin", provider.GetService<RebuildDailySummaryHandler>() is not null);
        Assert.Equal(mode != "api", provider.GetService<SummaryProcessor>() is not null);
    }
    [Fact]
    public void ApiCompositionResolvesReplayWithoutHostedProcessing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinWallet"] = "Host=localhost;Database=ledger",
            ["ConnectionStrings:DailySummary"] = "Host=localhost;Database=summary",
            ["ConnectionStrings:LedgerReadOnly"] = "Host=localhost;Database=ledger"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddScoped<FinWallet.Application.Security.ICurrentActor, RegistrationActor>();
        FinWallet.Application.DependencyInjection.AddApplication(services, configuration);
        FinWallet.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        services.AddDailySummaryAdministration().AddDailySummaryAdminInfrastructure(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Empty(provider.GetServices<IHostedService>());
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FinWallet.Application.Outbox.ReplayOutboxHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FinWallet.Application.Wallets.CreateWallet.CreateWalletHandler>());
        Assert.Null(scope.ServiceProvider.GetService<FinWallet.Application.Outbox.OutboxProcessor>());
    }

    private sealed class RegistrationActor : FinWallet.Application.Security.ICurrentActor
    {
        public Guid CustomerId => Guid.Empty;
        public string Subject => "registration-test";
        public string CorrelationId => "registration-test";
        public bool CanDeposit => false;
    }

    [Fact]
    public async Task CombinedWorkerHasExactlyTwoHostedServicesAndValidScopes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinWallet"] = "Host=localhost;Database=ledger",
            ["ConnectionStrings:DailySummary"] = "Host=localhost;Database=summary",
            ["RabbitMq:Password"] = "validation_only"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging().AddSingleton<IConfiguration>(configuration);
        FinWallet.Application.DependencyInjection.AddOutboxProcessing(services, configuration);
        FinWallet.Infrastructure.DependencyInjection.AddOutboxWorkerInfrastructure(services, configuration);
        services.AddDailySummaryProcessing().AddDailySummaryWorkerInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(2, hosted.Length);
        Assert.Single(hosted.OfType<FinWallet.Infrastructure.BackgroundServices.OutboxBackgroundService>());
        Assert.Single(hosted.OfType<DailySummaryBackgroundService>());
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FinWallet.Application.Outbox.OutboxProcessor>());
        Assert.Null(scope.ServiceProvider.GetService<FinWallet.Application.Wallets.CreateWallet.CreateWalletHandler>());
    }

    [Fact]
    public void AdministrationRequiresLedgerConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DailySummary"] = "Host=localhost;Database=summary"
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDailySummaryAdminInfrastructure(configuration));
        Assert.Contains("LedgerReadOnly", error.Message);
        new ServiceCollection().AddDailySummaryReadInfrastructure(configuration);
        new ServiceCollection().AddDailySummaryWorkerInfrastructure(configuration);
    }
    [Fact]
    public void FinancialCompositionDoesNotRegisterSummaryServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FinWallet"] = "Host=localhost;Database=ledger"
        }).Build();
        var services = new ServiceCollection();
        FinWallet.Application.DependencyInjection.AddApplication(services, configuration);
        FinWallet.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SummaryProcessor)
            || d.ServiceType == typeof(GetSummaryStatusQueryHandler)
            || d.ServiceType == typeof(RebuildDailySummaryHandler)
            || d.ServiceType == typeof(GetDailySummaryQueryHandler) || d.ServiceType == typeof(DailySummaryBackgroundService)
            || d.ServiceType == typeof(Npgsql.NpgsqlDataSource));
    }
}
