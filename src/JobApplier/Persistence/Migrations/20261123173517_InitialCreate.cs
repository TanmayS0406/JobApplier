using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobApplier.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ScreenshotFile = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusDetail = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QueueId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AccountRecorded = table.Column<bool>(type: "bit", nullable: false),
                    LimitHit = table.Column<bool>(type: "bit", nullable: false),
                    RetryAfterUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LimitResetKnown = table.Column<bool>(type: "bit", nullable: false),
                    AutoRetry = table.Column<bool>(type: "bit", nullable: false),
                    LimitRetries = table.Column<int>(type: "int", nullable: false),
                    AutoSubmit = table.Column<bool>(type: "bit", nullable: false),
                    MinMatchPercent = table.Column<int>(type: "int", nullable: false),
                    Apply = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Extracted = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Found = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Log = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Match = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingHelp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedUtc",
                table: "Jobs",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
