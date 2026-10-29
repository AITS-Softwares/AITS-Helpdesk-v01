using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Text.Json.Serialization;

namespace Help_Desk.Models;

public enum TicketStatus { Open, InProgress, Resolved, Closed }
public enum TicketPriority { Low, Medium, High, Urgent }
public enum SupportLevel { L1 = 1, L2 = 2, L3 = 3 }
public enum TicketSeverity { Low, Medium, High, Critical }
public enum UserType { ClientUser, AitsStaff }
public class Client
{
    public int Id { get; set; }
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [StringLength(40)] public string? Phone { get; set; }
}
public class CompanyProject
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [ValidateNever, JsonIgnore] public Client? Client { get; set; }
}
public class StaffProject
{
    [EnumDataType(typeof(SupportLevel))] public SupportLevel SupportLevel { get; set; } = SupportLevel.L1;
    public int StaffId { get; set; }
    public int ProjectId { get; set; }
    [ValidateNever, JsonIgnore] public AitsStaffUser? Staff { get; set; }
    [ValidateNever, JsonIgnore] public CompanyProject? Project { get; set; }
}
public abstract class DeskUser
{
    [ValidateNever, JsonIgnore] public Client? Client { get; set; }
    [EnumDataType(typeof(UserType))] public UserType UserType { get; set; }
    public int? ClientId { get; set; }
    public int Id { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [Required, StringLength(80)] public string Department { get; set; } = "";
}
public sealed class ClientDeskUser : DeskUser
{
    public ClientDeskUser() { UserType = UserType.ClientUser; }
}
public sealed class AitsStaffUser : DeskUser
{
    public bool IsActive { get; set; } = true;
    public AitsStaffUser() { UserType = UserType.AitsStaff; }
}
public class UserEditViewModel
{
    public int Id { get; set; }
    [EnumDataType(typeof(UserType))] public UserType UserType { get; set; }
    public int? ClientId { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [Required, StringLength(80)] public string Department { get; set; } = "";
    public DeskUser ToEntity()
    {
        DeskUser user = UserType == UserType.AitsStaff ? new AitsStaffUser() : new ClientDeskUser();
        user.Id = Id; user.Name = Name; user.Email = Email; user.Department = Department; user.ClientId = ClientId;
        return user;
    }
    public static UserEditViewModel From(DeskUser user) => new() { Id = user.Id, Name = user.Name, Email = user.Email, Department = user.Department, ClientId = user.ClientId, UserType = user.UserType };
}
public class Ticket
{
    [EnumDataType(typeof(SupportLevel))] public SupportLevel SupportLevel { get; set; } = SupportLevel.L1;
    [EnumDataType(typeof(TicketSeverity))] public TicketSeverity Severity { get; set; } = TicketSeverity.Medium;
    public int? ProjectId { get; set; }
    [ValidateNever, JsonIgnore] public CompanyProject? Project { get; set; }
    [ValidateNever, JsonIgnore] public Client? Client { get; set; }
    [ValidateNever, JsonIgnore] public ClientDeskUser? Requester { get; set; }
    [ValidateNever, JsonIgnore] public AitsStaffUser? Assignee { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Select a client.")] public int ClientId { get; set; }
    public int Id { get; set; }
    [Required, StringLength(160)] public string Title { get; set; } = "";
    [Required, StringLength(5000)] public string Description { get; set; } = "";
    [EnumDataType(typeof(TicketStatus))] public TicketStatus Status { get; set; }
    [EnumDataType(typeof(TicketPriority))] public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    [Range(1, int.MaxValue, ErrorMessage = "Select a requester.")] public int RequesterId { get; set; }
    public int? AssigneeId { get; set; }
    public bool HasCustomTat { get; set; }
    public DateTime? ResolutionDueAt { get; set; }
    public DateTime? WorkStartedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<TicketComment> Comments { get; set; } = [];
}
public class TicketPhoto
{
    public int TicketId { get; set; }
    [ValidateNever, JsonIgnore] public Ticket? Ticket { get; set; }
    [JsonIgnore] public byte[] Data { get; set; } = [];
    [StringLength(30)] public string ContentType { get; set; } = "";
    [StringLength(180)] public string FileName { get; set; } = "";
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
public class TicketComment
{
    [StringLength(80)] public string? AuthorName { get; set; }
    [StringLength(20)] public string? AuthorRole { get; set; }
    public int Id { get; set; }
    public int TicketId { get; set; }
    [ValidateNever, JsonIgnore] public Ticket? Ticket { get; set; }
    [Required, StringLength(3000)] public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class DeskData
{
    public List<CompanyProject> Projects { get; set; } = [];
    public int SchemaVersion { get; set; }
    public List<Client> Clients { get; set; } = [];
    public List<DeskUser> Users { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
}
public class TicketListViewModel
{
    public int? ClientId { get; set; }
    public int? ProjectId { get; set; }
    public List<CompanyProject> Projects { get; set; } = [];
    public List<Client> Clients { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
    public List<DeskUser> Users { get; set; } = [];
    public string? Search { get; set; }
    public TicketStatus? Status { get; set; }
    public TicketPriority? Priority { get; set; }
}

public class StaffProjectsViewModel
{
    [ValidateNever] public DeskUser Staff { get; set; } = new AitsStaffUser();
    [ValidateNever] public List<CompanyProject> Projects { get; set; } = [];
    public List<int> SelectedProjectIds { get; set; } = [];
}

public class ProjectTeamViewModel
{
    public CompanyProject Project { get; set; } = new();
    public List<AitsStaffUser> Staff { get; set; } = [];
    public List<StaffProject> Members { get; set; } = [];
}
public class TicketEscalation
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    [ValidateNever, JsonIgnore] public Ticket? Ticket { get; set; }
    public int StaffId { get; set; }
    [ValidateNever, JsonIgnore] public AitsStaffUser? Staff { get; set; }
    public SupportLevel FromLevel { get; set; }
    public SupportLevel ToLevel { get; set; }
    [Required, StringLength(1000)] public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TicketActivity
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    [ValidateNever, JsonIgnore] public Ticket? Ticket { get; set; }
    [Required, StringLength(80)] public string ActorName { get; set; } = "";
    [Required, StringLength(500)] public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

