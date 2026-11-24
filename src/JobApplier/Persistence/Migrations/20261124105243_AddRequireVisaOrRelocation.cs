using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobApplier.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequireVisaOrRelocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireVisaOrRelocation",
                table: "Jobs",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireVisaOrRelocation",
                table: "Jobs");
        }
    }
}
