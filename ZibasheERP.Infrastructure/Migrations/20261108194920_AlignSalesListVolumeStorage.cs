using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlignSalesListVolumeStorage : Migration
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
            throw new NotSupportedException("Reverting sales-list volume columns to integers would truncate existing fractional values.");
        }
    }
}
