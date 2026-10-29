using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Data;

public class HelpDeskDbContext(DbContextOptions<HelpDeskDbContext> options) : DbContext(options)
{
    public DbSet<TatPolicy> TatPolicies => Set<TatPolicy>();
    public DbSet<InternalAccount> InternalAccounts => Set<InternalAccount>();
    public DbSet<ClientAccount> ClientAccounts => Set<ClientAccount>();
    public DbSet<AitsStaffUser> AitsStaff => Set<AitsStaffUser>();
    public DbSet<ClientDeskUser> ClientUsers => Set<ClientDeskUser>();
    public IQueryable<DeskAccount> AccountsWithUsers => Accounts.Include(a => ((InternalAccount)a).Staff).Include(a => ((ClientAccount)a).ClientUser);
    public DbSet<DeskAccount> Accounts => Set<DeskAccount>();
    public DbSet<StaffProject> StaffProjects => Set<StaffProject>();
    public DbSet<CompanyProject> Projects => Set<CompanyProject>();
    public DbSet<Client> Companies => Set<Client>();
    public DbSet<DeskUser> Users => Set<DeskUser>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketEscalation> TicketEscalations => Set<TicketEscalation>();
    public DbSet<TicketPhoto> TicketPhotos => Set<TicketPhoto>();
    public DbSet<PasswordResetRequest> PasswordResetRequests => Set<PasswordResetRequest>();
    public DbSet<TicketEmailNotification> TicketEmailNotifications => Set<TicketEmailNotification>();
    public DbSet<TicketActivity> TicketActivities => Set<TicketActivity>();
    public DbSet<TicketComment> Comments => Set<TicketComment>();
    public DbSet<DatabaseState> DatabaseStates => Set<DatabaseState>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TatPolicy>().Property(p => p.Id).ValueGeneratedNever();
        model.Entity<TatPolicy>().ToTable("TatPolicies", t => t.HasCheckConstraint("CK_TatPolicy_Values", "Id = 1 AND LowHours BETWEEN 1 AND 8760 AND MediumHours BETWEEN 1 AND 8760 AND HighHours BETWEEN 1 AND 8760 AND UrgentHours BETWEEN 1 AND 8760"));
        model.Entity<TicketEscalation>().HasOne(e => e.Ticket).WithMany().HasForeignKey(e => e.TicketId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<TatPolicy>().Property(p => p.Id).ValueGeneratedNever();
        model.Entity<TatPolicy>().ToTable("TatPolicies", t => t.HasCheckConstraint("CK_TatPolicy_Values", "Id = 1 AND LowHours BETWEEN 1 AND 8760 AND MediumHours BETWEEN 1 AND 8760 AND HighHours BETWEEN 1 AND 8760 AND UrgentHours BETWEEN 1 AND 8760"));
        model.Entity<TicketEscalation>().HasOne(e => e.Staff).WithMany().HasForeignKey(e => e.StaffId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<StaffProject>().Property(m => m.SupportLevel).HasDefaultValue(SupportLevel.L1);
        model.Entity<StaffProject>().ToTable("StaffProjects", t => t.HasCheckConstraint("CK_StaffProject_Level", "SupportLevel BETWEEN 1 AND 3"));
        model.Entity<TicketEmailNotification>().HasOne(n => n.Ticket).WithMany().HasForeignKey(n => n.TicketId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<TicketEmailNotification>().HasOne(n => n.Staff).WithMany().HasForeignKey(n => n.StaffId).OnDelete(DeleteBehavior.SetNull);
        model.Entity<TicketEmailNotification>().HasIndex(n => new { n.TicketId, n.StaffId, n.Kind, n.AlertDueAt }).IsUnique().HasFilter("[StaffId] IS NOT NULL");
        model.Entity<TicketEmailNotification>().HasIndex(n => new { n.Status, n.NextAttemptAt });
        model.Entity<TicketEmailNotification>().ToTable("TicketEmailNotifications", t => t.HasCheckConstraint("CK_TicketEmail_Status", "Status BETWEEN 0 AND 5"));
        model.Entity<TicketPhoto>().HasKey(p => p.TicketId);
        model.Entity<TicketPhoto>().HasOne(p => p.Ticket).WithOne().HasForeignKey<TicketPhoto>(p => p.TicketId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<PasswordResetRequest>().Property(r => r.Email).UseCollation("Latin1_General_100_CI_AS");
        model.Entity<PasswordResetRequest>().HasIndex(r => r.Email).IsUnique().HasFilter("[CompletedAt] IS NULL");
        model.Entity<PasswordResetRequest>().HasOne(r => r.CompletedBy).WithMany().HasForeignKey(r => r.CompletedByAccountId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DeskAccount>().UseTpcMappingStrategy();
        model.Entity<DeskAccount>().Property(a => a.Id).UseSequence("AccountIds");
        model.Entity<DeskAccount>().Property(a => a.IsActive).HasDefaultValue(true);
        model.Entity<DeskAccount>().Property(a => a.IsApproved).HasDefaultValue(true);
        model.Entity<DeskAccount>().Property(a => a.Email).UseCollation("Latin1_General_100_CI_AS");
        model.Entity<DeskAccount>().HasIndex(a => a.Email).IsUnique();
        model.Entity<DeskAccount>().HasIndex(a => a.DeskUserId).IsUnique().HasFilter("[DeskUserId] IS NOT NULL");
        model.Entity<InternalAccount>().ToTable("InternalAccounts", t => { t.HasCheckConstraint("CK_InternalAccount_Role", "(Role = 0 AND DeskUserId IS NULL) OR (Role = 1 AND DeskUserId IS NOT NULL)"); t.HasTrigger("TR_InternalAccounts_GroupUniqueness"); t.UseSqlOutputClause(false); });
        model.Entity<ClientAccount>().ToTable("ClientAccounts", t => { t.HasCheckConstraint("CK_ClientAccount_Role", "Role = 2 AND DeskUserId IS NOT NULL"); t.HasTrigger("TR_ClientAccounts_GroupUniqueness"); t.UseSqlOutputClause(false); });
        model.Entity<InternalAccount>().HasOne(a => a.Staff).WithMany().HasForeignKey(a => a.DeskUserId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ClientAccount>().HasOne(a => a.ClientUser).WithMany().HasForeignKey(a => a.DeskUserId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Client>().ToTable("Companies");
        model.Entity<Client>().Property(c => c.Name).UseCollation("Latin1_General_100_CI_AS");
        model.Entity<Client>().HasIndex(c => c.Name).IsUnique();
        model.Entity<DeskUser>().UseTpcMappingStrategy();
        model.Entity<DeskUser>().Property(u => u.Id).UseSequence("UserIds");
        model.Entity<DeskUser>().Property(u => u.Email).UseCollation("Latin1_General_100_CI_AS");
        model.Entity<DeskUser>().HasIndex(u => u.Email).IsUnique();
        model.Entity<DeskUser>().HasOne(u => u.Client).WithMany().HasForeignKey(u => u.ClientId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AitsStaffUser>().Property(s => s.IsActive).HasDefaultValue(true);
        model.Entity<AitsStaffUser>().ToTable("AitsStaff", t => { t.HasCheckConstraint("CK_AitsStaff_Type", "UserType = 1 AND ClientId IS NULL"); t.HasTrigger("TR_AitsStaff_GroupUniqueness"); t.UseSqlOutputClause(false); });
        model.Entity<ClientDeskUser>().ToTable("ClientUsers", t => { t.HasCheckConstraint("CK_ClientUser_Company", "UserType = 0 AND ClientId IS NOT NULL"); t.HasTrigger("TR_ClientUsers_GroupUniqueness"); t.UseSqlOutputClause(false); });
        model.Entity<StaffProject>().HasKey(p => new { p.StaffId, p.ProjectId });
        model.Entity<StaffProject>().HasOne(p => p.Staff).WithMany().HasForeignKey(p => p.StaffId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<StaffProject>().HasOne(p => p.Project).WithMany().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CompanyProject>().Property(p => p.Name).UseCollation("Latin1_General_100_CI_AS");
        model.Entity<CompanyProject>().HasIndex(p => new { p.ClientId, p.Name }).IsUnique();
        model.Entity<CompanyProject>().HasAlternateKey(p => new { p.ClientId, p.Id });
        model.Entity<CompanyProject>().HasOne(p => p.Client).WithMany().HasForeignKey(p => p.ClientId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ticket>().Property(t => t.Status).IsConcurrencyToken();
        model.Entity<Ticket>().Property(t => t.SupportLevel).IsConcurrencyToken();
        model.Entity<Ticket>().Property(t => t.AssigneeId).IsConcurrencyToken();
        model.Entity<Ticket>().HasOne(t => t.Project).WithMany().HasForeignKey(t => new { t.ClientId, t.ProjectId }).HasPrincipalKey(p => new { p.ClientId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ticket>().HasOne(t => t.Client).WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ticket>().HasOne(t => t.Requester).WithMany().HasForeignKey(t => t.RequesterId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ticket>().HasOne(t => t.Assignee).WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ticket>().ToTable("Tickets", t => {
            t.HasCheckConstraint("CK_Ticket_Level", "SupportLevel BETWEEN 1 AND 3");
            t.HasCheckConstraint("CK_Ticket_Severity", "Severity BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_Ticket_Status", "Status BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_Ticket_Priority", "Priority BETWEEN 0 AND 3");
        });
        model.Entity<TicketActivity>().HasOne(a => a.Ticket).WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<TicketActivity>().HasIndex(a => new { a.TicketId, a.CreatedAt });
        model.Entity<TicketComment>().HasOne(c => c.Ticket).WithMany(t => t.Comments).HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class DatabaseState
{
    public int Id { get; set; }
    public DateTime InitializedAt { get; set; }
}


