using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260803010000_AddTelegramProcessedUpdates")]
    public partial class AddTelegramProcessedUpdates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramProcessedUpdates",
                columns: table => new
                {
                    UpdateId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramProcessedUpdates", x => x.UpdateId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramProcessedUpdates_ReceivedAt",
                table: "TelegramProcessedUpdates",
                column: "ReceivedAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TelegramProcessedUpdates");
        }
    }
}
