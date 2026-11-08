using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryReferencePricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InventoryReferencePricePerMl",
                table: "SalesLists",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Establish a baseline for existing unsold packages without changing
            // their agreed/custom price or any reservation/invoice snapshot.
            migrationBuilder.Sql("""
                UPDATE stock SET InventoryReferencePricePerMl=currentPrice.PricePerMl
                FROM SalesLists stock
                JOIN SalesLists original ON original.Id=stock.InventoryReferenceListId AND original.IsInventoryOffer=0
                CROSS APPLY (
                    SELECT TOP(1) currentList.PricePerMl FROM SalesLists currentList
                    WHERE currentList.IsDeleted=0 AND currentList.IsInventoryOffer=0
                      AND currentList.PerfumeId=original.PerfumeId
                      AND COALESCE(currentList.StablePublicCode,currentList.PublicCode)=COALESCE(original.StablePublicCode,original.PublicCode)
                      AND (currentList.Id=original.Id OR currentList.TelegramMessageId IS NOT NULL)
                    ORDER BY currentList.OpenDate DESC,currentList.CreatedAt DESC,currentList.Id DESC
                ) currentPrice
                WHERE stock.IsDeleted=0 AND stock.IsInventoryOffer=1 AND stock.InventoryCode IS NOT NULL
                  AND stock.Status IN(1,2) AND stock.InventoryReferencePricePerMl IS NULL AND currentPrice.PricePerMl>0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InventoryReferencePricePerMl",
                table: "SalesLists");
        }
    }
}
