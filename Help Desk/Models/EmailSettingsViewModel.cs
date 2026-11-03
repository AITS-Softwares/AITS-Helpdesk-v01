using System.ComponentModel.DataAnnotations;
namespace Help_Desk.Models;
public class EmailSettingsViewModel
{
    public bool Enabled { get; set; }
    public bool IncomingEnabled { get; set; }
    [StringLength(253)] public string IncomingHost { get; set; } = "imap.gmail.com";
    [Range(1,65535)] public int IncomingPort { get; set; } = 993;
    [EmailAddress, StringLength(160)] public string? SupportAddress { get; set; }
    [Required, EmailAddress, StringLength(160)] public string UserName { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string FromAddress { get; set; } = "";
    [Required, StringLength(100)] public string FromName { get; set; } = "AITS Helpdesk";
    [Required, StringLength(253)] public string Host { get; set; } = "smtp.gmail.com";
    [Range(1,65535)] public int Port { get; set; } = 587;
    [DataType(DataType.Password), StringLength(256)] public string? Password { get; set; }
    [StringLength(500)] public string? PublicBaseUrl { get; set; }
    public bool HasPassword { get; set; }
}
