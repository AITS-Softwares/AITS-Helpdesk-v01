namespace Help_Desk.Models;

public sealed record UserTicketSummary(int Id, string Title, TicketStatus Status, DateTime UpdatedAt);
