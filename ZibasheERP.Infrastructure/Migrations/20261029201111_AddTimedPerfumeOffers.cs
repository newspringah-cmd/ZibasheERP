using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTimedPerfumeOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FlashSalePromotionId",
                table: "SalesListRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FlashSalePromotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OriginalPricePerMl = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountPercent = table.Column<int>(type: "int", nullable: false),
                    GroupChatId = table.Column<long>(type: "bigint", nullable: false),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlashSalePromotions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlashSalePromotions_SalesLists_SalesListId",
                        column: x => x.SalesListId,
                        principalTable: "SalesLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesListRequests_FlashSalePromotionId",
                table: "SalesListRequests",
                column: "FlashSalePromotionId");

            migrationBuilder.CreateIndex(
                name: "IX_FlashSalePromotions_SalesListId_StartsAtUtc_EndsAtUtc",
                table: "FlashSalePromotions",
                columns: new[] { "SalesListId", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_SalesListRequests_FlashSalePromotions_FlashSalePromotionId",
                table: "SalesListRequests",
                column: "FlashSalePromotionId",
                principalTable: "FlashSalePromotions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesListRequests_FlashSalePromotions_FlashSalePromotionId",
                table: "SalesListRequests");

            migrationBuilder.DropTable(
                name: "FlashSalePromotions");

            migrationBuilder.DropIndex(
                name: "IX_SalesListRequests_FlashSalePromotionId",
                table: "SalesListRequests");

            migrationBuilder.DropColumn(
                name: "FlashSalePromotionId",
                table: "SalesListRequests");
        }
    }
}
