using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Data;

// The old SQLite schema is read independently of the current SQL Server tables.
public class LegacyImportDbContext(DbContextOptions<LegacyImportDbContext> options) : DbContext(options)
{
    public DbSet<Client> Companies => Set<Client>();
    public DbSet<UserEditViewModel> Users => Set<UserEditViewModel>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Client>().ToTable("Companies");
        model.Entity<UserEditViewModel>().ToTable("Users");
        model.Entity<Ticket>().Ignore(t => t.ProjectId).Ignore(t => t.Project).Ignore(t => t.Requester).Ignore(t => t.Assignee).Ignore(t => t.Client);
        model.Entity<Ticket>().ToTable("Tickets");
        model.Entity<TicketComment>().ToTable("Comments");
        model.Entity<TicketComment>().HasOne(c => c.Ticket).WithMany(t => t.Comments).HasForeignKey(c => c.TicketId);
    }
}

public class LegacyDeskData
{
    public int SchemaVersion { get; set; }
    public List<Client> Clients { get; set; } = [];
    public List<UserEditViewModel> Users { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
    public DeskData ToDeskData() => new() { SchemaVersion = SchemaVersion, Clients = Clients, Users = Users.Select(u => u.ToEntity()).ToList(), Tickets = Tickets };
}
