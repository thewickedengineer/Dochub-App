using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dochub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_conversations",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<int>(type: "integer", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope_label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    sources = table.Column<string>(type: "jsonb", nullable: true),
                    cited = table.Column<string>(type: "jsonb", nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    usage = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_messages_chat_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "dochub",
                        principalTable: "chat_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_organization_id_user_id_updated_at",
                schema: "dochub",
                table: "chat_conversations",
                columns: new[] { "organization_id", "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_conversation_id_created_at",
                schema: "dochub",
                table: "chat_messages",
                columns: new[] { "conversation_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_messages",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "chat_conversations",
                schema: "dochub");
        }
    }
}
