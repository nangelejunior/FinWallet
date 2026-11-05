using FinWallet.Api.Contracts;
using FinWallet.Application.Transfers.GetTransfer;
using FinWallet.Application.Transfers.TransferWallet;

namespace FinWallet.Api.Endpoints;

public static class TransferEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/transfers")
            .RequireAuthorization()
            .WithTags("Transfers");

        group.MapPost("/", Transfer)
            .WithName("TransferWallet");

        group.MapGet("/{transactionId:guid}", GetTransfer)
            .WithName("GetTransfer");

        return app;
    }

    private static async Task<IResult> GetTransfer(
        Guid transactionId,
        GetTransferQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty)
            return Results.BadRequest(new { Error = "Invalid transaction identifier." });
        var result = await handler.HandleAsync(transactionId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Transfer(TransferRequest request,
        TransferWalletHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.SourceWalletId == Guid.Empty ||
            request.DestinationWalletId == Guid.Empty)
            return Results.BadRequest(
                new { Error = "Invalid wallet identifier." });

        if (request.SourceWalletId == request.DestinationWalletId)
            return Results.BadRequest(
                new { Error = "Source and destination must differ." });

        if (request.Amount <= 0)
            return Results.BadRequest(
                new { Error = "Amount must be positive." });

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Results.BadRequest(
                new { Error = "IdempotencyKey is required." });

        var command = new TransferWalletCommand(
            Guid.Empty,
            request.SourceWalletId,
            request.DestinationWalletId,
            request.Amount,
            request.IdempotencyKey);

        var result = await handler.HandleAsync(command, cancellationToken);

        return Results.Ok(result);
    }
}