using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeAssistant.Api.Migrations
{
    /// <inheritdoc />
    public partial class TemplateMultiRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TemplateRoles",
                columns: table => new
                {
                    RolesId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateRoles", x => new { x.RolesId, x.TemplateId });
                    table.ForeignKey(
                        name: "FK_TemplateRoles_Roles_RolesId",
                        column: x => x.RolesId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TemplateRoles_Templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateRoles_TemplateId",
                table: "TemplateRoles",
                column: "TemplateId");

            // Переносим существующие Template.Role (строка) в новую связь
            // many-to-many, сопоставляя по имени должности, прежде чем колонку
            // снесёт следующий DropColumn — иначе старые шаблоны молча потеряли
            // бы привязку к должности.
            migrationBuilder.Sql(
                """
                INSERT INTO "TemplateRoles" ("RolesId", "TemplateId")
                SELECT r."Id", t."Id"
                FROM "Templates" t
                JOIN "Roles" r ON r."Name" = t."Role"
                """
            );

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Templates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TemplateRoles");

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Roles");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Templates",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
