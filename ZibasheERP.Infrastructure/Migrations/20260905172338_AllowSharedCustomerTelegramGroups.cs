using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowSharedCustomerTelegramGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerTelegramGroups_ChatId",
                table: "CustomerTelegramGroups");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTelegramGroups_ChatId",
                table: "CustomerTelegramGroups",
                column: "ChatId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerTelegramGroups_ChatId",
                table: "CustomerTelegramGroups");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTelegramGroups_ChatId",
                table: "CustomerTelegramGroups",
                column: "ChatId",
                unique: true);
        }
    }
}
