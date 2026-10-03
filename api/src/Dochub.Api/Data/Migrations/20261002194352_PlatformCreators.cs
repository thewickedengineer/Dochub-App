using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dochub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlatformCreators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_creator",
                schema: "dochub",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_creator",
                schema: "dochub",
                table: "users");
        }
    }
}
