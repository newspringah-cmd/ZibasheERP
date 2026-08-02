using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    public partial class AddOutboxChannelClaimIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_Status_NextAttemptAt_LockedUntil_CreatedAt",
                table: "NotificationOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Channel_Status_NextAttemptAt_LockedUntil_CreatedAt",
                table: "NotificationOutbox",
                columns: new[] { "Channel", "Status", "NextAttemptAt", "LockedUntil", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationOutbox_Channel_Status_NextAttemptAt_LockedUntil_CreatedAt",
                table: "NotificationOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_NextAttemptAt_LockedUntil_CreatedAt",
                table: "NotificationOutbox",
                columns: new[] { "Status", "NextAttemptAt", "LockedUntil", "CreatedAt" });
        }
    }
}
