using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dochub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UploadRemovalAndDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_dismissals",
                schema: "dochub",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_dismissals", x => new { x.user_id, x.notification_id });
                    table.ForeignKey(
                        name: "FK_notification_dismissals_notifications_notification_id",
                        column: x => x.notification_id,
                        principalSchema: "dochub",
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_dismissals_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "dochub",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_dismissals_notification_id",
                schema: "dochub",
                table: "notification_dismissals",
                column: "notification_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_dismissals",
                schema: "dochub");
        }
    }
}
