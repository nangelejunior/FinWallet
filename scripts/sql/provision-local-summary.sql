-- Development only. Execute against the local projection database as its owner.
-- Password matches appsettings.Development.json; use managed secrets elsewhere.
DO $$ BEGIN
 IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='summary_local_consumer') THEN
   CREATE ROLE summary_local_consumer LOGIN PASSWORD 'finwallet_daily_dev';
 END IF;
END $$;
GRANT summary_consumer TO summary_local_consumer;
