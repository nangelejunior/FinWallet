CREATE TABLE IF NOT EXISTS summary_generations(id uuid PRIMARY KEY, created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS summary_state(singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
 generation uuid NOT NULL REFERENCES summary_generations(id), reconciled_at timestamptz, processed_at timestamptz);
CREATE TABLE IF NOT EXISTS summary_wallets(generation uuid NOT NULL, wallet uuid NOT NULL, owner uuid NOT NULL,
 currency varchar(3) NOT NULL, PRIMARY KEY(generation,wallet));
CREATE TABLE IF NOT EXISTS summary_days(generation uuid NOT NULL, wallet uuid NOT NULL, currency varchar(3) NOT NULL,
 day date NOT NULL, credits numeric(24,4) NOT NULL, debits numeric(24,4) NOT NULL,
 PRIMARY KEY(generation,wallet,currency,day));
CREATE TABLE IF NOT EXISTS summary_inbox(generation uuid NOT NULL, origin text NOT NULL, event_id uuid NOT NULL,
 received_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(generation,origin,event_id));
CREATE TABLE IF NOT EXISTS summary_quarantine(id uuid PRIMARY KEY, type text NOT NULL, payload text NOT NULL,
 reason text NOT NULL, attempts integer NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), replayed_at timestamptz);
CREATE TABLE IF NOT EXISTS summary_audit(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, actor text NOT NULL,
 operation text NOT NULL, scope text NOT NULL, correlation text NOT NULL, result text NOT NULL, at timestamptz NOT NULL DEFAULT now());