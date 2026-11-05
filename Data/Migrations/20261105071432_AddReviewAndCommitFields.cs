using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewAndCommitFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ApprovedCustomerVersion",
                table: "ImportRows",
                type: "varbinary(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedSnapshotJson",
                table: "ImportRows",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionReason",
                table: "ImportRows",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details",
                table: "ImportAuditEntries",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedCustomerVersion",
                table: "ImportRows");

            migrationBuilder.DropColumn(
                name: "ApprovedSnapshotJson",
                table: "ImportRows");

            migrationBuilder.DropColumn(
                name: "DecisionReason",
                table: "ImportRows");

            migrationBuilder.DropColumn(
                name: "Details",
                table: "ImportAuditEntries");
        }
    }
}
