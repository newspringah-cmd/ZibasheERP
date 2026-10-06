using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UniqueSalesListChannelPost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Production already received this constraint as a targeted repair.
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                    WHERE object_id=OBJECT_ID(N'SalesLists')
                    AND name=N'IX_SalesLists_TelegramChannelId_TelegramMessageId')
                    CREATE UNIQUE INDEX IX_SalesLists_TelegramChannelId_TelegramMessageId
                    ON SalesLists(TelegramChannelId, TelegramMessageId)
                    WHERE IsDeleted=0 AND TelegramChannelId IS NOT NULL AND TelegramMessageId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SalesLists_TelegramChannelId_TelegramMessageId",
                table: "SalesLists");
        }
    }
}
