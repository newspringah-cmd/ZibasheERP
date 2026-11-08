using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SupportHalfMlReadyInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "TotalVolume",
                table: "SalesLists",
                type: "decimal(18,1)",
                precision: 18,
                scale: 1,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "ReservedVolume",
                table: "SalesLists",
                type: "decimal(18,1)",
                precision: 18,
                scale: 1,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "MinimumRequestVolumeMl",
                table: "SalesLists",
                type: "decimal(18,1)",
                precision: 18,
                scale: 1,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<decimal>(
                name: "VolumeMl",
                table: "SalesListRequests",
                type: "decimal(18,1)",
                precision: 18,
                scale: 1,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently round a real package or customer reservation during rollback.
            migrationBuilder.Sql("IF EXISTS(SELECT 1 FROM SalesLists WHERE TotalVolume%1<>0 OR ReservedVolume%1<>0 OR MinimumRequestVolumeMl%1<>0) OR EXISTS(SELECT 1 FROM SalesListRequests WHERE VolumeMl%1<>0) THROW 51000, 'Fractional inventory exists; schema rollback would lose quantity.', 1;");
            migrationBuilder.AlterColumn<int>(
                name: "TotalVolume",
                table: "SalesLists",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,1)",
                oldPrecision: 18,
                oldScale: 1);

            migrationBuilder.AlterColumn<int>(
                name: "ReservedVolume",
                table: "SalesLists",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,1)",
                oldPrecision: 18,
                oldScale: 1);

            migrationBuilder.AlterColumn<int>(
                name: "MinimumRequestVolumeMl",
                table: "SalesLists",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,1)",
                oldPrecision: 18,
                oldScale: 1);

            migrationBuilder.AlterColumn<int>(
                name: "VolumeMl",
                table: "SalesListRequests",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,1)",
                oldPrecision: 18,
                oldScale: 1);
        }
    }
}
