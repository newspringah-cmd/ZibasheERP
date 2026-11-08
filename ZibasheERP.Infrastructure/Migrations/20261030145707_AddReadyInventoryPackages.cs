using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReadyInventoryPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InventoryCode",
                table: "SalesLists",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InventoryCreatedByUserId",
                table: "SalesLists",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InventoryReferenceListId",
                table: "SalesLists",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoragePosition",
                table: "SalesLists",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryPositionChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousPosition = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Position = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    AdminUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryPositionChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryPositionChanges_SalesLists_SalesListId",
                        column: x => x.SalesListId,
                        principalTable: "SalesLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesLists_InventoryCode",
                table: "SalesLists",
                column: "InventoryCode",
                unique: true,
                filter: "[InventoryCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryPositionChanges_SalesListId_CreatedAt",
                table: "InventoryPositionChanges",
                columns: new[] { "SalesListId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryPositionChanges");

            migrationBuilder.DropIndex(
                name: "IX_SalesLists_InventoryCode",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "InventoryCode",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "InventoryCreatedByUserId",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "InventoryReferenceListId",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "StoragePosition",
                table: "SalesLists");
        }
    }
}
