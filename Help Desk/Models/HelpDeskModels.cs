using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Text.Json.Serialization;

namespace Help_Desk.Models;

public enum TicketStatus { Open, InProgress, Resolved, Closed }
public enum TicketPriority { Low, Medium, High, Urgent }
public enum UserType { ClientUser, AitsStaff }
public class Client
{
    public int Id { get; set; }
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [StringLength(40)] public string? Phone { get; set; }
}
public class DeskUser
{
    [ValidateNever, JsonIgnore] public Client? Client { get; set; }
    [EnumDataType(typeof(UserType))] public UserType UserType { get; set; }
    public int? ClientId { get; set; }
    public int Id { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [Required, StringLength(80)] public string Department { get; set; } = "";
}
public class Ticket
{
    [ValidateNever, JsonIgnore] public Client? Client { get; set; }
    [ValidateNever, JsonIgnore] public DeskUser? Requester { get; set; }
    [ValidateNever, JsonIgnore] public DeskUser? Assignee { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Select a client.")] public int ClientId { get; set; }
    public int Id { get; set; }
    [Required, StringLength(160)] public string Title { get; set; } = "";
    [Required, StringLength(5000)] public string Description { get; set; } = "";
    [EnumDataType(typeof(TicketStatus))] public TicketStatus Status { get; set; }
    [EnumDataType(typeof(TicketPriority))] public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    [Range(1, int.MaxValue, ErrorMessage = "Select a requester.")] public int RequesterId { get; set; }
    public int? AssigneeId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<TicketComment> Comments { get; set; } = [];
}
public class TicketComment
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    [ValidateNever, JsonIgnore] public Ticket? Ticket { get; set; }
    [Required, StringLength(3000)] public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class DeskData
{
    public int SchemaVersion { get; set; }
    public List<Client> Clients { get; set; } = [];
    public List<DeskUser> Users { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
}
public class TicketListViewModel
{
    public int? ClientId { get; set; }
    public List<Client> Clients { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
    public List<DeskUser> Users { get; set; } = [];
    public string? Search { get; set; }
    public TicketStatus? Status { get; set; }
    public TicketPriority? Priority { get; set; }
}
