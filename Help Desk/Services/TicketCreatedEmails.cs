using System.Net.Mail;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Help_Desk.Services;

public class TicketCreatedEmails(HelpDeskDbContext db, IOptionsMonitor<TicketEmailOptions> options)
{
    public async Task<int> QueueAsync(Ticket ticket)
    {
        if (!options.CurrentValue.Enabled || !ticket.ProjectId.HasValue) return 0;
        var targetLevel = ticket.SupportLevel;
        var recipients = await db.StaffProjects.AsNoTracking()
            .Where(m => m.ProjectId == ticket.ProjectId && m.SupportLevel == targetLevel && m.Project!.ClientId == ticket.ClientId && m.Staff!.IsActive &&
                db.InternalAccounts.Any(a => a.DeskUserId == m.StaffId && a.Role == AccountRole.AitsUser && a.IsApproved && a.IsActive && a.Email == m.Staff.Email))
            .Select(m => new { m.StaffId, m.Staff!.Email }).ToListAsync();
        var queued = 0;
        foreach (var recipient in recipients.DistinctBy(r => r.Email.Trim(), StringComparer.OrdinalIgnoreCase)) {
            if (!MailAddress.TryCreate(recipient.Email.Trim(), out var address)) continue;
            if (ticket.Id > 0 && await db.TicketEmailNotifications.AnyAsync(n => n.TicketId == ticket.Id && n.StaffId == recipient.StaffId && n.Kind == TicketEmailKind.Created)) continue;
            db.TicketEmailNotifications.Add(new TicketEmailNotification {
                TargetLevel = targetLevel, Ticket = ticket, StaffId = recipient.StaffId, CompanyId = ticket.ClientId,
                ProjectId = ticket.ProjectId.Value, RecipientEmail = address.Address
            });
            queued++;
        }
        return queued;
    }
}

