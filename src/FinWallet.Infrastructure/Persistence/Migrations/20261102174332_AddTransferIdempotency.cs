using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinWallet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ledger_transactions_IdempotencyKey",
                table: "ledger_transactions");

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "ledger_transactions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "ledger_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_transactions_CustomerId_IdempotencyKey",
                table: "ledger_transactions",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ledger_transactions_CustomerId_IdempotencyKey",
                table: "ledger_transactions");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "ledger_transactions");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "ledger_transactions");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_transactions_IdempotencyKey",
                table: "ledger_transactions",
                column: "IdempotencyKey",
                unique: true);
        }
    }
}
