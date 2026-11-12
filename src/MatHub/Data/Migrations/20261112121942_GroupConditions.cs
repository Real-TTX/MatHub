using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupConditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MatchAny",
                table: "ClientGroup",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MoreRules",
                table: "ClientGroup",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchAny",
                table: "ClientGroup");

            migrationBuilder.DropColumn(
                name: "MoreRules",
                table: "ClientGroup");
        }
    }
}
