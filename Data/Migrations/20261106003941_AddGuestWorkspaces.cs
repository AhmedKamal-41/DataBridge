using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_NormalizedEmail",
                table: "Customers");

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "ImportBatches",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "Customers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "GuestWorkspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    TokenExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SessionExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Imports = table.Column<int>(type: "int", nullable: false),
                    AiSearches = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestWorkspaces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_WorkspaceId_Id",
                table: "ImportBatches",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_WorkspaceId_NormalizedEmail",
                table: "Customers",
                columns: new[] { "WorkspaceId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuestWorkspaces_TokenHash",
                table: "GuestWorkspaces",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove demo data before restoring the shared member tables and email index.
            migrationBuilder.Sql("""
                DELETE entry
                FROM ImportAuditEntries AS entry
                INNER JOIN ImportBatches AS batch ON batch.Id = entry.ImportBatchId
                WHERE batch.WorkspaceId <> '00000000-0000-0000-0000-000000000000';
                DELETE FROM ImportBatches WHERE WorkspaceId <> '00000000-0000-0000-0000-000000000000';
                DELETE FROM Customers WHERE WorkspaceId <> '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.DropTable(
                name: "GuestWorkspaces");

            migrationBuilder.DropIndex(
                name: "IX_ImportBatches_WorkspaceId_Id",
                table: "ImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_Customers_WorkspaceId_NormalizedEmail",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "ImportBatches");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_NormalizedEmail",
                table: "Customers",
                column: "NormalizedEmail",
                unique: true);
        }
    }
}
