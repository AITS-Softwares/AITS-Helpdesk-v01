using System.ComponentModel.DataAnnotations;
namespace Help_Desk.Models;

public class TatPolicy
{
    public int Id { get; set; } = 1;
    [Range(1, 8760), Display(Name = "Low priority")] public int LowHours { get; set; } = 72;
    [Range(1, 8760), Display(Name = "Medium priority")] public int MediumHours { get; set; } = 24;
    [Range(1, 8760), Display(Name = "High priority")] public int HighHours { get; set; } = 8;
    [Range(1, 8760), Display(Name = "Urgent priority")] public int UrgentHours { get; set; } = 4;
    public int Hours(TicketPriority priority) => priority switch {
        TicketPriority.Low => LowHours, TicketPriority.High => HighHours,
        TicketPriority.Urgent => UrgentHours, _ => MediumHours
    };
}
