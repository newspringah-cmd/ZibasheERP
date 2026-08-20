using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesListGiftAndBottleOwnerRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GiftRecipientTelegramUserId",
                table: "SalesListRequests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GiftRecipientTelegramUsername",
                table: "SalesListRequests",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBottleOwner",
                table: "SalesListRequests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsGift",
                table: "SalesListRequests",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GiftRecipientTelegramUserId",
                table: "SalesListRequests");

            migrationBuilder.DropColumn(
                name: "GiftRecipientTelegramUsername",
                table: "SalesListRequests");

            migrationBuilder.DropColumn(
                name: "IsBottleOwner",
                table: "SalesListRequests");

            migrationBuilder.DropColumn(
                name: "IsGift",
                table: "SalesListRequests");
        }
    }
}
