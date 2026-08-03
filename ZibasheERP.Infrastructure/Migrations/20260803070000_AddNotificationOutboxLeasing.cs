using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZibasheERP.Infrastructure.Persistence;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260803070000_AddNotificationOutboxLeasing")]
    public partial class AddNotificationOutboxLeasing : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_Status_CreatedAt",
                table: "NotificationOutbox");

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedUntil",
                table: "NotificationOutbox",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_LockedUntil_CreatedAt",
                table: "NotificationOutbox",
                columns: new[] { "Status", "LockedUntil", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_Status_LockedUntil_CreatedAt",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "NotificationOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_CreatedAt",
                table: "NotificationOutbox",
                columns: new[] { "Status", "CreatedAt" });
        }
    }
}
