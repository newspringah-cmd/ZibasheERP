using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesListPublicationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Accords",
                table: "SalesLists",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BaseNotes",
                table: "SalesLists",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DisplayBrand",
                table: "SalesLists",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EnglishName",
                table: "SalesLists",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Gender",
                table: "SalesLists",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MiddleNotes",
                table: "SalesLists",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PersianName",
                table: "SalesLists",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProductPageUrl",
                table: "SalesLists",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PublicCode",
                table: "SalesLists",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseYear",
                table: "SalesLists",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TopNotes",
                table: "SalesLists",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SalesLists_PublicCode",
                table: "SalesLists",
                column: "PublicCode",
                unique: true,
                filter: "[PublicCode] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SalesLists_PublicCode",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "Accords",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "BaseNotes",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "DisplayBrand",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "EnglishName",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "MiddleNotes",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "PersianName",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "ProductPageUrl",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "PublicCode",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "ReleaseYear",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "TopNotes",
                table: "SalesLists");
        }
    }
}
