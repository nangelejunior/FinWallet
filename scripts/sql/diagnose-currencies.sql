-- Read only. Run on the financial database before deploying against persistent data.
BEGIN TRANSACTION READ ONLY;
SELECT 'wallet' AS source, "Id", "Currency", CASE WHEN "Currency" ~ '^[A-Za-z]{3}$' THEN 'case' ELSE 'invalid' END AS issue
FROM wallets WHERE "Currency" !~ '^[A-Z]{3}$';
SELECT 'account' AS source,a."Id",a."Currency",w."Id" AS wallet
FROM ledger_accounts a LEFT JOIN wallets w ON w."Id"=a."WalletId"
WHERE a."Currency" !~ '^[A-Z]{3}$' OR a."Currency"<>w."Currency";
SELECT DISTINCT 'transaction' AS source,t."Id",t."Currency"
FROM ledger_transactions t LEFT JOIN ledger_entries e ON e."TransactionId"=t."Id"
LEFT JOIN ledger_accounts a ON a."Id"=e."AccountId"
WHERE t."Currency" !~ '^[A-Z]{3}$' OR t."Currency"<>a."Currency";
-- JSONPath recursively inspects currency values; deliberately returns IDs only.
SELECT "Id", "Type", "DeadLetteredAt" FROM outbox_messages
WHERE "ProcessedAt" IS NULL AND jsonb_path_exists("Payload"::jsonb,
 '$.**.currency ? (@.type() != "string" || !(@ like_regex "^[A-Z]{3}$"))');
COMMIT;
