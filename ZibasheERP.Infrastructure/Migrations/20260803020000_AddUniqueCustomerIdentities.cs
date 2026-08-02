using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    public partial class AddUniqueCustomerIdentities : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [Customers] SET [Username] = LTRIM(RTRIM(CASE WHEN LEFT([Username], 1) = '@' THEN SUBSTRING([Username], 2, 100) ELSE [Username] END)) WHERE [Username] IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "IX_Customers_Mobile",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_TelegramId",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Mobile",
                table: "Customers",
                column: "Mobile",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TelegramId",
                table: "Customers",
                column: "TelegramId",
                unique: true,
                filter: "[TelegramId] IS NOT NULL AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Username",
                table: "Customers",
                column: "Username",
                unique: true,
                filter: "[Username] IS NOT NULL AND [IsDeleted] = 0");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_Mobile",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_TelegramId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_Username",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Mobile",
                table: "Customers",
                column: "Mobile");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TelegramId",
                table: "Customers",
                column: "TelegramId");
        }
    }
}
