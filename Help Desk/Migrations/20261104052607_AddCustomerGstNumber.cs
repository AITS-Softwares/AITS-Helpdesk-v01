using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Help_Desk.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerGstNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GstNumber",
                table: "Companies",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GstNumber",
                table: "Companies");
        }
    }
}
