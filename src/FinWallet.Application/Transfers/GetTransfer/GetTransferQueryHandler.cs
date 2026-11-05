using FinWallet.Application.Security;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Enums;

namespace FinWallet.Application.Transfers.GetTransfer;

public sealed record TransferDetailsResult(Guid TransactionId, Guid CustomerId,
    string Status, decimal Amount, string Currency, DateTimeOffset CreatedAt,
    DateTimeOffset? PostedAt);

public sealed class GetTransferQueryHandler(ILedgerTransactionRepository transactions, ICurrentActor actor)
{
    public async Task<TransferDetailsResult?> HandleAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        var transaction = await transactions.GetByIdAsync(transactionId, cancellationToken);
        if (transaction is null || transaction.CustomerId != actor.CustomerId || transaction.Type != TransactionType.Transfer) return null;
        return new TransferDetailsResult(transaction.Id, transaction.CustomerId,
            transaction.Status.ToString(), transaction.Amount, transaction.Currency,
            transaction.CreatedAt, transaction.PostedAt);
    }
}
