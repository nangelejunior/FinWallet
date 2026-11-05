using FinWallet.Application.DailySummary.RebuildDailySummary;
using FinWallet.Application.DailySummary.Processing;
using FinWallet.Application.DailySummary.GetSummaryStatus;
using FinWallet.Application.DailySummary.GetDailySummary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace FinWallet.Application.DailySummary;

public static class DependencyInjection
{
    public static IServiceCollection AddDailySummaryQueries(this IServiceCollection services)
    {
        services.TryAddSingleton<GetDailySummaryQueryHandler>();
        services.TryAddSingleton<GetSummaryStatusQueryHandler>();
        return services;
    }
    public static IServiceCollection AddDailySummaryProcessing(this IServiceCollection services)
    {
        services.TryAddSingleton<SummaryProcessor>();
        return services;
    }
    public static IServiceCollection AddDailySummaryAdministration(this IServiceCollection services)
    {
        services.AddDailySummaryQueries().AddDailySummaryProcessing();
        services.TryAddSingleton<RebuildDailySummaryHandler>();
        return services;
    }
}
