using FinWallet.Application.Ledger;
using FinWallet.Application.Outbox;
using FinWallet.Application.Transfers.GetTransfer;
using FinWallet.Application.Transfers.TransferWallet;
using FinWallet.Application.Wallets.CreateWallet;
using FinWallet.Application.Wallets.GetWallet;
using FinWallet.Application.Wallets.GetWalletBalance;
using FinWallet.Application.Wallets.DepositWallet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinWallet.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<FinWallet.Application.Wallets.GetHistoricalBalance.GetHistoricalBalanceQueryHandler>();
        services.AddScoped<CreateWalletHandler>();
        services.AddScoped<GetWalletQueryHandler>();
        services.AddScoped<GetWalletBalanceQueryHandler>();
        services.AddScoped<DepositWalletHandler>();

        services.AddScoped<TransferWalletHandler>();
        services.AddScoped<GetTransferQueryHandler>();

        services.AddScoped<LedgerPostingService>();
        services.AddScoped<ReplayOutboxHandler>();

        return services;
    }

    public static IServiceCollection AddOutboxProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutboxProcessorOptions>(configuration.GetSection("Outbox"));
        services.AddScoped<OutboxProcessor>();
        return services;
    }
}