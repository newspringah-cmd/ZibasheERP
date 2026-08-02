using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260802180000_AddShipmentWorkflow")]
public partial class AddShipmentWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("AddressId", "Shipments", "uniqueidentifier", nullable: false, defaultValue: Guid.Empty);
        migrationBuilder.AddColumn<string>("ReceiverName", "Shipments", "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("Mobile", "Shipments", "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("Province", "Shipments", "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("City", "Shipments", "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("PostalCode", "Shipments", "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("FullAddress", "Shipments", "nvarchar(1000)", maxLength: 1000, nullable: false, defaultValue: "");

        migrationBuilder.AlterColumn<string>("ShippingCompany", "Shipments", "nvarchar(100)", maxLength: 100, nullable: false, oldClrType: typeof(string), oldType: "nvarchar(max)");
        migrationBuilder.AlterColumn<string>("TrackingCode", "Shipments", "nvarchar(100)", maxLength: 100, nullable: true, oldClrType: typeof(string), oldType: "nvarchar(max)", oldNullable: true);
        migrationBuilder.AlterColumn<string>("Notes", "Shipments", "nvarchar(500)", maxLength: 500, nullable: true, oldClrType: typeof(string), oldType: "nvarchar(max)", oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Shipments_TrackingCode",
            table: "Shipments",
            column: "TrackingCode",
            unique: true,
            filter: "[TrackingCode] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Shipments_TrackingCode", "Shipments");
        foreach (var column in new[] { "AddressId", "ReceiverName", "Mobile", "Province", "City", "PostalCode", "FullAddress" })
            migrationBuilder.DropColumn(column, "Shipments");

        migrationBuilder.AlterColumn<string>("ShippingCompany", "Shipments", "nvarchar(max)", nullable: false, oldClrType: typeof(string), oldType: "nvarchar(100)", oldMaxLength: 100);
        migrationBuilder.AlterColumn<string>("TrackingCode", "Shipments", "nvarchar(max)", nullable: true, oldClrType: typeof(string), oldType: "nvarchar(100)", oldMaxLength: 100, oldNullable: true);
        migrationBuilder.AlterColumn<string>("Notes", "Shipments", "nvarchar(max)", nullable: true, oldClrType: typeof(string), oldType: "nvarchar(500)", oldMaxLength: 500, oldNullable: true);
    }
}
