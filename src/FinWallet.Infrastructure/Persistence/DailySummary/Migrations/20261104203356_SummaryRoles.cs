using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinWallet.Infrastructure.Persistence.DailySummary.Migrations
{
    /// <inheritdoc />
    public partial class SummaryRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
-- Database owner runs migrations. Provision LOGIN identities separately and grant only the appropriate NOLOGIN role.
DO $$ BEGIN
 IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='summary_reader') THEN CREATE ROLE summary_reader NOLOGIN; END IF;
 IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='summary_consumer') THEN CREATE ROLE summary_consumer NOLOGIN; END IF;
 IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='summary_admin') THEN CREATE ROLE summary_admin NOLOGIN; END IF;
END $$;
GRANT USAGE ON SCHEMA public TO summary_reader,summary_consumer,summary_admin;
GRANT SELECT ON summary_state,summary_wallets,summary_days TO summary_reader;
GRANT SELECT,INSERT,UPDATE ON summary_state,summary_wallets,summary_days,summary_inbox,summary_quarantine TO summary_consumer;
GRANT SELECT,INSERT,UPDATE ON summary_generations,summary_state,summary_wallets,summary_days,summary_inbox,summary_quarantine,summary_audit TO summary_admin;
GRANT USAGE ON SEQUENCE summary_audit_id_seq TO summary_admin;
""");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Cluster roles may be used by other databases; retain them on downgrade.

        }
    }
}
