using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinWallet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxLockToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LockToken",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LockToken",
                table: "outbox_messages");
        }
    }
}
