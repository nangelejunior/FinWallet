using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using FinWallet.Infrastructure.Persistence.DailySummary.Rebuild;
using FinWallet.Infrastructure.BackgroundServices.DailySummary;
using FinWallet.Application.Abstractions.DailySummary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Npgsql;
namespace FinWallet.Infrastructure.DailySummary;

public static class DependencyInjection
{
    public static IServiceCollection AddDailySummaryReadInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("DailySummary")
            ?? throw new InvalidOperationException("ConnectionStrings:DailySummary obrigatório");
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connection));
        services.TryAddSingleton<ISummaryReader, DailySummaryReader>();
        return services;
    }
    private static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDailySummaryReadInfrastructure(configuration);
        services.TryAddSingleton<DailySummaryRepository>();
        services.TryAddSingleton<ISummaryPersistence>(sp => sp.GetRequiredService<DailySummaryRepository>());
        return services;
    }
    public static IServiceCollection AddDailySummaryWorkerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPersistence(configuration);
        services.TryAddSingleton<DailySummaryBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<DailySummaryBackgroundService>());
        return services;
    }
    public static IServiceCollection AddDailySummaryAdminInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var ledger = configuration.GetConnectionString("LedgerReadOnly")
            ?? throw new InvalidOperationException("ConnectionStrings:LedgerReadOnly obrigatório para a API administrativa");
        services.AddPersistence(configuration);
        services.TryAddSingleton<ISummaryRebuildSource>(sp => new DailySummaryRebuildSource(sp.GetRequiredService<NpgsqlDataSource>(), ledger));
        return services;
    }
}
