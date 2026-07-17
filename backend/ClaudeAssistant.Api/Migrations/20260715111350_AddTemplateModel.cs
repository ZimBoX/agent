using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeAssistant.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "Templates",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Model",
                table: "Templates");
        }
    }
}
