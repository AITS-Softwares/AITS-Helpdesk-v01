using System.Data;
using System.Net.Mail;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;
namespace Help_Desk.Services;

public class TatOverdueEmails(HelpDeskDbContext db)
{
    public async Task QueueAsync(CancellationToken cancellationToken)
    {
        // Serialize queue creation across app instances; delivery still uses the existing leased outbox.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'Aits.TatOverdueQueue', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, 'TAT queue is busy.', 1;", cancellationToken);
        var now = DateTime.UtcNow;
        var tickets = await db.Tickets.AsNoTracking().Where(t => t.AssigneeId != null && t.ProjectId != null &&
            t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed &&
            db.StaffProjects.Any(m => m.ProjectId == t.ProjectId && m.StaffId == t.AssigneeId && m.Project!.ClientId == t.ClientId && m.Staff!.IsActive &&
                db.InternalAccounts.Any(a => a.DeskUserId == m.StaffId && a.Role == AccountRole.AitsUser && a.IsActive && a.IsApproved && a.Email == m.Staff.Email)))
            .Select(t => new { Ticket = t, DueAt = t.ResolutionDueAt ?? t.CreatedAt.AddHours(
                t.Priority == TicketPriority.Urgent ? 4 : t.Priority == TicketPriority.High ? 8 : t.Priority == TicketPriority.Low ? 72 : 24) })
            .Where(t => t.DueAt <= now && !db.TicketEmailNotifications.Any(n => n.TicketId == t.Ticket.Id && n.StaffId == t.Ticket.AssigneeId && n.Kind == TicketEmailKind.TatOverdue && n.AlertDueAt == t.DueAt))
            .OrderBy(t => t.DueAt).Take(100).ToListAsync(cancellationToken);
        foreach (var row in tickets) {
            var ticket = row.Ticket;
            var recipient = await db.StaffProjects.AsNoTracking()
                .Where(m => m.ProjectId == ticket.ProjectId && m.StaffId == ticket.AssigneeId && m.Project!.ClientId == ticket.ClientId && m.Staff!.IsActive &&
                    db.InternalAccounts.Any(a => a.DeskUserId == m.StaffId && a.Role == AccountRole.AitsUser && a.IsActive && a.IsApproved && a.Email == m.Staff.Email))
                .Select(m => m.Staff!.Email).SingleOrDefaultAsync(cancellationToken);
            if (recipient is null || !MailAddress.TryCreate(recipient, out var address)) continue;
            db.TicketEmailNotifications.Add(new TicketEmailNotification {
                Kind = TicketEmailKind.TatOverdue, AlertDueAt = row.DueAt, TicketId = ticket.Id,
                StaffId = ticket.AssigneeId, CompanyId = ticket.ClientId, ProjectId = ticket.ProjectId!.Value,
                RecipientEmail = address.Address
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

