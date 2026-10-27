using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Data;

public class HelpDeskDbContext(DbContextOptions<HelpDeskDbContext> options) : DbContext(options)
{
    public DbSet<DeskAccount> Accounts => Set<DeskAccount>();
    public DbSet<Client> Companies => Set<Client>();
    public DbSet<DeskUser> Users => Set<DeskUser>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> Comments => Set<TicketComment>();
    public DbSet<DatabaseState> DatabaseStates => Set<DatabaseState>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DeskAccount>().Property(a => a.IsApproved).HasDefaultValue(true);
        model.Entity<DeskAccount>().Property(a => a.Email).UseCollation("Latin1_General_100_CI_AS");
        model.Entity<DeskAccount>().HasIndex(a => a.Email).IsUnique();
        model.Entity<DeskAccount>().HasIndex(a => a.DeskUserId).IsUnique().HasFilter("[DeskUserId] IS NOT NULL");
        model.Entity<DeskAccount>().HasOne(a => a.DeskUser).WithMany().HasForeignKey(a => a.DeskUserId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DeskAccount>().ToTable("Accounts", t => t.HasCheckConstraint("CK_Account_Role", "(Role = 0 AND DeskUserId IS NULL) OR (Role IN (1, 2) AND DeskUserId IS NOT NULL)"));
        model.Entity<Client>().ToTable("Companies");
        model.Entity<Client>().Property(c => c.Name).UseCollation("Latin1_General_100_CI_AS");
        model.Entity<Client>().HasIndex(c => c.Name).IsUnique();
        model.Entity<DeskUser>().Property(u => u.Email).UseCollation("Latin1_General_100_CI_AS");
        model.Entity<DeskUser>().HasIndex(u => u.Email).IsUnique();
        model.Entity<DeskUser>().HasOne(u => u.Client).WithMany().HasForeignKey(u => u.ClientId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DeskUser>().ToTable("Users", t => t.HasCheckConstraint("CK_User_Company", "(UserType = 0 AND ClientId IS NOT NULL) OR (UserType = 1 AND ClientId IS NULL)"));
        model.Entity<Ticket>().HasOne(t => t.Client).WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ticket>().HasOne(t => t.Requester).WithMany().HasForeignKey(t => t.RequesterId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ticket>().HasOne(t => t.Assignee).WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ticket>().ToTable("Tickets", t => {
            t.HasCheckConstraint("CK_Ticket_Status", "Status BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_Ticket_Priority", "Priority BETWEEN 0 AND 3");
        });
        model.Entity<TicketComment>().HasOne(c => c.Ticket).WithMany(t => t.Comments).HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class DatabaseState
{
    public int Id { get; set; }
    public DateTime InitializedAt { get; set; }
}
