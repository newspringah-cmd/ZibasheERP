using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTrackingPhotoMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TelegramPaymentTrackingPhotoMessageId",
                table: "InvoiceIssuanceBatchSalesLists",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TelegramPaymentTrackingPhotoMessageId",
                table: "InvoiceIssuanceBatchSalesLists");
        }
    }
}
