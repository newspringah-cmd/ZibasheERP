using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260804010000_AddInvoicePaymentAccounts")]
public partial class AddInvoicePaymentAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InvoicePaymentAccounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CardNumber = table.Column<string>(type: "nvarchar(19)", maxLength: 19, nullable: false),
                AccountHolder = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                DisplayOrder = table.Column<int>(type: "int", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                IsDeleted = table.Column<bool>(type: "bit", nullable: false)
            }, constraints: table => table.PrimaryKey("PK_InvoicePaymentAccounts", x => x.Id));
        migrationBuilder.CreateIndex("IX_InvoicePaymentAccounts_CardNumber", "InvoicePaymentAccounts", "CardNumber", unique: true, filter: "[IsDeleted] = 0");
        migrationBuilder.CreateIndex("IX_InvoicePaymentAccounts_IsActive_DisplayOrder", "InvoicePaymentAccounts", new[] { "IsActive", "DisplayOrder" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("InvoicePaymentAccounts");
}
