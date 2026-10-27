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

public class AccountController(HelpDeskDbContext db, IPasswordHasher<DeskAccount> hasher, JwtService jwt) : Controller
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
        var account = await db.Accounts.Include(a => a.DeskUser).SingleOrDefaultAsync(a => a.Email == email);
        var valid = false;
        if (account is not null && (account.LockoutUntil is null || account.LockoutUntil <= DateTime.UtcNow))
        {
            var verification = hasher.VerifyHashedPassword(account, account.PasswordHash, model.Password);
            var correctArea = model.Area == LoginArea.Aits
                ? account.Role is AccountRole.Admin or AccountRole.AitsUser
                : account.Role == AccountRole.ClientUser;
            valid = verification != PasswordVerificationResult.Failed && correctArea && account.IsApproved;
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
            ModelState.AddModelError("", "Unable to sign in. Check your email, password and login page. AITS registrations also need admin approval.");
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
            var person = new DeskUser { Name = model.Name.Trim(), Email = model.Email, Department = model.Department.Trim(), ClientId = model.ClientId, UserType = UserType.ClientUser };
            var account = new DeskAccount { Name = person.Name, Email = person.Email, Role = AccountRole.ClientUser, DeskUser = person };
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
            var person = new DeskUser { Name = model.Name.Trim(), Email = model.Email, Department = model.Department.Trim(), UserType = UserType.AitsStaff };
            var account = new DeskAccount { Name = person.Name, Email = person.Email, DeskUser = person, Role = AccountRole.AitsUser, IsApproved = false };
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
        var account = new DeskAccount { Name = model.Name.Trim(), Email = model.Email.Trim(), Role = AccountRole.Admin };
        account.PasswordHash = hasher.HashPassword(account, model.Password);
        db.Accounts.Add(account);
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to create the admin account. The email may already be registered."); return View(model); }
        TempData["Message"] = "Admin account created. Use AITS sign in to continue.";
        return RedirectToAction(nameof(AitsLogin));
    }
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Index() => View(await db.Accounts.AsNoTracking().Include(a => a.DeskUser).OrderBy(a => a.Name).ToListAsync());
    private async Task<List<DeskUser>> AvailableUsers() => await db.Users.AsNoTracking().Where(u => !db.Accounts.Any(a => a.DeskUserId == u.Id)).OrderBy(u => u.Name).ToListAsync();
    [Authorize(Roles = "Admin"), HttpGet]
    public async Task<IActionResult> Create(int? userId) => View(new ProvisionAccountViewModel { DeskUserId = userId ?? 0, Users = await AvailableUsers() });
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ProvisionAccountViewModel model)
    {
        var person = await db.Users.FindAsync(model.DeskUserId);
        if (person is null || await db.Accounts.AnyAsync(a => a.DeskUserId == model.DeskUserId || (person != null && a.Email == person.Email)))
            ModelState.AddModelError("DeskUserId", "Select a directory user without an existing account.");
        if (ModelState.IsValid)
        {
            var account = new DeskAccount { Name = person!.Name, Email = person.Email, DeskUserId = person.Id,
                Role = person.UserType == UserType.AitsStaff ? AccountRole.AitsUser : AccountRole.ClientUser };
            account.PasswordHash = hasher.HashPassword(account, model.Password);
            db.Accounts.Add(account);
            try { await db.SaveChangesAsync(); TempData["Message"] = "Sign-in account created. Share the password with the user securely."; return RedirectToAction(nameof(Index)); }
            catch (DbUpdateException) { ModelState.AddModelError("", "This user or email already has an account. Reload and try again."); }
        }
        model.Users = await AvailableUsers(); return View(model);
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
