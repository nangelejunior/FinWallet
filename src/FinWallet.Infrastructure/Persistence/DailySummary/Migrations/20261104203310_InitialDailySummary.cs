using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinWallet.Infrastructure.Persistence.DailySummary.Migrations
{
    /// <inheritdoc />
    public partial class InitialDailySummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "summary_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    actor = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    correlation = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("summary_audit_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "summary_days",
                columns: table => new
                {
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    credits = table.Column<decimal>(type: "numeric(24,4)", precision: 24, scale: 4, nullable: false),
                    debits = table.Column<decimal>(type: "numeric(24,4)", precision: 24, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("summary_days_pkey", x => new { x.generation, x.wallet, x.currency, x.day });
                });

            migrationBuilder.CreateTable(
                name: "summary_generations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("summary_generations_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "summary_inbox",
                columns: table => new
                {
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("summary_inbox_pkey", x => new { x.generation, x.origin, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "summary_quarantine",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    payload = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    replayed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("summary_quarantine_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "summary_wallets",
                columns: table => new
                {
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    owner = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("summary_wallets_pkey", x => new { x.generation, x.wallet });
                });

            migrationBuilder.CreateTable(
                name: "summary_state",
                columns: table => new
                {
                    singleton = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("summary_state_pkey", x => x.singleton);
                    table.CheckConstraint("summary_state_singleton_check", "singleton");
                    table.ForeignKey(
                        name: "summary_state_generation_fkey",
                        column: x => x.generation,
                        principalTable: "summary_generations",
                        principalColumn: "id");
                });
            migrationBuilder.Sql("""
                WITH generation AS (INSERT INTO summary_generations(id) VALUES(gen_random_uuid()) RETURNING id)
                INSERT INTO summary_state(singleton,generation) SELECT true,id FROM generation;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "summary_audit");

            migrationBuilder.DropTable(
                name: "summary_days");

            migrationBuilder.DropTable(
                name: "summary_inbox");

            migrationBuilder.DropTable(
                name: "summary_quarantine");

            migrationBuilder.DropTable(
                name: "summary_state");

            migrationBuilder.DropTable(
                name: "summary_wallets");

            migrationBuilder.DropTable(
                name: "summary_generations");
        }
    }
}
