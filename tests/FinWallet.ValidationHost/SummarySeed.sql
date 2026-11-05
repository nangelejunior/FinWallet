-- Seed 20261103. 1,000 wallets, 365 UTC days, 500,000 deposits, 1,000,000 double-entry postings.
-- Half the operations concentrate on ten wallets; other half spread across 990 wallets.
CREATE TEMP TABLE seed_wallet AS
SELECT i, md5('wallet-'||i)::uuid w, md5('owner-'||i)::uuid o,
 md5('customer-account-'||i)::uuid a, md5('clearing-account-'||i)::uuid c FROM generate_series(0,999) i;
INSERT INTO wallets("Id","CustomerId","Currency","Status","CreatedAt") SELECT w,o,'BRL','Active',now()-interval '365 days' FROM seed_wallet;
INSERT INTO ledger_accounts("Id","WalletId","Type","Currency","CreatedAt")
 SELECT a,w,'Customer','BRL',now()-interval '365 days' FROM seed_wallet UNION ALL
 SELECT c,w,'Clearing','BRL',now()-interval '365 days' FROM seed_wallet;
CREATE TEMP TABLE seed_transactions AS
 SELECT n,md5('txn-'||n)::uuid t, CASE WHEN n%2=0 THEN n/2%10 ELSE 10+n/2%990 END wi,
 (1+n%10)::numeric amount,
 (date_trunc('day',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC')-((n/1000)%365)*interval '1 day'+interval '1 second' posted
 FROM generate_series(0,499999) n;
INSERT INTO ledger_transactions("Id","CustomerId","IdempotencyKey","RequestHash","Type","Status","Amount","Currency","CreatedAt","PostedAt")
 SELECT t,w.o,'seed-'||n,repeat('0',64),'Credit','Posted',amount,'BRL',posted,posted
 FROM seed_transactions t JOIN seed_wallet w ON w.i=t.wi;
INSERT INTO ledger_entries("Id","TransactionId","AccountId","Type","Amount","CreatedAt")
 SELECT md5('credit-'||n)::uuid,t,a,'Credit',amount,posted FROM seed_transactions t JOIN seed_wallet w ON w.i=t.wi UNION ALL
 SELECT md5('debit-'||n)::uuid,t,c,'Debit',amount,posted FROM seed_transactions t JOIN seed_wallet w ON w.i=t.wi;
INSERT INTO account_balances("AccountId","PostedBalance","ReservedBalance","Version","UpdatedAt")
 SELECT "AccountId",sum(CASE WHEN "Type"='Credit' THEN "Amount" ELSE -"Amount" END),0,0,now()
 FROM ledger_entries GROUP BY "AccountId";
ANALYZE;
