using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeAssistant.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddModelWireFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseAnthropicWire",
                table: "AiModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseAnthropicWire",
                table: "AiModels");
        }
    }
}
