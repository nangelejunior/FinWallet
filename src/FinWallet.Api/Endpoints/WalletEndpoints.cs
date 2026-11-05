using FinWallet.Api.Contracts;
using FinWallet.Application.Wallets.CreateWallet;
using FinWallet.Application.Wallets.GetWallet;
using FinWallet.Application.Wallets.GetWalletBalance;
using FinWallet.Application.Wallets.DepositWallet;

namespace FinWallet.Api.Endpoints;

public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/wallets")
            .RequireAuthorization()
            .WithTags("Wallets");

        group.MapPost("/", CreateWallet)
            .WithName("CreateWallet");

        group.MapGet("/{walletId:guid}", GetWallet)
            .WithName("GetWallet");

        group.MapGet("/{walletId:guid}/balance", GetBalance)
            .WithName("GetWalletBalance");

        group.MapGet("/{walletId:guid}/balance/history", GetHistory);

        group.MapPost("/{walletId:guid}/deposits", Deposit)
            .RequireAuthorization("Deposit")
            .WithName("DepositToWallet");

        return app;
    }

    private static async Task<IResult> CreateWallet(
        CreateWalletRequest request,
        CreateWalletHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            return Results.BadRequest(new
            {
                Error = "Currency is required."
            });
        }

        var command = new CreateWalletCommand(
            Guid.Empty,
            request.Currency);

        var result = await handler.HandleAsync(command, cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetWallet(
        Guid walletId,
        GetWalletQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (walletId == Guid.Empty)
        {
            return Results.BadRequest(new
            {
                Error = "Invalid wallet identifier."
            });
        }

        var result = await handler.HandleAsync(walletId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetBalance(
        Guid walletId,
        GetWalletBalanceQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (walletId == Guid.Empty)
        {
            return Results.BadRequest(new
            {
                Error = "Invalid wallet identifier."
            });
        }

        var result = await handler.HandleAsync(walletId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetHistory(Guid walletId, string? at,
        FinWallet.Application.Wallets.GetHistoricalBalance.GetHistoricalBalanceQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (at is null || !System.Text.RegularExpressions.Regex.IsMatch(at,
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") ||
            !DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var instant))
            return Results.BadRequest(new { Error = "at deve ser ISO-8601 com offset." });
        var result = await handler.HandleAsync(walletId, instant, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Deposit(
        Guid walletId,
        DepositRequest request,
        DepositWalletHandler handler,
        CancellationToken cancellationToken)
    {
        if (walletId == Guid.Empty)
        {
            return Results.BadRequest(new
            {
                Error = "Invalid wallet identifier."
            });
        }

        if (request.Amount <= 0)
        {
            return Results.BadRequest(new
            {
                Error = "Amount must be greater than zero."
            });
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Results.BadRequest(new
            {
                Error = "IdempotencyKey is required."
            });
        }

        var result = await handler.HandleAsync(
            new DepositWalletCommand(walletId, request.Amount, request.IdempotencyKey), cancellationToken);
        return Results.Ok(result);
    }
}
