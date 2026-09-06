using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    [DbContext(typeof(Persistence.AppDbContext))]
    [Migration("20260907090000_AddInvoiceTelegramDocumentMetadata")]
    public partial class AddInvoiceTelegramDocumentMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TelegramInvoiceChatId",
                table: "Invoices",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TelegramInvoiceMessageId",
                table: "Invoices",
                type: "bigint",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TelegramInvoiceChatId", table: "Invoices");
            migrationBuilder.DropColumn(name: "TelegramInvoiceMessageId", table: "Invoices");
        }
    }
}
