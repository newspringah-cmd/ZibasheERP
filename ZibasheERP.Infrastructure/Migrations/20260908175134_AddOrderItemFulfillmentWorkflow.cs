using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderItemFulfillmentWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArrivedInIranAt",
                table: "OrderItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DecantedAt",
                table: "OrderItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnteredDecantQueueAt",
                table: "OrderItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FulfillmentStatus",
                table: "OrderItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ShippedAt",
                table: "OrderItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShippingRequestId",
                table: "OrderItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ShippingRequestedAt",
                table: "OrderItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE oi
                SET FulfillmentStatus = CASE
                    WHEN o.Status IN (8, 10) THEN 10
                    WHEN o.Status IN (6, 7) THEN 9
                    WHEN o.Status IN (4, 5) THEN 6
                    WHEN o.Status = 3 THEN 4
                    WHEN o.Status = 2 THEN 2
                    ELSE 1
                END,
                DecantedAt = CASE WHEN o.Status IN (6, 7, 8, 10) THEN COALESCE(o.UpdatedAt, o.CreatedAt) ELSE NULL END,
                ShippedAt = CASE WHEN o.Status IN (8, 10) THEN COALESCE(o.ShippedAt, o.UpdatedAt, o.CreatedAt) ELSE NULL END
                FROM OrderItems oi
                INNER JOIN Orders o ON o.Id = oi.OrderId;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_FulfillmentStatus_SalesListId",
                table: "OrderItems",
                columns: new[] { "FulfillmentStatus", "SalesListId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_ShippingRequestId",
                table: "OrderItems",
                column: "ShippingRequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderItems_FulfillmentStatus_SalesListId",
                table: "OrderItems");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_ShippingRequestId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ArrivedInIranAt",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "DecantedAt",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "EnteredDecantQueueAt",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "FulfillmentStatus",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ShippedAt",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ShippingRequestId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ShippingRequestedAt",
                table: "OrderItems");
        }
    }
}
