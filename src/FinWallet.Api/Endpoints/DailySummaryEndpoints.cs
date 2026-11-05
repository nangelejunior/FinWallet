using FinWallet.Application.DailySummary.GetSummaryStatus;
using FinWallet.Application.DailySummary.GetDailySummary;

namespace FinWallet.Api.Endpoints;

internal static class DailySummaryEndpoints
{
    internal static void MapDailySummaryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/wallets/{walletId:guid}/daily-summary", async (Guid walletId, string? date,
            HttpContext context, GetDailySummaryQueryHandler store, GetSummaryStatusQueryHandler status, CancellationToken ct) =>
        {
            if (!GetDailySummaryQueryHandler.TryParseDate(date, out var day))
                return Results.BadRequest(new { error = "date deve ser YYYY-MM-DD, sem data futura (UTC)." });
            var result = await store.HandleAsync(walletId, Guid.Parse(context.User.FindFirst("customer_id")!.Value), day, ct);
            if (result is null)
            {
                if (!await status.IsInitialized(ct))
                    return Results.Json(new { status = "initializing" }, statusCode: 503);
                // Missing replica cannot safely distinguish an unknown wallet from one awaiting delivery.
                // A completed bootstrap establishes the wallet set as of its snapshot; 404 is still eventually consistent.
                return Results.NotFound(new { error = "Carteira ausente na projeção ou não pertence à identidade.", consistency = "eventual" });
            }
            if (result.ReconciledAt is null)
                return Results.Json(new { status = "initializing", result.ProcessedAt }, statusCode: 503);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result);
        }).RequireAuthorization();
    }
}
