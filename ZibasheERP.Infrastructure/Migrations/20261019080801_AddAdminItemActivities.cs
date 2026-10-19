using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminItemActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminItemActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorTelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    ActorUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActorDisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceUpdateId = table.Column<long>(type: "bigint", nullable: false),
                    SalesListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SalesListRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<int>(type: "int", nullable: false),
                    PreviousVolumeMl = table.Column<int>(type: "int", nullable: true),
                    NewVolumeMl = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminItemActivities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminItemActivities_ActorTelegramUserId_CreatedAt",
                table: "AdminItemActivities",
                columns: new[] { "ActorTelegramUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminItemActivities_CreatedAt_Action",
                table: "AdminItemActivities",
                columns: new[] { "CreatedAt", "Action" });

            // Record the actual activation time, not a fabricated historical start.
            migrationBuilder.Sql("""
                INSERT INTO [AdminItemActivities]
                    ([Id],[ActorTelegramUserId],[ActorDisplayName],[SourceUpdateId],[Action],[CreatedAt],[IsDeleted])
                VALUES ('A042B6D0-5458-4B5A-A497-6A6682BA81BD',0,N'system',0,0,SYSUTCDATETIME(),0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminItemActivities");
        }
    }
}
