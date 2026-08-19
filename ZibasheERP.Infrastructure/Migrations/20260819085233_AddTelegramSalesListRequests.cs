using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramSalesListRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinimumRequestVolumeMl",
                table: "SalesLists",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "TelegramDiscussionMessageId",
                table: "SalesLists",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesListRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TelegramUserId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TelegramUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VolumeMl = table.Column<int>(type: "int", nullable: false),
                    BottleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PerfumePricePerMl = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BottlePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedByAdmin = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExternalReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesListRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesListRequests_Bottles_BottleId",
                        column: x => x.BottleId,
                        principalTable: "Bottles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesListRequests_SalesLists_SalesListId",
                        column: x => x.SalesListId,
                        principalTable: "SalesLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesListRequests_BottleId",
                table: "SalesListRequests",
                column: "BottleId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesListRequests_ExternalReference",
                table: "SalesListRequests",
                column: "ExternalReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesListRequests_SalesListId_Kind_Status_CreatedAt",
                table: "SalesListRequests",
                columns: new[] { "SalesListId", "Kind", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesListRequests_SalesListId_TelegramUserId_Status",
                table: "SalesListRequests",
                columns: new[] { "SalesListId", "TelegramUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesListRequests");

            migrationBuilder.DropColumn(
                name: "MinimumRequestVolumeMl",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "TelegramDiscussionMessageId",
                table: "SalesLists");
        }
    }
}
