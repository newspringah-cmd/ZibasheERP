using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramSalesListImports : Migration
    {
        /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
            migrationBuilder.CreateTable(
                name: "TelegramSalesListImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceChannelId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceMessageId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourcePhotoPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RawText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParsedPayload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParseIssues = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReviewChatId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReviewMessageId = table.Column<long>(type: "bigint", nullable: true),
                    TelegramPhotoFileId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SalesListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewedByTelegramUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramSalesListImports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramSalesListImports_SalesLists_SalesListId",
                        column: x => x.SalesListId,
                        principalTable: "SalesLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

        migrationBuilder.AddColumn<long>(
            name: "PublishedMessageId",
            table: "TelegramSalesListImports",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateIndex(
                name: "IX_TelegramSalesListImports_SalesListId",
                table: "TelegramSalesListImports",
                column: "SalesListId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramSalesListImports_SourceChannelId_SourceMessageId",
                table: "TelegramSalesListImports",
                columns: new[] { "SourceChannelId", "SourceMessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramSalesListImports");
        }
    }
}
