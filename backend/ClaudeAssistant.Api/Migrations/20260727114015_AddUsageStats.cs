using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeAssistant.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CacheReadTokens",
                table: "RequestHistory",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "RequestHistory",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "RequestHistory",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "RequestHistory",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "ProviderCostUsd",
                table: "RequestHistory",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "CacheReadTokens",
                table: "ChatMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "ChatMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "ChatMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "ProviderCostUsd",
                table: "ChatMessages",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                table: "RequestHistory");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "RequestHistory");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "RequestHistory");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "RequestHistory");

            migrationBuilder.DropColumn(
                name: "ProviderCostUsd",
                table: "RequestHistory");

            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "ProviderCostUsd",
                table: "ChatMessages");
        }
    }
}
