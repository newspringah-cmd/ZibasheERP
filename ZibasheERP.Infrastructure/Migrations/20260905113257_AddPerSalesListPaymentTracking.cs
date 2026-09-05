using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPerSalesListPaymentTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TelegramPaymentTrackingChatId",
                table: "InvoiceIssuanceBatchSalesLists",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TelegramPaymentTrackingMessageId",
                table: "InvoiceIssuanceBatchSalesLists",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TelegramPaymentTrackingChatId",
                table: "InvoiceIssuanceBatchSalesLists");

            migrationBuilder.DropColumn(
                name: "TelegramPaymentTrackingMessageId",
                table: "InvoiceIssuanceBatchSalesLists");
        }
    }
}
