using FinWallet.Domain.Entities;

namespace FinWallet.Application.Abstractions.Persistence;

public interface ILedgerTransactionRepository
{
    Task AddAsync(
        LedgerTransaction transaction,
        CancellationToken cancellationToken = default);

    Task<LedgerTransaction?> GetByIdAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default);

    Task<LedgerTransaction?> GetByIdempotencyKeyAsync(
        Guid customerId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
    
    Task AcquireIdempotencyLockAsync(
        Guid customerId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}