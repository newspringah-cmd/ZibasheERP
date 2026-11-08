using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDirhamPurchaseCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirhamExchangeRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TomanPerDirham = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    EffectiveAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirhamExchangeRates", x => x.Id);
                    table.CheckConstraint("CK_DirhamExchangeRates_Positive", "[TomanPerDirham] > 0");
                });

            migrationBuilder.CreateTable(
                name: "PerfumePurchasePrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AmountAed = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    BottleVolumeMl = table.Column<decimal>(type: "decimal(18,1)", precision: 18, scale: 1, nullable: false),
                    RecordedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfumePurchasePrices", x => x.Id);
                    table.CheckConstraint("CK_PerfumePurchasePrices_Positive", "[AmountAed] > 0 AND [BottleVolumeMl] > 0");
                    table.ForeignKey(
                        name: "FK_PerfumePurchasePrices_SalesLists_SalesListId",
                        column: x => x.SalesListId,
                        principalTable: "SalesLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirhamExchangeRates_EffectiveAtUtc",
                table: "DirhamExchangeRates",
                column: "EffectiveAtUtc",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PerfumePurchasePrices_SalesListId_CreatedAt",
                table: "PerfumePurchasePrices",
                columns: new[] { "SalesListId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PerfumePurchasePrices_VariantKey_CreatedAt",
                table: "PerfumePurchasePrices",
                columns: new[] { "VariantKey", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirhamExchangeRates");

            migrationBuilder.DropTable(
                name: "PerfumePurchasePrices");
        }
    }
}
