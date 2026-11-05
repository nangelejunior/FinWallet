\set ON_ERROR_STOP on
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
WITH active AS (SELECT generation FROM summary_state),
 inbox AS (SELECT event_id FROM summary_inbox WHERE generation=(SELECT generation FROM active) AND origin='ledger.posted.v1'),
 movements AS (SELECT wallet,currency,day,credits,debits FROM summary_days WHERE generation=(SELECT generation FROM active)),
 wallets AS (SELECT wallet,owner,currency FROM summary_wallets WHERE generation=(SELECT generation FROM active))
SELECT
 (SELECT count(*) FROM inbox),
 (SELECT coalesce(md5(string_agg(event_id::text,',' ORDER BY event_id)),md5('')) FROM inbox),
 (SELECT count(*) FROM movements),
 (SELECT coalesce(md5(string_agg(concat_ws(':',wallet,currency,to_char(day,'YYYY-MM-DD'),credits,debits),',' ORDER BY wallet,currency,day)),md5('')) FROM movements),
 (SELECT count(*) FROM wallets),
 (SELECT coalesce(md5(string_agg(concat_ws(':',wallet,owner,currency),',' ORDER BY wallet)),md5('')) FROM wallets);
COMMIT;
