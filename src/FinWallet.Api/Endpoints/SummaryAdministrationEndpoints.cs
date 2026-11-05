using FinWallet.Application.DailySummary.RebuildDailySummary;
using FinWallet.Application.DailySummary.Processing;
using FinWallet.Application.DailySummary.GetSummaryStatus;

namespace FinWallet.Api.Endpoints;

internal static class SummaryAdministrationEndpoints
{
    internal static void MapSummaryAdministrationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/admin/status", async (GetSummaryStatusQueryHandler query, CancellationToken ct) =>
        {
            var status = await query.HandleAsync(ct);
            return status is null ? Results.StatusCode(503) : Results.Ok(status);
        }).RequireAuthorization("SummaryAdmin");
        app.MapPost("/api/v1/admin/rebuild", async (HttpContext context, RebuildDailySummaryHandler rebuild, CancellationToken ct) =>
        {
            var generation = await rebuild.HandleAsync(context.User.FindFirst("sub")!.Value, context.TraceIdentifier, ct);
            return Results.Ok(new { generation });
        }).RequireAuthorization("SummaryAdmin");
        app.MapPost("/api/v1/admin/quarantine/{id:guid}/replay", async (Guid id, HttpContext context, SummaryProcessor store, CancellationToken ct) =>
        {
            await store.Replay(id, context.User.FindFirst("sub")!.Value, context.TraceIdentifier, ct);
            return Results.Ok(new { id });
        }).RequireAuthorization("SummaryAdmin");
    }
}
