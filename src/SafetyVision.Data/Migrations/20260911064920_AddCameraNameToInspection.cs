using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SafetyVision.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraNameToInspection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CameraName",
                table: "Inspections",
                type: "varchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CameraName",
                table: "Inspections");
        }
    }
}
