using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dochub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DocumentVersionsAndIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "blob_etag",
                schema: "dochub",
                table: "uploaded_documents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "chunk_count",
                schema: "dochub",
                table: "uploaded_documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_md5",
                schema: "dochub",
                table: "uploaded_documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_version_id",
                schema: "dochub",
                table: "uploaded_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "processed_at",
                schema: "dochub",
                table: "uploaded_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_processed_at",
                schema: "dochub",
                table: "artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status",
                schema: "dochub",
                table: "artifacts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "document_versions",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    source_location = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    source_type = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    blob_container = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    blob_path = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    blob_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    blob_etag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    content_md5 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    blob_last_modified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_versions_uploaded_documents_uploaded_document_id",
                        column: x => x.uploaded_document_id,
                        principalSchema: "dochub",
                        principalTable: "uploaded_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_artifact_id_name_uploaded_at",
                schema: "dochub",
                table: "document_versions",
                columns: new[] { "artifact_id", "name", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_content_md5",
                schema: "dochub",
                table: "document_versions",
                column: "content_md5");

            migrationBuilder.CreateIndex(
                name: "IX_document_versions_uploaded_document_id_revision",
                schema: "dochub",
                table: "document_versions",
                columns: new[] { "uploaded_document_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_versions",
                schema: "dochub");

            migrationBuilder.DropColumn(
                name: "blob_etag",
                schema: "dochub",
                table: "uploaded_documents");

            migrationBuilder.DropColumn(
                name: "chunk_count",
                schema: "dochub",
                table: "uploaded_documents");

            migrationBuilder.DropColumn(
                name: "content_md5",
                schema: "dochub",
                table: "uploaded_documents");

            migrationBuilder.DropColumn(
                name: "current_version_id",
                schema: "dochub",
                table: "uploaded_documents");

            migrationBuilder.DropColumn(
                name: "processed_at",
                schema: "dochub",
                table: "uploaded_documents");

            migrationBuilder.DropColumn(
                name: "last_processed_at",
                schema: "dochub",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "dochub",
                table: "artifacts");
        }
    }
}
