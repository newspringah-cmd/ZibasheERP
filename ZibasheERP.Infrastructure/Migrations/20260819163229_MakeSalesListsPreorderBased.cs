using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeSalesListsPreorderBased : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "BatchId",
                table: "SalesLists",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "PerfumeId",
                table: "SalesLists",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE salesList
                SET salesList.PerfumeId = batch.PerfumeId
                FROM SalesLists AS salesList
                INNER JOIN Batches AS batch ON batch.Id = salesList.BatchId;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "PerfumeId",
                table: "SalesLists",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesLists_PerfumeId",
                table: "SalesLists",
                column: "PerfumeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesLists_Perfumes_PerfumeId",
                table: "SalesLists",
                column: "PerfumeId",
                principalTable: "Perfumes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesLists_Perfumes_PerfumeId",
                table: "SalesLists");

            migrationBuilder.DropIndex(
                name: "IX_SalesLists_PerfumeId",
                table: "SalesLists");

            migrationBuilder.DropColumn(
                name: "PerfumeId",
                table: "SalesLists");

            migrationBuilder.AlterColumn<Guid>(
                name: "BatchId",
                table: "SalesLists",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
