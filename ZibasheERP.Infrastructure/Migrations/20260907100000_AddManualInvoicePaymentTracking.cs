using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    [DbContext(typeof(Persistence.AppDbContext))]
    [Migration("20260907100000_AddManualInvoicePaymentTracking")]
    public partial class AddManualInvoicePaymentTracking : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TelegramPaymentTrackingChatId",
                table: "Invoices",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
            migrationBuilder.AddColumn<long>(
                name: "TelegramPaymentTrackingMessageId",
                table: "Invoices",
                type: "bigint",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TelegramPaymentTrackingChatId", table: "Invoices");
            migrationBuilder.DropColumn(name: "TelegramPaymentTrackingMessageId", table: "Invoices");
        }
    }
}
