using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260802230000_AddOrderDeliveryAddress")]
public partial class AddOrderDeliveryAddress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "DeliveryAddressId",
            table: "Orders",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Orders_DeliveryAddressId",
            table: "Orders",
            column: "DeliveryAddressId");

        migrationBuilder.AddForeignKey(
            name: "FK_Orders_Addresses_DeliveryAddressId",
            table: "Orders",
            column: "DeliveryAddressId",
            principalTable: "Addresses",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Orders_Addresses_DeliveryAddressId",
            table: "Orders");
        migrationBuilder.DropIndex(name: "IX_Orders_DeliveryAddressId", table: "Orders");
        migrationBuilder.DropColumn(name: "DeliveryAddressId", table: "Orders");
    }
}
