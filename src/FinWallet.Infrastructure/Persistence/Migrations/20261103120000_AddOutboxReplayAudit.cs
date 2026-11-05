using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace FinWallet.Infrastructure.Persistence.Migrations;
[DbContext(typeof(FinWalletDbContext))]
[Migration("20261103120000_AddOutboxReplayAudit")]
public sealed class AddOutboxReplayAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE outbox_replay_audit (
          request_id uuid PRIMARY KEY, message_id uuid NOT NULL REFERENCES outbox_messages("Id"),
          actor text NOT NULL, reason varchar(500) NOT NULL, correlation text NOT NULL,
          occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(), attempt_count integer NOT NULL,
          last_error text, dead_lettered_at timestamptz NOT NULL, next_attempt_at timestamptz, locked_until timestamptz, lock_token uuid);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE outbox_replay_audit");
}
