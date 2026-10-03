using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dochub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncDeletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "removed_document_count",
                schema: "dochub",
                table: "source_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "removed_document_count",
                schema: "dochub",
                table: "source_documents");
        }
    }
}
