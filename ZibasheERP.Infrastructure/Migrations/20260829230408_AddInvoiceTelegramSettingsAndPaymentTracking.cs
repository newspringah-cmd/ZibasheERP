using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceTelegramSettingsAndPaymentTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TelegramPaymentTrackingChatId",
                table: "InvoiceIssuanceBatches",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TelegramPaymentTrackingMessageId",
                table: "InvoiceIssuanceBatches",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvoiceTelegramSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GreetingStickerFileId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedByTelegramUserId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceTelegramSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceTelegramSettings");

            migrationBuilder.DropColumn(
                name: "TelegramPaymentTrackingChatId",
                table: "InvoiceIssuanceBatches");

            migrationBuilder.DropColumn(
                name: "TelegramPaymentTrackingMessageId",
                table: "InvoiceIssuanceBatches");
        }
    }
}
