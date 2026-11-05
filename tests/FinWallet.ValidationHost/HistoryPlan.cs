using System.Data;
using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;
using FinWallet.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinWallet.Validation;

public static class HistoryPlan
{
    public static async Task Run()
    {
        await using var db = FinancialHarness.Db();
        await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync();
        Guid selected = default;
        for (var i = 0; i < 100; i++)
        {
            var owner = Guid.NewGuid();
            var wallet = Wallet.Create(owner, "BRL"); db.Wallets.Add(wallet); selected = wallet.Id;
            var customer = LedgerAccount.Create(wallet.Id, AccountType.Customer, "BRL");
            var clearing = LedgerAccount.Create(wallet.Id, AccountType.Clearing, "BRL");
            db.LedgerAccounts.AddRange(customer, clearing);
            var balance = AccountBalance.Create(customer.Id); var compensation = AccountBalance.Create(clearing.Id);
            for (var j = 0; j < 100; j++)
            {
                var tx = LedgerTransaction.Create(owner, $"plan-{j}", "synthetic", TransactionType.Credit, 1, "BRL");
                tx.Post(); db.LedgerTransactions.Add(tx);
                db.LedgerEntries.AddRange(LedgerEntry.Create(tx.Id, customer.Id, EntryType.Credit, 1),
                    LedgerEntry.Create(tx.Id, clearing.Id, EntryType.Debit, 1));
                balance.ApplyCredit(1); compensation.ApplyClearingDebit(clearing, 1);
            }
            db.AccountBalances.AddRange(balance, compensation);
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ANALYZE");
        FinancialHarness.Require(await new HistoricalBalanceReader(db).ReadAsync(selected, DateTimeOffset.UtcNow, default) == 100,
            "Consulta histórica divergente");
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)
            SELECT COALESCE(SUM(CASE WHEN e."Type" = 'Credit' THEN e."Amount" ELSE -e."Amount" END), 0)
            FROM ledger_entries e
            JOIN ledger_accounts a ON e."AccountId" = a."Id"
            JOIN ledger_transactions t ON e."TransactionId" = t."Id"
            WHERE a."WalletId" = @wallet AND a."Type" = 'Customer'
              AND t."Status" = 'Posted' AND t."PostedAt" <= @at
            """;
        command.Parameters.Add(new NpgsqlParameter("wallet", selected));
        command.Parameters.Add(new NpgsqlParameter("at", DateTimeOffset.UtcNow));
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string> { $"UTC: {DateTimeOffset.UtcNow:O}", "100 carteiras, 200 contas, 10000 transações e 20000 partidas sintéticas; ANALYZE executado.", command.CommandText };
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        var path = Path.Combine(ChildProcess.Root, "artifacts/validation/history-plan.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
    }
}
