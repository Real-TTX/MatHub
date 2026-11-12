using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PilotGroupId",
                table: "Package",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PackageVersionId",
                table: "Job",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PackageVersion",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ApprovedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedUserId = table.Column<long>(type: "bigint", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageVersion_Package_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Package",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageInstaller",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageVersionId = table.Column<long>(type: "bigint", nullable: false),
                    Architecture = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    FileSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Arguments = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UninstallCommand = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    InstallScript = table.Column<string>(type: "text", nullable: true),
                    WingetId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WingetVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProductCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageInstaller", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageInstaller_PackageVersion_PackageVersionId",
                        column: x => x.PackageVersionId,
                        principalTable: "PackageVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // every package from before the versions becomes its version 1 (or the version it named), approved, with its one installer for every platform
            migrationBuilder.Sql("""
                INSERT INTO "PackageVersion" ("PackageId", "Version", "Status", "Source", "ApprovedDate", "CreateDate", "UpdateDate")
                SELECT "Id", COALESCE(NULLIF("Version", ''), CASE WHEN "Kind" = 1 THEN 'latest' ELSE '1' END), 3, 'migrated', now(), now(), now() FROM "Package";
                INSERT INTO "PackageInstaller" ("PackageVersionId", "Architecture", "Kind", "FileName", "FileSize", "FileSha256", "Arguments", "UninstallCommand", "InstallScript", "WingetId", "CreateDate", "UpdateDate")
                SELECT v."Id", 0, p."Kind", p."FileName", p."FileSize", p."FileSha256", p."InstallArguments", p."UninstallCommand", p."InstallScript", p."WingetId", now(), now()
                FROM "PackageVersion" v JOIN "Package" p ON p."Id" = v."PackageId";
                UPDATE "Package" p SET "Version" = v."Version" FROM "PackageVersion" v WHERE v."PackageId" = p."Id";
                """);

            migrationBuilder.DropColumn(name: "FileName", table: "Package");
            migrationBuilder.DropColumn(name: "FileSize", table: "Package");
            migrationBuilder.DropColumn(name: "FileSha256", table: "Package");
            migrationBuilder.DropColumn(name: "InstallArguments", table: "Package");
            migrationBuilder.DropColumn(name: "InstallScript", table: "Package");
            migrationBuilder.DropColumn(name: "UninstallCommand", table: "Package");

            migrationBuilder.CreateIndex(
                name: "IX_Package_PilotGroupId",
                table: "Package",
                column: "PilotGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageInstaller_PackageVersionId",
                table: "PackageInstaller",
                column: "PackageVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersion_PackageId_Status",
                table: "PackageVersion",
                columns: new[] { "PackageId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_Package_ClientGroup_PilotGroupId",
                table: "Package",
                column: "PilotGroupId",
                principalTable: "ClientGroup",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Package_ClientGroup_PilotGroupId",
                table: "Package");

            migrationBuilder.DropTable(
                name: "PackageInstaller");

            migrationBuilder.DropTable(
                name: "PackageVersion");

            migrationBuilder.DropIndex(
                name: "IX_Package_PilotGroupId",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "PackageVersionId",
                table: "Job");

            migrationBuilder.DropColumn(name: "PilotGroupId", table: "Package");

            migrationBuilder.AddColumn<long>(name: "FileSize", table: "Package", type: "bigint", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "Package",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileSha256",
                table: "Package",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstallArguments",
                table: "Package",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstallScript",
                table: "Package",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UninstallCommand",
                table: "Package",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }
    }
}
