using Help_Desk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Help_Desk.Migrations;

[DbContext(typeof(HelpDeskDbContext))]
[Migration("20261106113500_AddTicketEmailThreadAndCommentAttachments")]
public partial class AddTicketEmailThreadAndCommentAttachments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "EmailThreadMessageId",
            table: "Tickets",
            type: "nvarchar(320)",
            maxLength: 320,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CommentId",
            table: "TicketAttachments",
            type: "int",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_TicketAttachments_CommentId",
            table: "TicketAttachments",
            column: "CommentId");

        migrationBuilder.AddForeignKey(
            name: "FK_TicketAttachments_Comments_CommentId",
            table: "TicketAttachments",
            column: "CommentId",
            principalTable: "Comments",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_TicketAttachments_Comments_CommentId",
            table: "TicketAttachments");

        migrationBuilder.DropIndex(
            name: "IX_TicketAttachments_CommentId",
            table: "TicketAttachments");

        migrationBuilder.DropColumn(
            name: "CommentId",
            table: "TicketAttachments");

        migrationBuilder.DropColumn(
            name: "EmailThreadMessageId",
            table: "Tickets");
    }
}
