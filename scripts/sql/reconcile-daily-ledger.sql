\set ON_ERROR_STOP on
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
WITH movements AS (
 SELECT a."WalletId" AS wallet,t."Currency" AS currency,(t."PostedAt" AT TIME ZONE 'UTC')::date AS day,
 coalesce(sum(e."Amount") FILTER(WHERE e."Type"='Credit'),0)::numeric(24,4) credits,
 coalesce(sum(e."Amount") FILTER(WHERE e."Type"='Debit'),0)::numeric(24,4) debits
 FROM ledger_transactions t JOIN ledger_entries e ON e."TransactionId"=t."Id"
 JOIN ledger_accounts a ON a."Id"=e."AccountId"
 WHERE t."Status"='Posted' AND a."Type"='Customer' GROUP BY 1,2,3
)
SELECT
 (SELECT count(*) FROM ledger_transactions WHERE "Status"='Posted'),
 (SELECT coalesce(md5(string_agg("Id"::text,',' ORDER BY "Id")),md5('')) FROM ledger_transactions WHERE "Status"='Posted'),
 (SELECT count(*) FROM movements),
 (SELECT coalesce(md5(string_agg(concat_ws(':',wallet,currency,to_char(day,'YYYY-MM-DD'),credits,debits),',' ORDER BY wallet,currency,day)),md5('')) FROM movements),
 (SELECT count(*) FROM wallets),
 (SELECT coalesce(md5(string_agg(concat_ws(':',"Id","CustomerId","Currency"),',' ORDER BY "Id")),md5('')) FROM wallets);
COMMIT;
