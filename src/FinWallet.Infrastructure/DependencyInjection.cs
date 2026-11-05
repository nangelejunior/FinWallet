using FinWallet.Application.Abstractions.Messaging;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Application.Abstractions.Randomness;
using FinWallet.Infrastructure.BackgroundServices;
using FinWallet.Infrastructure.Messaging;
using FinWallet.Infrastructure.Persistence;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.Infrastructure.Randomness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinWallet.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddFinancialPersistence(configuration);

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<FinWallet.Application.Wallets.GetHistoricalBalance.IHistoricalBalanceReader, HistoricalBalanceReader>();
        services.AddScoped<IWalletRepository, WalletRepository>();
        services.AddScoped<ILedgerAccountRepository, LedgerAccountRepository>();
        services.AddScoped<IAccountBalanceRepository, AccountBalanceRepository>();
        services.AddScoped<ILedgerTransactionRepository, LedgerTransactionRepository>();
        services.AddScoped<ILedgerEntryRepository, LedgerEntryRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();

        return services;
    }

    public static IServiceCollection AddOutboxWorkerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddFinancialPersistence(configuration);
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddSingleton<IRandomProvider, DefaultRandomProvider>();
        services.AddHostedService<OutboxBackgroundService>();
        services.AddOptions<OutboxWorkerOptions>()
            .Bind(configuration.GetSection("OutboxWorker"))
            .Validate(options => options.PollingInterval > TimeSpan.Zero, "PollingInterval must be greater than zero")
            .ValidateOnStart();
        services
            .AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection("RabbitMq"))
            .Validate(
                x => !string.IsNullOrWhiteSpace(x.HostName)
                    && x.Port is > 0 and <= 65535
                    && !string.IsNullOrWhiteSpace(x.UserName)
                    && !string.IsNullOrWhiteSpace(x.Password)
                    && !string.IsNullOrWhiteSpace(x.QueueName),
                "Invalid RabbitMQ configuration")
            .ValidateOnStart();

        return services;
    }

    private static IServiceCollection AddFinancialPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("FinWallet")
            ?? throw new InvalidOperationException("Connection string 'FinWallet' não configurada.");
        services.AddDbContext<FinWalletDbContext>(options => options.UseNpgsql(connection));
        return services;
    }
}
