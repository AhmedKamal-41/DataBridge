using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewVersionsAndAiCallCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ImportRows",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "AiUsageDays",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Calls = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageDays", x => x.Day);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsageDays");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ImportRows");
        }
    }
}
