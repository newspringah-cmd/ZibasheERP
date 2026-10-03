using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZibasheERP.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramAccountantLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramAccountantLearningMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChatId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ChatTitle = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    ReplyToMessageId = table.Column<long>(type: "bigint", nullable: true),
                    SenderTelegramUserId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SenderUsername = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SenderDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsAccountant = table.Column<bool>(type: "bit", nullable: false),
                    MessageText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramAccountantLearningMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelegramAccountantLearningSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsLearningEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsAutoReplyEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CollectionStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedByTelegramUserId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramAccountantLearningSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAccountantLearningMessages_ChatId_MessageId",
                table: "TelegramAccountantLearningMessages",
                columns: new[] { "ChatId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAccountantLearningMessages_CreatedAt",
                table: "TelegramAccountantLearningMessages",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramAccountantLearningMessages");

            migrationBuilder.DropTable(
                name: "TelegramAccountantLearningSettings");
        }
    }
}
