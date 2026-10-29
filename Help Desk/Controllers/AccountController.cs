using System.Net;
using System.Security.Claims;
using Help_Desk.Data;
using Help_Desk.Models;
using Help_Desk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Controllers;

public class AccountController(HelpDeskDbContext db, IPasswordHasher<DeskAccount> hasher, JwtService jwt, CurrentAccess access) : Controller
{
    private bool IsLocal => HttpContext.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
    private async Task LoginOptions() => ViewBag.CanSetup = IsLocal && !await db.Accounts.AnyAsync(a => a.Role == AccountRole.Admin);

    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> Login(string? returnUrl)
    {
        await LoginOptions(); return View(new LoginViewModel { ReturnUrl = returnUrl });
    }
    // Admin and staff share the AITS sign-in; clients use the client sign-in.
    [AllowAnonymous, HttpGet]
    public IActionResult AdminLogin(string? returnUrl) => RedirectToAction(nameof(AitsLogin), new { returnUrl });
    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> AitsLogin(string? returnUrl)
    {
        await LoginOptions(); return View("Login", new LoginViewModel { Area = LoginArea.Aits, ReturnUrl = returnUrl });
    }
    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) { await LoginOptions(); return View(model); }
        var email = model.Email.Trim();
        var account = await db.AccountsWithUsers.SingleOrDefaultAsync(a => a.Email == email);
        var valid = false;
        if (account is not null && (account.LockoutUntil is null || account.LockoutUntil <= DateTime.UtcNow))
        {
            var verification = hasher.VerifyHashedPassword(account, account.PasswordHash, model.Password);
            var correctArea = model.Area == LoginArea.Aits
                ? account.Role is AccountRole.Admin or AccountRole.AitsUser
                : account.Role == AccountRole.ClientUser;
            valid = verification != PasswordVerificationResult.Failed && correctArea && account.IsApproved && account.IsActive &&
                (account is not InternalAccount || account.Role == AccountRole.Admin || account.DeskUser is AitsStaffUser { IsActive: true });
            if (valid)
            {
                account.FailedAttempts = 0; account.LockoutUntil = null;
                if (verification == PasswordVerificationResult.SuccessRehashNeeded) account.PasswordHash = hasher.HashPassword(account, model.Password);
            }
            else
            {
                account.FailedAttempts++;
                if (account.FailedAttempts >= 5) { account.LockoutUntil = DateTime.UtcNow.AddMinutes(15); account.FailedAttempts = 0; }
            }
            await db.SaveChangesAsync();
        }
        if (!valid)
        {
            ModelState.AddModelError("", "Unable to sign in. Check your email, password and login page. AITS accounts must be active and approved.");
            await LoginOptions(); return View(model);
        }
        jwt.SetCookie(HttpContext, account!);
        return !string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl)
            ? LocalRedirect(model.ReturnUrl) : RedirectToAction("Index", "Home");
    }
    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> Signup()
    {
        return View(new SignupViewModel { Companies = await db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync() });
    }
    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("auth")]
    public async Task<IActionResult> Signup(SignupViewModel model)
    {
        model.Email = model.Email?.Trim() ?? "";
        if (!await db.Companies.AnyAsync(c => c.Id == model.ClientId)) ModelState.AddModelError("ClientId", "Select an existing company. Only an AITS admin can add companies.");
        if (await db.Accounts.AnyAsync(a => a.Email == model.Email) || await db.Users.AnyAsync(u => u.Email == model.Email))
            ModelState.AddModelError("Email", "This email is already registered or has a directory record. Sign in or contact your AITS admin to enable access.");
        if (ModelState.IsValid)
        {
            var person = new ClientDeskUser { Name = model.Name.Trim(), Email = model.Email, Department = model.Department.Trim(), ClientId = model.ClientId, UserType = UserType.ClientUser };
            var account = new ClientAccount { Name = person.Name, Email = person.Email, Role = AccountRole.ClientUser, ClientUser = person };
            account.PasswordHash = hasher.HashPassword(account, model.Password);
            db.Accounts.Add(account);
            try {
                await db.SaveChangesAsync();
                TempData["Message"] = "Your client account is ready. Sign in to raise a ticket.";
                return RedirectToAction(nameof(Login));
            }
            catch (DbUpdateException) { ModelState.AddModelError("", "Unable to create this account. The email may already be registered or the company may have changed."); }
        }
        model.Companies = await db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        return View(model);
    }
    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> AitsSignup()
    {
        await LoginOptions(); return View(new StaffSignupViewModel());
    }
    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("auth")]
    public async Task<IActionResult> AitsSignup(StaffSignupViewModel model)
    {
        model.Email = model.Email?.Trim() ?? "";
        if (await db.Accounts.AnyAsync(a => a.Email == model.Email) || await db.Users.AnyAsync(u => u.Email == model.Email))
            ModelState.AddModelError("Email", "This email is already registered or has a directory record. Contact your AITS admin to enable access.");
        if (ModelState.IsValid)
        {
            var person = new AitsStaffUser { Name = model.Name.Trim(), Email = model.Email, Department = model.Department.Trim(), UserType = UserType.AitsStaff };
            var account = new InternalAccount { Name = person.Name, Email = person.Email, Staff = person, Role = AccountRole.AitsUser, IsApproved = false };
            account.PasswordHash = hasher.HashPassword(account, model.Password);
            db.Accounts.Add(account);
            try {
                await db.SaveChangesAsync();
                TempData["Message"] = "AITS registration received. An admin must approve your account before you can sign in.";
                return RedirectToAction(nameof(AitsLogin));
            }
            catch (DbUpdateException) { ModelState.AddModelError("", "Unable to register. This email may already be in use."); }
        }
        await LoginOptions(); return View(model);
    }
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id)
    {
        var account = await db.Accounts.FindAsync(id);
        if (account is null || account.Role != AccountRole.AitsUser) return NotFound();
        account.IsApproved = true;
        account.SecurityStamp = Guid.NewGuid();
        await db.SaveChangesAsync();
        TempData["Message"] = "AITS staff account approved. The user can now sign in.";
        return RedirectToAction(nameof(Index));
    }
    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> Setup()
    {
        if (!IsLocal || await db.Accounts.AnyAsync(a => a.Role == AccountRole.Admin)) return NotFound();
        return View(new SetupAdminViewModel());
    }
    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("auth")]
    public async Task<IActionResult> Setup(SetupAdminViewModel model)
    {
        if (!IsLocal || await db.Accounts.AnyAsync(a => a.Role == AccountRole.Admin)) return NotFound();
        if (!ModelState.IsValid) return View(model);
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Serialize first-admin setup even if two requests arrive together.
        await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'Aits.FirstAdmin', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000; IF @result < 0 THROW 51000, 'Admin setup is busy. Try again.', 1;");
        if (await db.Accounts.AnyAsync(a => a.Role == AccountRole.Admin)) return NotFound();
        var account = new InternalAccount { Name = model.Name.Trim(), Email = model.Email.Trim(), Role = AccountRole.Admin };
        account.PasswordHash = hasher.HashPassword(account, model.Password);
        db.Accounts.Add(account);
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to create the admin account. The email may already be registered."); return View(model); }
        TempData["Message"] = "Admin account created. Use AITS sign in to continue.";
        return RedirectToAction(nameof(AitsLogin));
    }
    [Authorize(Roles = "Admin"), HttpGet]
    public IActionResult CreateAdmin() => View(new SetupAdminViewModel());

    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAdmin(SetupAdminViewModel model)
    {
        model.Name = model.Name?.Trim() ?? "";
        model.Email = model.Email?.Trim() ?? "";
        if (await db.Accounts.AnyAsync(a => a.Email == model.Email) || await db.Users.AnyAsync(u => u.Email == model.Email))
            ModelState.AddModelError("Email", "This email is already registered or has a directory record.");
        if (!ModelState.IsValid) return View(model);

        var account = new InternalAccount { Name = model.Name, Email = model.Email, Role = AccountRole.Admin };
        account.PasswordHash = hasher.HashPassword(account, model.Password);
        db.Accounts.Add(account);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to create the administrator. The email may already be registered."); return View(model); }
        TempData["Message"] = "Administrator account created. They can sign in at AITS sign in.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Index(bool clients = false, int? clientId = null)
    {
        ViewBag.ClientAccounts = clients;
        ViewBag.ClientId = clientId;
        ViewBag.Companies = await db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        ViewBag.DirectoryUsers = await db.Users.AsNoTracking().Include(u => u.Client)
            .Where(u => u.UserType == (clients ? UserType.ClientUser : UserType.AitsStaff) && (!clients || !clientId.HasValue || u.ClientId == clientId))
            .OrderBy(u => u.Name).ToListAsync();
        var memberships = await db.StaffProjects.AsNoTracking().Include(m => m.Project).ThenInclude(p => p!.Client).ToListAsync();
        ViewBag.StaffProjects = memberships.GroupBy(m => m.StaffId).ToDictionary(g => g.Key, g => g.Select(m => m.Project!.Client!.Name + " / " + m.Project.Name).ToList());

        var requests = new List<PasswordResetRequestItem>();
        if (User.IsInRole("Admin")) {
            var accounts = await db.Accounts.AsNoTracking().Select(a => new { a.Id, a.Email }).ToListAsync();
            var pending = await db.PasswordResetRequests.AsNoTracking().Where(r => r.CompletedAt == null).OrderBy(r => r.RequestedAt).ToListAsync();
            requests = pending.Select(r => new PasswordResetRequestItem { RequestId = r.Id, Email = r.Email, RequestedAt = r.RequestedAt, AccountId = accounts.FirstOrDefault(a => string.Equals(a.Email, r.Email, StringComparison.OrdinalIgnoreCase))?.Id }).ToList();
        }
        ViewBag.PasswordResetRequests = requests;
        return View(await db.AccountsWithUsers.AsNoTracking().Where(a => clients ? a is ClientAccount : a is InternalAccount).OrderBy(a => a.Name).ToListAsync());
    }
    [AllowAnonymous, HttpGet]
    public IActionResult ForgotPassword(LoginArea loginArea = LoginArea.Client) => View(new ForgotPasswordViewModel { LoginArea = loginArea });
    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        model.Email = model.Email?.Trim() ?? "";
        if (!ModelState.IsValid) return View(model);
        if (await db.Accounts.AnyAsync(a => a.Email == model.Email) && !await db.PasswordResetRequests.AnyAsync(r => r.Email == model.Email && r.CompletedAt == null)) {
            db.PasswordResetRequests.Add(new PasswordResetRequest { Email = model.Email });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* A concurrent request may already be pending. Keep the public response identical. */ }
        }
        TempData["Message"] = "If an account matches this email, your reset request is available for Admin review. Contact your Admin to verify your identity and arrange a new password.";
        return RedirectToAction(nameof(ForgotPassword), new { loginArea = model.LoginArea });
    }
    [Authorize(Roles = "Admin"), HttpGet]
    public async Task<IActionResult> ResetPassword(int id)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
        return account is null ? NotFound() : View(new AdminResetPasswordViewModel { Account = account });
    }
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id, [Bind("Password,ConfirmPassword")] AdminResetPasswordViewModel model)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id);
        if (account is null) return NotFound();
        model.Account = account;
        if (!ModelState.IsValid) return View(model);
        account.PasswordHash = hasher.HashPassword(account, model.Password);
        account.SecurityStamp = Guid.NewGuid();
        account.FailedAttempts = 0; account.LockoutUntil = null;
        var requests = await db.PasswordResetRequests.Where(r => r.Email == account.Email && r.CompletedAt == null).ToListAsync();
        var adminId = int.Parse(User.FindFirstValue("sub")!);
        foreach (var request in requests) { request.CompletedAt = DateTime.UtcNow; request.CompletedByAccountId = adminId; }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "The password could not be reset. Reload and try again."); return View(model); }
        if (account.Id == adminId) {
            Response.Cookies.Delete(JwtService.CookieName, new CookieOptions { Path = "/", Secure = Request.IsHttps, SameSite = SameSiteMode.Strict });
            TempData["Message"] = "Your password was reset. Sign in with your new password.";
            return RedirectToAction(nameof(AitsLogin));
        }
        TempData["Message"] = "Password reset. Existing sessions were revoked. Share the new password securely with the verified user.";
        return RedirectToAction(nameof(Index), new { clients = account is ClientAccount });
    }
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DismissResetRequest(int id)
    {
        var request = await db.PasswordResetRequests.SingleOrDefaultAsync(r => r.Id == id && r.CompletedAt == null);
        if (request is null) return NotFound();
        request.CompletedAt = DateTime.UtcNow;
        request.CompletedByAccountId = int.Parse(User.FindFirstValue("sub")!);
        await db.SaveChangesAsync();
        TempData["Message"] = "Reset request dismissed. The account password was not changed.";
        return RedirectToAction(nameof(Index));
    }
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(int id, [FromForm] bool? isActive)
    {
        if (!ModelState.IsValid || !isActive.HasValue) return BadRequest("Choose Activate or Deactivate. No account status was changed.");
        var account = await db.AccountsWithUsers.SingleOrDefaultAsync(a => a.Id == id);
        if (account is null) return NotFound();
        if (account.Role == AccountRole.Admin) return BadRequest("Administrator accounts cannot be deactivated here.");
        account.IsActive = isActive.Value;
        account.SecurityStamp = Guid.NewGuid();
        if (account.DeskUser is AitsStaffUser staff) staff.IsActive = isActive.Value;
        try {
            await db.SaveChangesAsync();
            TempData["Message"] = isActive.Value ? "Account activated. The user can sign in if approved." : "Account deactivated. Sign-in and existing sessions are blocked.";
        } catch (DbUpdateException) { TempData["Error"] = "Account status could not be saved. Refresh and try again."; }
        return RedirectToAction(nameof(Index), new { clients = account is ClientAccount });
    }
    private async Task<List<DeskUser>> AvailableUsers(bool staff) => await db.Users.AsNoTracking().Where(u => (access.IsAdmin || (u is ClientDeskUser && access.TicketCompanies.Any(c => c.Id == u.ClientId))) && u.UserType == (staff ? UserType.AitsStaff : UserType.ClientUser) && !db.Accounts.Any(a => a.DeskUserId == u.Id)).OrderBy(u => u.Name).ToListAsync();
    [Authorize(Roles = "Admin,AitsUser"), HttpGet]
    public async Task<IActionResult> Create(int? userId, bool staff = true)
    {
        if (!access.IsAdmin) staff = false;
        if (userId.HasValue) {
            var person = await db.Users.AsNoTracking().Include(u => u.Client).SingleOrDefaultAsync(u => u.Id == userId);
            if (person is null || (!access.IsAdmin && (person is not ClientDeskUser || !await access.TicketCompanies.AnyAsync(c => c.Id == person.ClientId)))) return NotFound();
            return View(new ProvisionAccountViewModel {
                Staff = person is AitsStaffUser, DeskUserId = person.Id, SelectedUser = person,
                AlreadyEnabled = await db.Accounts.AnyAsync(a => a.DeskUserId == person.Id || a.Email == person.Email)
            });
        }
        return View(new ProvisionAccountViewModel { Staff = staff, Users = await AvailableUsers(staff) });
    }
    [Authorize(Roles = "Admin,AitsUser"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromQuery] int? userId, [Bind("DeskUserId,Password,ConfirmPassword")] ProvisionAccountViewModel model)
    {
        // A direct Enable sign-in link fixes the target in the URL. Ignore a changed hidden field.
        if (userId.HasValue) { model.DeskUserId = userId.Value; ModelState.Remove(nameof(model.DeskUserId)); }
        var person = await db.Users.Include(u => u.Client).SingleOrDefaultAsync(u => u.Id == model.DeskUserId);
        if (person is null || (!access.IsAdmin && (person is not ClientDeskUser || !await access.TicketCompanies.AnyAsync(c => c.Id == person.ClientId)))) return NotFound();
        model.SelectedUser = person;
        model.Staff = person is AitsStaffUser;
        ModelState.Remove(nameof(model.Staff));
        model.AlreadyEnabled = await db.Accounts.AnyAsync(a => a.DeskUserId == person.Id || a.Email == person.Email);
        if (model.AlreadyEnabled) ModelState.AddModelError("", "Sign-in is already enabled for this user or email.");
        if (ModelState.IsValid)
        {
            DeskAccount account = person is AitsStaffUser ? new InternalAccount { Role = AccountRole.AitsUser } : new ClientAccount();
            account.Name = person.Name; account.Email = person.Email; account.DeskUserId = person.Id;
            account.IsActive = person is not AitsStaffUser staffUser || staffUser.IsActive;
            account.PasswordHash = hasher.HashPassword(account, model.Password);
            db.Accounts.Add(account);
            try { await db.SaveChangesAsync(); TempData["Message"] = "Sign-in account created. Share the password with the user securely."; return access.IsAdmin ? RedirectToAction(nameof(Index), new { clients = person is ClientDeskUser }) : RedirectToAction("ClientUsers", "MyWork", new { companyId = person.ClientId }); }
            catch (DbUpdateException) { ModelState.AddModelError("", "This user or email already has an account. Reload and try again."); }
        }
        return View(model);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        if (int.TryParse(User.FindFirstValue("sub"), out var id) && await db.Accounts.FindAsync(id) is { } account)
        {
            account.SecurityStamp = Guid.NewGuid(); await db.SaveChangesAsync();
        }
        Response.Cookies.Delete(JwtService.CookieName, new CookieOptions { Path = "/", Secure = Request.IsHttps, SameSite = SameSiteMode.Strict });
        return RedirectToAction(nameof(Login));
    }
    [HttpGet]
    public IActionResult Me() => Json(new { name = User.Identity?.Name, role = User.FindFirstValue("role"), userId = User.FindFirstValue("desk_user_id"), companyId = User.FindFirstValue("company_id") });
    [AllowAnonymous]
    public IActionResult Denied() { Response.StatusCode = 403; return View(); }
}
