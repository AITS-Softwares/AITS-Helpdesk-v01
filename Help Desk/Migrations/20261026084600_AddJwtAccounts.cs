using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Help_Desk.Migrations
{
    /// <inheritdoc />
    public partial class AddJwtAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false, collation: "Latin1_General_100_CI_AS"),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    DeskUserId = table.Column<int>(type: "int", nullable: true),
                    SecurityStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    LockoutUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.CheckConstraint("CK_Account_Role", "(Role = 0 AND DeskUserId IS NULL) OR (Role IN (1, 2) AND DeskUserId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Accounts_Users_DeskUserId",
                        column: x => x.DeskUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_DeskUserId",
                table: "Accounts",
                column: "DeskUserId",
                unique: true,
                filter: "[DeskUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Email",
                table: "Accounts",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Accounts");
        }
    }
}
