using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderInventoryFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsInventory",
                table: "Orders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Preserve the inventory marker for manual inventory invoices that
            // were issued before the flag was stored directly on the order.
            migrationBuilder.Sql("""
                UPDATE [o]
                SET [o].[IsInventory] = 1
                FROM [Orders] AS [o]
                WHERE EXISTS
                (
                    SELECT 1
                    FROM [NotificationOutbox] AS [n]
                    WHERE [n].[OrderId] = [o].[Id]
                      AND ISJSON([n].[Payload]) = 1
                      AND JSON_VALUE([n].[Payload], '$.IsInventory') = 'true'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsInventory",
                table: "Orders");
        }
    }
}
