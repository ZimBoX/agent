using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeAssistant.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateAllowFileSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowFileSearch",
                table: "Templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowFileSearch",
                table: "Templates");
        }
    }
}
