using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Help_Desk.Models;

public enum AccountRole { Admin, AitsUser, ClientUser }
public enum LoginArea { Client, Aits }
public class DeskAccount
{
    public int Id { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [Required] public string PasswordHash { get; set; } = "";
    public AccountRole Role { get; set; }
    public bool IsApproved { get; set; } = true;
    public int? DeskUserId { get; set; }
    public DeskUser? DeskUser { get; set; }
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public int FailedAttempts { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class LoginViewModel
{
    [EnumDataType(typeof(LoginArea))] public LoginArea Area { get; set; }
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}
public class SignupViewModel
{
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [Required, StringLength(80)] public string Department { get; set; } = "";
    [Range(1, int.MaxValue, ErrorMessage = "Select your company.")] public int ClientId { get; set; }
    [Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    [ValidateNever] public List<Client> Companies { get; set; } = [];
}
public class SetupAdminViewModel
{
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
public class ProvisionAccountViewModel
{
    [Range(1, int.MaxValue)] public int DeskUserId { get; set; }
    [Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    [ValidateNever] public List<DeskUser> Users { get; set; } = [];
}

public class StaffSignupViewModel : SetupAdminViewModel
{
    [Required, StringLength(80)] public string Department { get; set; } = "";
}
