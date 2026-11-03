using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Help_Desk.Migrations
{
    /// <inheritdoc />
    public partial class AddInboundReplyDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceEmailMessageId",
                table: "Comments",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Comments_SourceEmailMessageId",
                table: "Comments",
                column: "SourceEmailMessageId",
                unique: true,
                filter: "[SourceEmailMessageId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Comments_SourceEmailMessageId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "SourceEmailMessageId",
                table: "Comments");
        }
    }
}
