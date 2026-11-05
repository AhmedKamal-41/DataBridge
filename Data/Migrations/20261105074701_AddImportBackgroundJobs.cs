using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportBackgroundJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportBatchFiles",
                columns: table => new
                {
                    ImportBatchId = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatchFiles", x => x.ImportBatchId);
                    table.ForeignKey(
                        name: "FK_ImportBatchFiles_ImportBatches_ImportBatchId",
                        column: x => x.ImportBatchId,
                        principalTable: "ImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportBatchJobs",
                columns: table => new
                {
                    ImportBatchId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RunNumber = table.Column<int>(type: "int", nullable: false),
                    ProcessedRows = table.Column<int>(type: "int", nullable: true),
                    TotalRows = table.Column<int>(type: "int", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EnqueuedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatchJobs", x => x.ImportBatchId);
                    table.ForeignKey(
                        name: "FK_ImportBatchJobs_ImportBatches_ImportBatchId",
                        column: x => x.ImportBatchId,
                        principalTable: "ImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        
            // Batches from before this migration are already parsed, so their job
            // row says there is nothing left to do.
            migrationBuilder.Sql("""
                INSERT INTO ImportBatchJobs (ImportBatchId, Kind, Stage, RunNumber, UpdatedAtUtc)
                SELECT Id, 'Parse', 'Completed', 0, SYSUTCDATETIME()
                FROM ImportBatches;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportBatchFiles");

            migrationBuilder.DropTable(
                name: "ImportBatchJobs");
        }
    }
}
