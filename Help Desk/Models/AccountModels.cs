using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Help_Desk.Models;

public enum AccountRole { Admin, AitsUser, ClientUser }
public enum LoginArea { Client, Aits }
public abstract class DeskAccount
{
    public int Id { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [Required] public string PasswordHash { get; set; } = "";
    public AccountRole Role { get; set; }
    public bool IsApproved { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int? DeskUserId { get; set; }
    [NotMapped] public DeskUser? DeskUser => this is InternalAccount internalAccount ? internalAccount.Staff : (this as ClientAccount)?.ClientUser;
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public int FailedAttempts { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class InternalAccount : DeskAccount
{
    public AitsStaffUser? Staff { get; set; }
}
public sealed class ClientAccount : DeskAccount
{
    public ClientAccount() { Role = AccountRole.ClientUser; }
    public ClientDeskUser? ClientUser { get; set; }
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
    [Required, StringLength(128, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    [ValidateNever] public List<Client> Companies { get; set; } = [];
}
public class SetupAdminViewModel
{
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
public class ProvisionAccountViewModel
{
    public bool Staff { get; set; } = true;
    [Range(1, int.MaxValue)] public int DeskUserId { get; set; }
    [Required, StringLength(128, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    [ValidateNever] public List<DeskUser> Users { get; set; } = [];
    [ValidateNever] public DeskUser? SelectedUser { get; set; }
    [ValidateNever] public bool AlreadyEnabled { get; set; }
}

public class StaffSignupViewModel : SetupAdminViewModel
{
    [Required, StringLength(80)] public string Department { get; set; } = "";
}

public class PasswordResetRequest
{
    public int Id { get; set; }
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int? CompletedByAccountId { get; set; }
    public InternalAccount? CompletedBy { get; set; }
}
public class ForgotPasswordViewModel
{
    [Required, EmailAddress, StringLength(160)] public string Email { get; set; } = "";
    [EnumDataType(typeof(LoginArea))] public LoginArea LoginArea { get; set; }
}
public class AdminResetPasswordViewModel
{
    [ValidateNever] public DeskAccount? Account { get; set; }
    [Required, StringLength(128, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
public class PasswordResetRequestItem
{
    public int RequestId { get; set; }
    public int? AccountId { get; set; }
    public string Email { get; set; } = "";
    public DateTime RequestedAt { get; set; }
}

public class AccountDirectoryRow
{
    public DeskUser? User { get; set; }
    public DeskAccount? Account { get; set; }
    public string Name => User?.Name ?? Account?.Name ?? "";
}
