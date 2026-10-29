using System.ComponentModel.DataAnnotations;

namespace Help_Desk.Models;

public enum TicketEmailStatus { Pending, Processing, Sent, Retry, Failed, Skipped }

public enum TicketEmailKind { Created, TatOverdue }

public class TicketEmailNotification
{
    public TicketEmailKind Kind { get; set; }
    public DateTime AlertDueAt { get; set; } = DateTime.MinValue;
    public SupportLevel? TargetLevel { get; set; }
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public int? StaffId { get; set; }
    public AitsStaffUser? Staff { get; set; }
    public int CompanyId { get; set; }
    public int ProjectId { get; set; }
    [Required, StringLength(160)] public string RecipientEmail { get; set; } = "";
    public TicketEmailStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? LeaseUntil { get; set; }
    public DateTime? SentAt { get; set; }
    [StringLength(300)] public string? LastError { get; set; }
}

