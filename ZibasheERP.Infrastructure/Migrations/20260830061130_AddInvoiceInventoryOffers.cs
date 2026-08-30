using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceInventoryOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FixedBottleId",
                table: "SalesLists",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInventoryOffer",
                table: "SalesLists",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceOrderItemId",
                table: "SalesLists",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceSalesListRequestId",
                table: "OrderItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesLists_FixedBottleId",
                table: "SalesLists",
                column: "FixedBottleId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesLists_SourceOrderItemId",
                table: "SalesLists",
                column: "SourceOrderItemId",
                unique: true,
                filter: "[SourceOrderItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_SourceSalesListRequestId",
                table: "OrderItems",
                column: "SourceSalesListRequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_SalesListRequests_SourceSalesListRequestId",
                table: "OrderItems",
                column: "SourceSalesListRequestId",
                principalTable: "SalesListRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesLists_Bottles_FixedBottleId",
                table: "SalesLists",
                column: "FixedBottleId",
                principalTable: "Bottles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_SalesListRequests_SourceSalesListRequestId",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesLists_Bottles_FixedBottleId",
                table: "SalesLists");

            migrationBuilder.DropIndex(
                name: "IX_SalesLists_FixedBottleId",
                table: "SalesLists");

            migrationBuilder.DropIndex(
                name: "IX_SalesLists_SourceOrderItemId",
                table: "SalesLists");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_SourceSalesListRequestId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "FixedBottleId",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "IsInventoryOffer",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "SourceOrderItemId",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "SourceSalesListRequestId",
                table: "OrderItems");
        }
    }
}
