using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    public partial class AddNotificationRetryBackoff : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_Status_LockedUntil_CreatedAt",
                table: "NotificationOutbox");

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "NotificationOutbox",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_NextAttemptAt_LockedUntil_CreatedAt",
                table: "NotificationOutbox",
                columns: new[] { "Status", "NextAttemptAt", "LockedUntil", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_Status_NextAttemptAt_LockedUntil_CreatedAt",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "NotificationOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_LockedUntil_CreatedAt",
                table: "NotificationOutbox",
                columns: new[] { "Status", "LockedUntil", "CreatedAt" });
        }
    }
}
