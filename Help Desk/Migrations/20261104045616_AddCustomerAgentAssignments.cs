using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Help_Desk.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerAgentAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerAgents",
                columns: table => new
                {
                    ClientId = table.Column<int>(type: "int", nullable: false),
                    StaffId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAgents", x => new { x.ClientId, x.StaffId });
                    table.ForeignKey(
                        name: "FK_CustomerAgents_AitsStaff_StaffId",
                        column: x => x.StaffId,
                        principalTable: "AitsStaff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerAgents_Companies_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAgents_StaffId",
                table: "CustomerAgents",
                column: "StaffId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerAgents");
        }
    }
}
