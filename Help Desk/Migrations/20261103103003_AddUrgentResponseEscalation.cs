using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Help_Desk.Migrations
{
    /// <inheritdoc />
    public partial class AddUrgentResponseEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TatPolicy_Values",
                table: "TatPolicies");

            migrationBuilder.AddColumn<DateTime>(
                name: "SupportLevelStartedAt",
                table: "Tickets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.Sql("UPDATE [Tickets] SET [SupportLevelStartedAt] = [CreatedAt];");

            migrationBuilder.AlterColumn<DateTime>(
                name: "SupportLevelStartedAt",
                table: "Tickets",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "L1ResponseHours",
                table: "TatPolicies",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "L2ResponseHours",
                table: "TatPolicies",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TatPolicy_Values",
                table: "TatPolicies",
                sql: "Id = 1 AND LowHours BETWEEN 1 AND 8760 AND MediumHours BETWEEN 1 AND 8760 AND HighHours BETWEEN 1 AND 8760 AND UrgentHours BETWEEN 1 AND 8760 AND L1ResponseHours BETWEEN 1 AND 8760 AND L2ResponseHours BETWEEN 1 AND 8760");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TatPolicy_Values",
                table: "TatPolicies");

            migrationBuilder.DropColumn(
                name: "SupportLevelStartedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "L1ResponseHours",
                table: "TatPolicies");

            migrationBuilder.DropColumn(
                name: "L2ResponseHours",
                table: "TatPolicies");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TatPolicy_Values",
                table: "TatPolicies",
                sql: "Id = 1 AND LowHours BETWEEN 1 AND 8760 AND MediumHours BETWEEN 1 AND 8760 AND HighHours BETWEEN 1 AND 8760 AND UrgentHours BETWEEN 1 AND 8760");
        }
    }
}
