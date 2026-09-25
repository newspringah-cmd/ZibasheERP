using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStableSalesListPublicCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StablePublicCode",
                table: "SalesLists",
                type: "int",
                nullable: true);

            // Backfill list cycles that were already created by the automatic rollover.
            // ExternalReference stores the previous list id as auto-next:{id:N}:...
            migrationBuilder.Sql(
                """
                DECLARE @updatedRows int = 1;
                WHILE @updatedRows > 0
                BEGIN
                    UPDATE nextList
                    SET StablePublicCode = COALESCE(previousList.StablePublicCode, previousList.PublicCode)
                    FROM SalesLists AS nextList
                    INNER JOIN SalesListRequests AS request
                        ON request.SalesListId = nextList.Id
                        AND request.IsDeleted = 0
                    INNER JOIN SalesLists AS previousList
                        ON LOWER(REPLACE(CONVERT(varchar(36), previousList.Id), '-', '')) =
                           SUBSTRING(request.ExternalReference, 11, 32)
                    WHERE request.ExternalReference LIKE 'auto-next:%'
                      AND (nextList.StablePublicCode IS NULL OR
                           nextList.StablePublicCode <> COALESCE(previousList.StablePublicCode, previousList.PublicCode));

                    SET @updatedRows = @@ROWCOUNT;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StablePublicCode",
                table: "SalesLists");
        }
    }
}
