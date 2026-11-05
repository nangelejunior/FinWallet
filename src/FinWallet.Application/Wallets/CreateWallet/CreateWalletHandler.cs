using FinWallet.Application.IntegrationEvents;
using Microsoft.Extensions.Logging;
using FinWallet.Application.Security;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;

namespace FinWallet.Application.Wallets.CreateWallet;

public sealed class CreateWalletHandler(
    IWalletRepository walletRepository,
    ILedgerAccountRepository ledgerAccountRepository,
    IAccountBalanceRepository accountBalanceRepository,
    IOutboxRepository outboxRepository, IUnitOfWork unitOfWork, ICurrentActor actor, ILogger<CreateWalletHandler> logger)
{
    public async Task<Guid> HandleAsync(
        CreateWalletCommand command,
        CancellationToken cancellationToken = default)
    {
        command = command with { CustomerId = actor.CustomerId, Currency = FinWallet.Domain.Common.CurrencyCode.Normalize(command.Currency) };
        var result = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var existing =
                    await walletRepository.GetByCustomerAndCurrencyAsync(
                        command.CustomerId,
                        command.Currency,
                        ct);

                if (existing is not null)
                {
                    throw new FinWallet.Application.Security.OperationConflictException("wallet_already_exists",
                        "O cliente já possui uma carteira nesta moeda.");
                }

                var wallet = Wallet.Create(
                    command.CustomerId,
                    command.Currency);

                var account = LedgerAccount.Create(
                    wallet.Id,
                    AccountType.Customer,
                    command.Currency);

                var balance = AccountBalance.Create(account.Id);

                await walletRepository.AddAsync(wallet, ct);
                await ledgerAccountRepository.AddAsync(account, ct);
                await accountBalanceRepository.AddAsync(balance, ct);
                var registered = new WalletRegistered(wallet.Id, wallet.Id, wallet.CustomerId, wallet.Currency);
                await outboxRepository.AddAsync(IntegrationEventOutbox.Create(registered, DateTime.UtcNow), ct);

                return wallet.Id;
            },
            cancellationToken);
        logger.LogInformation("FinancialAudit Actor={Actor} Operation=CreateWallet Resource={Resource} Result=Success Correlation={Correlation}",
            actor.Subject, result, actor.CorrelationId);
        return result;
    }
}