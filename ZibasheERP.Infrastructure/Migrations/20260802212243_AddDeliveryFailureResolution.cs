using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryFailureResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResolutionNotes",
                table: "IntegrationDeliveryFailures",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "IntegrationDeliveryFailures",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeliveryFailures_ResolvedAt_ReportedAt",
                table: "IntegrationDeliveryFailures",
                columns: new[] { "ResolvedAt", "ReportedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IntegrationDeliveryFailures_ResolvedAt_ReportedAt",
                table: "IntegrationDeliveryFailures");

            migrationBuilder.DropColumn(
                name: "ResolutionNotes",
                table: "IntegrationDeliveryFailures");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "IntegrationDeliveryFailures");
        }
    }
}
