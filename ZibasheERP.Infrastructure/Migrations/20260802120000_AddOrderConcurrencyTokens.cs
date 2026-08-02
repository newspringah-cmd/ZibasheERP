using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260802120000_AddOrderConcurrencyTokens")]
public partial class AddOrderConcurrencyTokens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "SalesLists",
            type: "rowversion",
            rowVersion: true,
            nullable: false);

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "Customers",
            type: "rowversion",
            rowVersion: true,
            nullable: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "SalesLists");

        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "Customers");
    }
}
