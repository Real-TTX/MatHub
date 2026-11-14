using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClientPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Version",
                table: "Package",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "ClientPackage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    PackageId = table.Column<long>(type: "bigint", nullable: false),
                    PackageVersionId = table.Column<long>(type: "bigint", nullable: true),
                    Installed = table.Column<bool>(type: "boolean", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPackage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPackage_Client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientPackage_PackageVersion_PackageVersionId",
                        column: x => x.PackageVersionId,
                        principalTable: "PackageVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientPackage_Package_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Package",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPackage_ClientId",
                table: "ClientPackage",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPackage_PackageId_ClientId",
                table: "ClientPackage",
                columns: new[] { "PackageId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientPackage_PackageVersionId",
                table: "ClientPackage",
                column: "PackageVersionId");

            // what MatHub did last with a package on a client, from the runs that worked so far (kinds 2 install, 3 uninstall; status 3 succeeded)
            migrationBuilder.Sql("""
                INSERT INTO "ClientPackage" ("ClientId", "PackageId", "PackageVersionId", "Installed", "Date", "CreateDate", "UpdateDate")
                SELECT DISTINCT ON (r."ClientId", j."PackageId") r."ClientId", j."PackageId", j."PackageVersionId", j."Kind" = 2, r."FinishedDate", now(), now()
                FROM "JobRun" r JOIN "Job" j ON j."Id" = r."JobId"
                WHERE r."Status" = 3 AND j."Kind" IN (2, 3) AND j."PackageId" IS NOT NULL AND r."FinishedDate" IS NOT NULL
                  AND EXISTS (SELECT 1 FROM "Package" p WHERE p."Id" = j."PackageId") AND EXISTS (SELECT 1 FROM "Client" c WHERE c."Id" = r."ClientId")
                ORDER BY r."ClientId", j."PackageId", r."FinishedDate" DESC;
                """);

            // the detection by winget id compares in lower case: an index for it
            migrationBuilder.Sql("""CREATE INDEX "IX_ClientSoftware_WingetId_Lower" ON "ClientSoftware" (lower("WingetId"));""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_ClientSoftware_WingetId_Lower";""");

            migrationBuilder.DropTable(
                name: "ClientPackage");

            migrationBuilder.AlterColumn<string>(
                name: "Version",
                table: "Package",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }
    }
}
