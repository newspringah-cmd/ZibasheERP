using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260802160000_AddInvoiceWorkflow")]
public partial class AddInvoiceWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "InvoiceNumber",
            table: "Invoices",
            type: "nvarchar(50)",
            maxLength: 50,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(max)");

        migrationBuilder.AddColumn<int>(
            name: "Status",
            table: "Invoices",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateIndex(
            name: "IX_Invoices_InvoiceNumber",
            table: "Invoices",
            column: "InvoiceNumber",
            unique: true);

        migrationBuilder.DropIndex(
            name: "IX_Invoices_OrderId",
            table: "Invoices");

        migrationBuilder.CreateIndex(
            name: "IX_Invoices_OrderId",
            table: "Invoices",
            column: "OrderId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Invoices_InvoiceNumber", table: "Invoices");
        migrationBuilder.DropIndex(name: "IX_Invoices_OrderId", table: "Invoices");
        migrationBuilder.DropColumn(name: "Status", table: "Invoices");

        migrationBuilder.AlterColumn<string>(
            name: "InvoiceNumber",
            table: "Invoices",
            type: "nvarchar(max)",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(50)",
            oldMaxLength: 50);

        migrationBuilder.CreateIndex(
            name: "IX_Invoices_OrderId",
            table: "Invoices",
            column: "OrderId");
    }
}
