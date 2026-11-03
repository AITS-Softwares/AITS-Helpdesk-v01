using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Help_Desk.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketEmailSourceMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceEmailMessageId",
                table: "Tickets",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_SourceEmailMessageId",
                table: "Tickets",
                column: "SourceEmailMessageId",
                unique: true,
                filter: "[SourceEmailMessageId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_SourceEmailMessageId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SourceEmailMessageId",
                table: "Tickets");
        }
    }
}
