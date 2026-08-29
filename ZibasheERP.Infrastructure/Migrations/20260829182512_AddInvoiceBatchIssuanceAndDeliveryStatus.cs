using Microsoft.EntityFrameworkCore.Migrations;

using System;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceBatchIssuanceAndDeliveryStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey("FK_Orders_SalesLists_SalesListId", "Orders");
            migrationBuilder.AlterColumn<Guid>(name: "SalesListId", table: "Orders", type: "uniqueidentifier", nullable: true, oldClrType: typeof(Guid), oldType: "uniqueidentifier");
            migrationBuilder.AddColumn<Guid>(name: "InvoiceIssuanceBatchId", table: "Orders", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<int>(name: "Source", table: "Orders", type: "int", nullable: false, defaultValue: 0);

            migrationBuilder.AlterColumn<Guid>(name: "SalesListId", table: "OrderItems", type: "uniqueidentifier", nullable: true, oldClrType: typeof(Guid), oldType: "uniqueidentifier");
            migrationBuilder.AlterColumn<Guid>(name: "PerfumeId", table: "OrderItems", type: "uniqueidentifier", nullable: true, oldClrType: typeof(Guid), oldType: "uniqueidentifier");
            migrationBuilder.AddColumn<string>(name: "ManualDescription", table: "OrderItems", type: "nvarchar(300)", maxLength: 300, nullable: true);

            migrationBuilder.AddColumn<int>(name: "DeliveryStatus", table: "Invoices", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<DateTime>(name: "DeliveryStatusChangedAt", table: "Invoices", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<string>(name: "DeliveryStatusNote", table: "Invoices", type: "nvarchar(1000)", maxLength: 1000, nullable: true);

            migrationBuilder.CreateTable(
                name: "InvoiceIssuanceBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByTelegramUserId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_InvoiceIssuanceBatches", x => x.Id));

            migrationBuilder.CreateTable(
                name: "InvoiceIssuanceBatchSalesLists",
                columns: table => new
                {
                    InvoiceIssuanceBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceIssuanceBatchSalesLists", x => new { x.InvoiceIssuanceBatchId, x.SalesListId });
                    table.ForeignKey("FK_InvoiceIssuanceBatchSalesLists_InvoiceIssuanceBatches_InvoiceIssuanceBatchId", x => x.InvoiceIssuanceBatchId, "InvoiceIssuanceBatches", "Id", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey("FK_InvoiceIssuanceBatchSalesLists_SalesLists_SalesListId", x => x.SalesListId, "SalesLists", "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(name: "IX_Orders_InvoiceIssuanceBatchId", table: "Orders", column: "InvoiceIssuanceBatchId");
            migrationBuilder.CreateIndex(name: "IX_InvoiceIssuanceBatches_Status_CreatedAt", table: "InvoiceIssuanceBatches", columns: new[] { "Status", "CreatedAt" });
            migrationBuilder.CreateIndex(name: "IX_InvoiceIssuanceBatchSalesLists_SalesListId", table: "InvoiceIssuanceBatchSalesLists", column: "SalesListId", unique: true);
            migrationBuilder.AddForeignKey(name: "FK_Orders_InvoiceIssuanceBatches_InvoiceIssuanceBatchId", table: "Orders", column: "InvoiceIssuanceBatchId", principalTable: "InvoiceIssuanceBatches", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Orders_SalesLists_SalesListId", table: "Orders", column: "SalesListId", principalTable: "SalesLists", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey("FK_Orders_InvoiceIssuanceBatches_InvoiceIssuanceBatchId", "Orders");
            migrationBuilder.DropForeignKey("FK_Orders_SalesLists_SalesListId", "Orders");
            migrationBuilder.DropTable("InvoiceIssuanceBatchSalesLists");
            migrationBuilder.DropTable("InvoiceIssuanceBatches");
            migrationBuilder.DropIndex("IX_Orders_InvoiceIssuanceBatchId", "Orders");
            migrationBuilder.DropColumn("InvoiceIssuanceBatchId", "Orders");
            migrationBuilder.DropColumn("Source", "Orders");
            migrationBuilder.DropColumn("ManualDescription", "OrderItems");
            migrationBuilder.DropColumn("DeliveryStatus", "Invoices");
            migrationBuilder.DropColumn("DeliveryStatusChangedAt", "Invoices");
            migrationBuilder.DropColumn("DeliveryStatusNote", "Invoices");
            migrationBuilder.AlterColumn<Guid>(name: "SalesListId", table: "Orders", type: "uniqueidentifier", nullable: false, defaultValue: Guid.Empty, oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "SalesListId", table: "OrderItems", type: "uniqueidentifier", nullable: false, defaultValue: Guid.Empty, oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "PerfumeId", table: "OrderItems", type: "uniqueidentifier", nullable: false, defaultValue: Guid.Empty, oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
            migrationBuilder.AddForeignKey(name: "FK_Orders_SalesLists_SalesListId", table: "Orders", column: "SalesListId", principalTable: "SalesLists", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
        }
    }
}
