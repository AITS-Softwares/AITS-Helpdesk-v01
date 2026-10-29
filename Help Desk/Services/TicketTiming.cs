using Help_Desk.Models;
namespace Help_Desk.Services;
public static class TicketTiming
{
    public static int TargetHours(TicketPriority priority) => priority switch {
        TicketPriority.Low => 72, TicketPriority.Medium => 24, TicketPriority.High => 8, TicketPriority.Urgent => 4, _ => 24
    };
    public static DateTime DueAt(Ticket ticket) => ticket.ResolutionDueAt ?? ticket.CreatedAt.AddHours(TargetHours(ticket.Priority));
    public static string Duration(TimeSpan value) { value = value < TimeSpan.Zero ? TimeSpan.Zero : value; return $"{(int)value.TotalDays}d {value.Hours:D2}h {value.Minutes:D2}m"; }
}
