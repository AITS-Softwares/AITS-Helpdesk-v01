using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Help_Desk.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerEmailDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailDomains",
                table: "Companies",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            // Keep the legacy routing key populated for existing customers that have no
            // support route. The UI no longer exposes projects; this row is internal only.
            migrationBuilder.Sql("INSERT INTO [Projects] ([ClientId], [Name]) SELECT c.[Id], N'General' FROM [Companies] c WHERE NOT EXISTS (SELECT 1 FROM [Projects] p WHERE p.[ClientId] = c.[Id])");
            migrationBuilder.Sql("UPDATE t SET t.[ProjectId] = (SELECT TOP (1) p.[Id] FROM [Projects] p WHERE p.[ClientId] = t.[ClientId] ORDER BY p.[Id]) FROM [Tickets] t WHERE t.[ProjectId] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailDomains",
                table: "Companies");
        }
    }
}
