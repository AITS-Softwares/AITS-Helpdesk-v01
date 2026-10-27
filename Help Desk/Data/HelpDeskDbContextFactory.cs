using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Help_Desk.Data;

public class HelpDeskDbContextFactory : IDesignTimeDbContextFactory<HelpDeskDbContext>
{
    public HelpDeskDbContext CreateDbContext(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var connection = builder.Configuration.GetConnectionString("HelpDesk")
            ?? throw new InvalidOperationException("The HelpDesk SQL Server connection string is missing.");
        return new HelpDeskDbContext(new DbContextOptionsBuilder<HelpDeskDbContext>().UseSqlServer(connection).Options);
    }
}
