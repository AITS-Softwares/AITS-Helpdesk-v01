using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;
namespace Help_Desk.Services;

public record TatAlertTicket(int Id, string Title, DateTime DueAt);
public record TatAlertSnapshot(int Count, DateTime ServerTime, List<TatAlertTicket> Tickets);
public class TatAlerts(CurrentAccess access)
{
    public async Task<TatAlertSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = access.Tickets.AsNoTracking()
            .Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .Where(t => access.IsAdmin || t.AssigneeId == access.UserId)
            .Select(t => new { t.Id, t.Title, DueAt = t.ResolutionDueAt ?? t.CreatedAt.AddHours(
                t.Priority == TicketPriority.Urgent ? 4 : t.Priority == TicketPriority.High ? 8 : t.Priority == TicketPriority.Low ? 72 : 24) })
            .Where(t => t.DueAt <= now);
        var count = await query.CountAsync(cancellationToken);
        var tickets = await query.OrderBy(t => t.DueAt).ThenBy(t => t.Id).Take(5)
            .Select(t => new TatAlertTicket(t.Id, t.Title, t.DueAt)).ToListAsync(cancellationToken);
        return new(count, now, tickets);
    }
}
