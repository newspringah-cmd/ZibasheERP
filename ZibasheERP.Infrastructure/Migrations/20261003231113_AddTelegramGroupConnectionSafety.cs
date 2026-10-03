using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramGroupConnectionSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerTelegramGroups_ChatId",
                table: "CustomerTelegramGroups");

            migrationBuilder.AddColumn<bool>(
                name: "IsPrimaryForShipping",
                table: "CustomerTelegramGroups",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RestoreOnBotRejoin",
                table: "CustomerTelegramGroups",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Existing healthy one-customer groups can be marked safely. Ambiguous
            // shared groups deliberately remain without a primary until an operator
            // chooses one from /ad.
            migrationBuilder.Sql("""
                UPDATE telegram_group
                SET telegram_group.IsPrimaryForShipping = 1
                FROM CustomerTelegramGroups AS telegram_group
                INNER JOIN
                (
                    SELECT ChatId
                    FROM CustomerTelegramGroups
                    WHERE IsDeleted = 0 AND IsActive = 1
                    GROUP BY ChatId
                    HAVING COUNT(*) = 1
                ) AS single_link ON single_link.ChatId = telegram_group.ChatId
                WHERE telegram_group.IsDeleted = 0 AND telegram_group.IsActive = 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTelegramGroups_ChatId_IsPrimaryForShipping",
                table: "CustomerTelegramGroups",
                columns: new[] { "ChatId", "IsPrimaryForShipping" },
                unique: true,
                filter: "[IsPrimaryForShipping] = 1 AND [IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerTelegramGroups_ChatId_IsPrimaryForShipping",
                table: "CustomerTelegramGroups");

            migrationBuilder.DropColumn(
                name: "IsPrimaryForShipping",
                table: "CustomerTelegramGroups");

            migrationBuilder.DropColumn(
                name: "RestoreOnBotRejoin",
                table: "CustomerTelegramGroups");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTelegramGroups_ChatId",
                table: "CustomerTelegramGroups",
                column: "ChatId");
        }
    }
}
