using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillInventoryOrdersReadyToShip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE [item]
                SET [item].[FulfillmentStatus] = 9,
                    [item].[DecantedAt] = COALESCE(
                        [item].[DecantedAt],
                        [orders].[InvoiceIssuedAt],
                        [item].[CreatedAt]),
                    [item].[UpdatedAt] = SYSUTCDATETIME()
                FROM [OrderItems] AS [item]
                INNER JOIN [Orders] AS [orders] ON [orders].[Id] = [item].[OrderId]
                WHERE [item].[IsDeleted] = 0
                  AND [orders].[IsDeleted] = 0
                  AND [orders].[IsInventory] = 1
                  AND [item].[FulfillmentStatus] = 6;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
