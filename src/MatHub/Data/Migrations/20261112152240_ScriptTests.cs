using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScriptTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TestClientId",
                table: "User",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TestClientId",
                table: "User");
        }
    }
}
