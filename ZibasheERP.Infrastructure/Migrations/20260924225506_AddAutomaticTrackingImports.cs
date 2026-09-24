using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticTrackingImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ShippingTelegramMessageId",
                table: "OrderItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrackingImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Carrier = table.Column<int>(type: "int", nullable: false),
                    RequestedByTelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    SourceChatId = table.Column<long>(type: "bigint", nullable: false),
                    SourceMessageId = table.Column<long>(type: "bigint", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackingImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackingDispatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Carrier = table.Column<int>(type: "int", nullable: false),
                    TrackingCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecipientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Destination = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TrackingUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ShippingRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    MatchNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CardImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    SendingStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackingDispatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackingDispatches_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrackingDispatches_TrackingImportBatches_ImportBatchId",
                        column: x => x.ImportBatchId,
                        principalTable: "TrackingImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackingDispatchDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TrackingDispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TelegramChatId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackingDispatchDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackingDispatchDeliveries_TrackingDispatches_TrackingDispatchId",
                        column: x => x.TrackingDispatchId,
                        principalTable: "TrackingDispatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackingDispatchDeliveries_TrackingDispatchId_TelegramChatId",
                table: "TrackingDispatchDeliveries",
                columns: new[] { "TrackingDispatchId", "TelegramChatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackingDispatches_Carrier_TrackingCode",
                table: "TrackingDispatches",
                columns: new[] { "Carrier", "TrackingCode" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_TrackingDispatches_CustomerId",
                table: "TrackingDispatches",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackingDispatches_ImportBatchId",
                table: "TrackingDispatches",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackingDispatches_ShippingRequestId",
                table: "TrackingDispatches",
                column: "ShippingRequestId",
                unique: true,
                filter: "[ShippingRequestId] IS NOT NULL AND [Status] IN (2, 3, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_TrackingImportBatches_SourceHash",
                table: "TrackingImportBatches",
                column: "SourceHash",
                unique: true,
                filter: "[IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackingDispatchDeliveries");

            migrationBuilder.DropTable(
                name: "TrackingDispatches");

            migrationBuilder.DropTable(
                name: "TrackingImportBatches");

            migrationBuilder.DropColumn(
                name: "ShippingTelegramMessageId",
                table: "OrderItems");
        }
    }
}
