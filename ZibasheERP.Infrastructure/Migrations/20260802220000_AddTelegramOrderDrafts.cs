using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260802220000_AddTelegramOrderDrafts")]
public partial class AddTelegramOrderDrafts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExternalReference",
            table: "Orders",
            type: "nvarchar(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "TelegramOrderDrafts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TelegramId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                SalesListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                VolumeMl = table.Column<int>(type: "int", nullable: false),
                BottleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                IsDeleted = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_TelegramOrderDrafts", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_Orders_ExternalReference",
            table: "Orders",
            column: "ExternalReference",
            unique: true,
            filter: "[ExternalReference] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_TelegramOrderDrafts_TelegramId_Status_ExpiresAt",
            table: "TelegramOrderDrafts",
            columns: new[] { "TelegramId", "Status", "ExpiresAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TelegramOrderDrafts");
        migrationBuilder.DropIndex(name: "IX_Orders_ExternalReference", table: "Orders");
        migrationBuilder.DropColumn(name: "ExternalReference", table: "Orders");
    }
}
