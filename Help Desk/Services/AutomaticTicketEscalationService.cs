using System.Data;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Services;

public sealed class AutomaticTicketEscalationService(HelpDeskDbContext db, TicketCreatedEmails emails, TicketTatPolicy policy, ILogger<AutomaticTicketEscalationService> logger)
{
    public async Task QueueAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'Aits.UrgentResponseEscalation', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, 'Escalation queue is busy.', 1;", cancellationToken);
        var settings = await policy.ReadAsync();
        var now = DateTime.UtcNow;
        var tickets = await db.Tickets.Include(t => t.Comments)
            .Where(t => t.Priority == TicketPriority.Urgent && (t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress) &&
                (t.SupportLevel == SupportLevel.L1 || t.SupportLevel == SupportLevel.L2) && t.ProjectId != null)
            .OrderBy(t => t.Id).Take(500).ToListAsync(cancellationToken);
        foreach (var ticket in tickets)
        {
            var windowHours = ticket.SupportLevel == SupportLevel.L1 ? settings.L1ResponseHours : settings.L2ResponseHours;
            var latestCustomer = ticket.Comments.Where(c => c.AuthorRole == "Customer user" || c.AuthorRole == "Client user").Select(c => (DateTime?)c.CreatedAt).Max();
            var latestStaff = ticket.Comments.Where(c => c.AuthorRole == "AITS staff" || c.AuthorRole == "Admin").Select(c => (DateTime?)c.CreatedAt).Max();
            var pendingSince = latestCustomer.HasValue && latestCustomer.Value > ticket.SupportLevelStartedAt ? latestCustomer.Value : ticket.SupportLevelStartedAt;
            if (latestStaff.HasValue && latestStaff.Value >= pendingSince) continue;
            if (pendingSince.AddHours(windowHours) > now) continue;
            var next = ticket.SupportLevel == SupportLevel.L1 ? SupportLevel.L2 : SupportLevel.L3;
            var hasTeam = await db.StaffProjects.AnyAsync(m => m.ProjectId == ticket.ProjectId && m.SupportLevel == next && m.Project!.ClientId == ticket.ClientId && m.Staff!.IsActive &&
                db.InternalAccounts.Any(a => a.DeskUserId == m.StaffId && a.Role == AccountRole.AitsUser && a.IsApproved && a.IsActive && a.Email == m.Staff.Email), cancellationToken);
            if (!hasTeam) { logger.LogWarning("Urgent ticket {TicketId} was not escalated because no active approved {SupportLevel} agent is assigned to the project.", ticket.Id, next); continue; }
            var old = ticket.SupportLevel;
            ticket.SupportLevel = next;
            ticket.AssigneeId = null;
            ticket.SupportLevelStartedAt = now;
            ticket.UpdatedAt = now;
            db.TicketActivities.Add(new TicketActivity { Ticket = ticket, ActorName = "AITS Helpdesk", Message = $"Automatically escalated from {old} to {next} after no staff response for {windowHours} hours." });
            await emails.QueueAsync(ticket, TicketEmailKind.AutoEscalated);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
