using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260803040000_AddUniqueDefaultCustomerAddress")]
    public partial class AddUniqueDefaultCustomerAddress : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "WITH Ranked AS (SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [CustomerId] ORDER BY [CreatedAt], [Id]) AS [RowNumber] FROM [Addresses] WHERE [IsDefault] = 1 AND [IsDeleted] = 0) UPDATE [Addresses] SET [IsDefault] = 0 WHERE [Id] IN (SELECT [Id] FROM Ranked WHERE [RowNumber] > 1);");

            migrationBuilder.DropIndex(
                name: "IX_Addresses_CustomerId",
                table: "Addresses");

            migrationBuilder.CreateIndex(
                name: "IX_Addresses_CustomerId_IsDefault",
                table: "Addresses",
                columns: new[] { "CustomerId", "IsDefault" },
                unique: true,
                filter: "[IsDefault] = 1 AND [IsDeleted] = 0");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Addresses_CustomerId_IsDefault",
                table: "Addresses");

            migrationBuilder.CreateIndex(
                name: "IX_Addresses_CustomerId",
                table: "Addresses",
                column: "CustomerId");
        }
    }
}
