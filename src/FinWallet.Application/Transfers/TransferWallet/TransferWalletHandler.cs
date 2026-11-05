using FinWallet.Application.IntegrationEvents;
using Microsoft.Extensions.Logging;
using FinWallet.Application.Security;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;
using FinWallet.Domain.Exceptions;
using FinWallet.Application.Transfers.Events;
using FinWallet.Domain.Services;

namespace FinWallet.Application.Transfers.TransferWallet;

public sealed class TransferWalletHandler(
    IWalletRepository walletRepository,
    ILedgerAccountRepository accountRepository,
    IAccountBalanceRepository balanceRepository,
    ILedgerTransactionRepository transactionRepository,
    ILedgerEntryRepository entryRepository,
    IOutboxRepository outboxRepository,
    IUnitOfWork unitOfWork, ICurrentActor actor, ILogger<TransferWalletHandler> logger)
{
    public async Task<TransferWalletResult> HandleAsync(
        TransferWalletCommand command,
        CancellationToken cancellationToken = default)
    {
        command = command with { CustomerId = actor.CustomerId };
        ValidateCommand(command);

        var requestHash = TransferRequestHasher.Compute(command);

        var result = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var owned = await walletRepository.GetByIdAsync(command.SourceWalletId, ct);
                if (owned is null || owned.CustomerId != actor.CustomerId)
                    throw new KeyNotFoundException("Carteira não encontrada.");

                await transactionRepository.AcquireIdempotencyLockAsync(
                  command.CustomerId,
                  command.IdempotencyKey,
                  ct);

                // 1. Idempotência: verificar se a operação já existe.
                var existing =
                    await transactionRepository.GetByIdempotencyKeyAsync(
                        command.CustomerId,
                        command.IdempotencyKey,
                        ct);

                if (existing is not null)
                {
                    if (!string.Equals(
                        existing.RequestHash,
                        requestHash,
                        StringComparison.Ordinal))
                    {
                        throw new LedgerValidationException(
                            "A chave de idempotência já foi utilizada com outros dados.");
                    }

                    return new TransferWalletResult(
                        existing.Id,
                        existing.Status.ToString(),
                        existing.Amount,
                        existing.Currency,
                        Replayed: true);
                }

                // 2. Carregar carteiras.
                var sourceWallet =
                    await walletRepository.GetByIdAsync(
                        command.SourceWalletId,
                        ct);

                var destinationWallet =
                    await walletRepository.GetByIdAsync(
                        command.DestinationWalletId,
                        ct);

                if (sourceWallet is null || destinationWallet is null)
                {
                    throw new KeyNotFoundException("Carteira não encontrada.");
                }

                if (sourceWallet.CustomerId != command.CustomerId)
                {
                    throw new LedgerValidationException(
                        "O cliente não é proprietário da carteira de origem.");
                }

                if (sourceWallet.Status != WalletStatus.Active ||
                    destinationWallet.Status != WalletStatus.Active)
                {
                    throw new LedgerValidationException(
                        "As duas carteiras precisam estar ativas.");
                }

                if (!string.Equals(
                    sourceWallet.Currency,
                    destinationWallet.Currency,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new LedgerValidationException(
                        "Não é permitido transferir entre moedas diferentes.");
                }

                // 3. Localizar as contas contábeis.
                var sourceAccounts =
                    await accountRepository.GetByWalletIdAsync(
                        sourceWallet.Id,
                        ct);

                var destinationAccounts =
                    await accountRepository.GetByWalletIdAsync(
                        destinationWallet.Id,
                        ct);

                var sourceAccount = sourceAccounts.SingleOrDefault(
                    x => x.Type == AccountType.Customer);

                var destinationAccount = destinationAccounts.SingleOrDefault(
                    x => x.Type == AccountType.Customer);

                if (sourceAccount is null || destinationAccount is null)
                {
                    throw new LedgerValidationException(
                        "Não foi encontrada uma conta de cliente para uma das carteiras.");
                }

                if (sourceAccount.Id == destinationAccount.Id)
                {
                    throw new LedgerValidationException(
                        "A origem e o destino não podem ser a mesma conta.");
                }

                // 4. Bloquear os saldos em ordem determinística.
                var accountIds = new[]
                {
                    sourceAccount.Id,
                    destinationAccount.Id
                };

                var balances = await balanceRepository.GetForUpdateAsync(
                    accountIds,
                    ct);

                var balanceMap = balances.ToDictionary(x => x.AccountId);

                if (!balanceMap.TryGetValue(
                    sourceAccount.Id,
                    out var sourceBalance) ||
                    !balanceMap.TryGetValue(
                    destinationAccount.Id,
                    out var destinationBalance))
                {
                    throw new LedgerValidationException(
                        "Saldo não inicializado para uma das carteiras.");
                }

                // 5. Criar a transação e as partidas.
                var transaction = LedgerTransaction.Create(
                    command.CustomerId,
                    command.IdempotencyKey,
                    requestHash,
                    TransactionType.Transfer,
                    command.Amount,
                    sourceWallet.Currency);

                var debit = LedgerEntry.Create(
                    transaction.Id,
                    sourceAccount.Id,
                    EntryType.Debit,
                    command.Amount);

                var credit = LedgerEntry.Create(
                    transaction.Id,
                    destinationAccount.Id,
                    EntryType.Credit,
                    command.Amount);

                var entries = new[] { debit, credit };

                var accountMap = new Dictionary<Guid, LedgerAccount>
                {
                    [sourceAccount.Id] = sourceAccount,
                    [destinationAccount.Id] = destinationAccount
                };

                // 6. Validar as partidas.
                Domain.Services.LedgerPostingValidator.Validate(
                    transaction,
                    entries,
                    accountMap);

                // 7. Aplicar as mudanças nos saldos.
                sourceBalance.ApplyDebit(command.Amount);
                destinationBalance.ApplyCredit(command.Amount);

                // 8. Publicar a transação.
                transaction.Post();

                await transactionRepository.AddAsync(transaction, ct);
                await entryRepository.AddRangeAsync(entries, ct);
                var posted = new PostedEvent(transaction.Id, transaction.Id,
                    transaction.PostedAt!.Value, [
                        new(sourceWallet.Id, sourceWallet.CustomerId, sourceWallet.Currency, 0, command.Amount),
                        new(destinationWallet.Id, destinationWallet.CustomerId, destinationWallet.Currency, command.Amount, 0)]);
                await outboxRepository.AddAsync(IntegrationEventOutbox.Create(posted), ct);

                var occurredAt = DateTime.UtcNow;

                var transferEvent = new TransferCompletedEvent(
                    EventId: Guid.NewGuid(),
                    TransactionId: transaction.Id,
                    CustomerId: command.CustomerId,
                    SourceWalletId: command.SourceWalletId,
                    DestinationWalletId: command.DestinationWalletId,
                    Amount: command.Amount,
                    Currency: sourceWallet.Currency,
                    OccurredAt: occurredAt
                );

                await outboxRepository.AddAsync(IntegrationEventOutbox.Create(transferEvent), ct);

                return new TransferWalletResult(
                    transaction.Id,
                    transaction.Status.ToString(),
                    transaction.Amount,
                    transaction.Currency,
                    Replayed: false);
            },
            cancellationToken);
        logger.LogInformation("FinancialAudit Actor={Actor} Operation=Transfer Resource={Resource} Destination={Destination} Transaction={Transaction} Replayed={Replayed} Result=Success Correlation={Correlation}",
            actor.Subject, command.SourceWalletId, command.DestinationWalletId, result.TransactionId, result.Replayed, actor.CorrelationId);
        return result;
    }

    private static void ValidateCommand(TransferWalletCommand command)
    {
        if (command.CustomerId == Guid.Empty)
            throw new ArgumentException("CustomerId obrigatório.");

        if (command.SourceWalletId == Guid.Empty)
            throw new ArgumentException("Carteira de origem obrigatória.");

        if (command.DestinationWalletId == Guid.Empty)
            throw new ArgumentException("Carteira de destino obrigatória.");

        if (command.SourceWalletId == command.DestinationWalletId)
            throw new ArgumentException("As carteiras devem ser diferentes.");

        MonetaryRules.ValidateAmount(command.Amount);

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 200)
            throw new ArgumentException("Idempotency key obrigatória.");
    }
}