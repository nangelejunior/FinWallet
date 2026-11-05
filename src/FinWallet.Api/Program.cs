using FinWallet.Application.DailySummary.GetSummaryStatus;
using FinWallet.Infrastructure.Persistence.DailySummary.Schema;
using FinWallet.Application.DailySummary;
using FinWallet.Infrastructure.DailySummary;
using FinWallet.Api.Security;
using FinWallet.Api.Endpoints;
using FinWallet.Api.Middleware;
using FinWallet.Application;
using FinWallet.Infrastructure;

if (args.Contains("--adopt-schema"))
{
    await DailySummarySchemaAdoption.Run(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
FinWallet.Api.Security.DevelopmentAuthentication.Configure(builder);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<FinWallet.Application.Security.ICurrentActor, FinWallet.Api.Security.HttpCurrentActor>();
builder.Services.AddFinancialJwtAuthentication(builder.Configuration);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SummaryAdmin", policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "summary.admin"));
    options.AddPolicy("Deposit", policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "deposit"));
    options.AddPolicy("OutboxAdmin", policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "outbox.admin"));
});

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddDailySummaryAdministration()
    .AddDailySummaryAdminInfrastructure(builder.Configuration);

var app = builder.Build();


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/api/v1/openapi/{documentName}.json");
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    try { await next(context); }
    finally
    {
        if (context.Request.Path.StartsWithSegments("/api/v1"))
            app.Logger.LogInformation("Audit Actor={Actor} Operation={Operation} Resource={Resource} Result={Result} Correlation={Correlation}",
                context.User.FindFirst("sub")?.Value ?? "anonymous", context.Request.Method,
                context.Request.Path.Value, context.Response.StatusCode, context.TraceIdentifier);
    }
});
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseWhen(context => context.Request.Path.Value?.EndsWith("/daily-summary", StringComparison.OrdinalIgnoreCase) == true
    || context.Request.Path.StartsWithSegments("/api/v1/admin/status")
    || context.Request.Path.StartsWithSegments("/api/v1/admin/rebuild")
    || context.Request.Path.StartsWithSegments("/api/v1/admin/quarantine"),
    summary => summary.UseMiddleware<SummaryExceptionHandlingMiddleware>());
app.UseAuthorization();

app.MapPost("/api/v1/admin/outbox/{messageId:guid}/replay", async (Guid messageId,
    FinWallet.Application.Outbox.ReplayOutboxRequest request, FinWallet.Application.Outbox.ReplayOutboxHandler handler, CancellationToken ct) =>
    Results.Accepted(value: await handler.HandleAsync(messageId, request, ct))).RequireAuthorization("OutboxAdmin");
app.MapDailySummaryEndpoints();
app.MapSummaryAdministrationEndpoints();
app.MapGet("/health/summary/ready", async (GetSummaryStatusQueryHandler reader, CancellationToken ct) =>
{
    try { return await reader.IsInitialized(ct) ? Results.Ok() : Results.StatusCode(503); }
    catch (Npgsql.NpgsqlException) { return Results.StatusCode(503); }
});
app.MapWalletEndpoints();
app.MapTransferEndpoints();
app.MapGet("/health", () => Results.Ok(new
{
    Application = "FinWallet",
    Status = "Healthy"
}));

app.MapGet("/health/live", () => Results.Ok());
app.MapGet("/health/ready", async (FinWallet.Infrastructure.Persistence.FinWalletDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok() : Results.StatusCode(503));
app.Run();

public partial class Program;
