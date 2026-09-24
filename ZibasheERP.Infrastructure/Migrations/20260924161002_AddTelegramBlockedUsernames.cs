using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramBlockedUsernames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramBlockedUsernames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NormalizedUsername = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BlockedByTelegramUserId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramBlockedUsernames", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramBlockedUsernames_NormalizedUsername",
                table: "TelegramBlockedUsernames",
                column: "NormalizedUsername",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramBlockedUsernames");
        }
    }
}
