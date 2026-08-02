using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedDataUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UsesOriginalBottle",
                table: "OrderItems");

            migrationBuilder.AddColumn<Guid>(
                name: "SalesListId",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SalesListId",
                table: "Orders",
                column: "SalesListId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_SalesLists_SalesListId",
                table: "Orders",
                column: "SalesListId",
                principalTable: "SalesLists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_SalesLists_SalesListId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_SalesListId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SalesListId",
                table: "Orders");

            migrationBuilder.AddColumn<bool>(
                name: "UsesOriginalBottle",
                table: "OrderItems",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
