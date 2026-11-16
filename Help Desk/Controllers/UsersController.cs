using Microsoft.AspNetCore.Authorization;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Controllers;

[Authorize(Roles = "Admin")]
public class UsersController(HelpDeskDbContext db) : Controller
{
    private async Task LoadCompanies() => ViewBag.Clients = await db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    public IActionResult Index(int? clientId, bool staff = false) => RedirectToAction(staff ? nameof(AitsStaff) : nameof(ClientUsers), new { clientId = staff ? null : clientId });
    public IActionResult ClientUsers(int? clientId) => RedirectToAction("Index", "Clients");
    public IActionResult AitsStaff() => RedirectToAction("Index", "Account");
    private async Task<IActionResult> RenderDirectory(int? clientId, bool staff)
    {
        ViewData["StaffSection"] = staff;
        await LoadCompanies();
        ViewBag.IsStaff = staff;
        ViewBag.EnabledUserIds = (await db.Accounts.Where(a => a.DeskUserId != null).Select(a => a.DeskUserId!.Value).ToListAsync()).ToHashSet();
        ViewBag.ClientId = staff ? null : clientId;
        var users = db.Users.AsNoTracking().Where(u => u.UserType == (staff ? UserType.AitsStaff : UserType.ClientUser));
        if (!staff && clientId.HasValue) users = users.Where(u => u.ClientId == clientId);
        if (staff) {
            var assignments = await db.StaffProjects.AsNoTracking().Include(m => m.Project).ThenInclude(p => p!.Client).ToListAsync();
            ViewBag.StaffProjects = assignments.GroupBy(m => m.StaffId).ToDictionary(g => g.Key, g => g.Select(m => m.Project!.Client!.Name).Distinct().OrderBy(name => name).ToList());
        }
        return View("Index", await users.OrderBy(u => u.Name).ToListAsync());
    }
    public async Task<IActionResult> Create(int? clientId, bool staff = false)
    {
        if (!staff) return RedirectToAction("Index", "Clients");
        ViewData["StaffSection"] = staff;
        await LoadCompanies(); return View("Edit", new UserEditViewModel { ClientId = staff ? null : clientId, UserType = staff ? UserType.AitsStaff : UserType.ClientUser });
    }
    public async Task<IActionResult> Edit(int id)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        if (user is not AitsStaffUser) return NotFound();
        ViewData["StaffSection"] = user is AitsStaffUser;
        await LoadCompanies(); return View(UserEditViewModel.From(user));
    }
    public async Task<IActionResult> Details(int id)
    {
        var user = await db.Users.AsNoTracking().Include(person => person.Client).SingleOrDefaultAsync(person => person.Id == id);
        if (user is not AitsStaffUser) return NotFound();
        ViewBag.SignInAccount = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.DeskUserId == id);
        var memberships = await db.StaffProjects.AsNoTracking().Include(membership => membership.Project)
            .Where(membership => membership.StaffId == id).ToListAsync();
        ViewBag.AssignedCustomerIds = memberships.Where(membership => membership.Project is not null)
            .Select(membership => membership.Project!.ClientId).Distinct().ToHashSet();
        ViewBag.AssignmentCustomers = await db.Companies.AsNoTracking().OrderBy(customer => customer.Name).ToListAsync();
        var tickets = db.Tickets.AsNoTracking().Where(ticket => user.UserType == UserType.AitsStaff
            ? ticket.AssigneeId == id : ticket.RequesterId == id);
        ViewBag.Tickets = await tickets.OrderByDescending(ticket => ticket.UpdatedAt)
            .Select(ticket => new UserTicketSummary(ticket.Id, ticket.Title, ticket.Status, ticket.UpdatedAt)).ToListAsync();
        return View(user);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([Bind("Id,Name,Email,Department,UserType,ClientId")] UserEditViewModel user)
    {
        if (user.Id == 0 && user.UserType != UserType.AitsStaff) return RedirectToAction("Index", "Clients");
        ViewData["StaffSection"] = user.UserType == UserType.AitsStaff;
        var existing = user.Id == 0 ? null : await db.Users.FindAsync(user.Id);
        if (user.Id != 0 && existing is null) return NotFound();
        if (existing is not null && existing is not AitsStaffUser) return NotFound();
        if (existing is not null && existing.UserType != user.UserType)
            ModelState.AddModelError("UserType", "Account groups are stored separately. Create a new user in the other group instead of changing this user's type.");
        if (existing is not null) { user.ClientId = existing.ClientId; ModelState.Remove("ClientId"); }
        user.Name = user.Name?.Trim() ?? ""; user.Email = user.Email?.Trim() ?? ""; user.Department = user.Department?.Trim() ?? "";
        if (await db.Users.AnyAsync(u => u.Id != user.Id && u.Email == user.Email)) ModelState.AddModelError("Email", "This email address is already in use.");
        if (user.UserType == UserType.AitsStaff) { user.ClientId = null; ModelState.Remove("ClientId"); }
        else if (!await db.Companies.AnyAsync(c => c.Id == user.ClientId)) ModelState.AddModelError("ClientId", "Select a company for this user.");
        if (await db.Tickets.AnyAsync(t => t.RequesterId == user.Id && t.ClientId != user.ClientId)) ModelState.AddModelError("ClientId", "This user has client tickets. Their company membership cannot be changed.");
        if (user.UserType != UserType.AitsStaff && await db.Tickets.AnyAsync(t => t.AssigneeId == user.Id)) ModelState.AddModelError("UserType", "Reassign this staff member's tickets before changing their type.");
        var account = existing is null ? null : await db.Accounts.SingleOrDefaultAsync(a => a.DeskUserId == user.Id);
        var accountId = account?.Id ?? 0;
        if (await db.Accounts.AnyAsync(a => a.Email == user.Email && a.Id != accountId)) ModelState.AddModelError("Email", "This email is already used by a sign-in account.");
        if (!ModelState.IsValid) { await LoadCompanies(); return View("Edit", user); }
        if (user.UserType != UserType.AitsStaff && existing is not null)
            db.StaffProjects.RemoveRange(db.StaffProjects.Where(m => m.StaffId == user.Id));
        if (account is not null) {
            account.Name = user.Name; account.Email = user.Email;
            account.Role = user.UserType == UserType.AitsStaff ? AccountRole.AitsUser : AccountRole.ClientUser;
            account.SecurityStamp = Guid.NewGuid();
        }
        if (existing is null) db.Users.Add(user.ToEntity());
        else { existing.Name = user.Name; existing.Email = user.Email; existing.Department = user.Department; existing.UserType = user.UserType; existing.ClientId = user.ClientId; }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to save. The email may be in use or the company may have changed. Reload and try again."); await LoadCompanies(); return View("Edit", user); }
        TempData["Message"] = "User saved successfully.";
        return RedirectToAction(user.UserType == UserType.AitsStaff ? nameof(AitsStaff) : nameof(ClientUsers), new { clientId = user.ClientId });
    }
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(int id, [FromForm] bool? isActive)
    {
        if (!ModelState.IsValid || !isActive.HasValue) return BadRequest("Choose Activate or Deactivate. No staff status was changed.");
        var staff = await db.AitsStaff.SingleOrDefaultAsync(s => s.Id == id);
        if (staff is null) return NotFound();
        staff.IsActive = isActive.Value;
        var account = await db.InternalAccounts.SingleOrDefaultAsync(a => a.DeskUserId == id);
        if (account is not null) { account.IsActive = isActive.Value; account.SecurityStamp = Guid.NewGuid(); }
        try {
            await db.SaveChangesAsync();
            TempData["Message"] = isActive.Value ? "Staff activated. They can sign in if their account is approved." : "Staff deactivated. Sign-in and existing sessions are blocked.";
        }
        catch (DbUpdateException) { TempData["Error"] = "Staff status could not be changed. Reload and try again."; }
        return RedirectToAction(nameof(AitsStaff));
    }
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AssignCustomers(int id)
    {
        ViewData["StaffSection"] = true;
        var staff = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.UserType == UserType.AitsStaff);
        if (staff is null) return NotFound();
        var model = new StaffProjectsViewModel {
            Staff = staff,
            Customers = await db.Companies.AsNoTracking().OrderBy(customer => customer.Name).ToListAsync()
        };
        var memberships = await db.StaffProjects.Where(m => m.StaffId == id).Include(m => m.Project).ToListAsync();
        model.SelectedClientIds = memberships.Where(m => m.Project is not null).Select(m => m.Project!.ClientId).Distinct().ToList();
        return View(model);
    }
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignCustomers(int id, [Bind("SelectedClientIds")] StaffProjectsViewModel model, bool returnToDetails = false)
    {
        ViewData["StaffSection"] = true;
        var staff = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.UserType == UserType.AitsStaff);
        if (staff is null) return NotFound();
        var selectedCustomerIds = model.SelectedClientIds.Distinct().ToList();
        if (selectedCustomerIds.Count != model.SelectedClientIds.Count)
            ModelState.AddModelError(nameof(model.SelectedClientIds), "A customer can only be selected once.");
        var customers = await db.Companies.AsNoTracking().OrderBy(customer => customer.Name).ToListAsync();
        if (selectedCustomerIds.Any(customerId => customers.All(customer => customer.Id != customerId)))
            ModelState.AddModelError("", "A selected customer no longer exists. Reload and try again.");
        if (ModelState.IsValid) {
            var current = await db.StaffProjects.Where(m => m.StaffId == id).ToListAsync();
            var projects = await db.Projects.AsNoTracking().OrderBy(project => project.ClientId).ThenBy(project => project.Name).ToListAsync();
            var selectedCustomerSet = selectedCustomerIds.ToHashSet();
            db.StaffProjects.RemoveRange(current.Where(membership => !projects.Any(project => project.Id == membership.ProjectId && selectedCustomerSet.Contains(project.ClientId))));
            var existingProjectIds = current.Select(membership => membership.ProjectId).ToHashSet();
            db.StaffProjects.AddRange(projects.Where(project => selectedCustomerSet.Contains(project.ClientId) && !existingProjectIds.Contains(project.Id))
                .Select(project => new StaffProject { StaffId = id, ProjectId = project.Id, SupportLevel = SupportLevel.L1 }));
            try {
                await db.SaveChangesAsync();
                TempData["Message"] = "Agent customer assignments saved. Existing support levels were kept; new customers use Frontline (L1).";
                return returnToDetails ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(AssignCustomers), new { id });
            }
            catch (DbUpdateException) { ModelState.AddModelError("", "Assignments could not be saved. Reload and try again."); }
        }
        model.Staff = staff; model.Customers = customers;
        return View(model);
    }
    public async Task<IActionResult> Delete(int id)
    {
        var user = await db.AitsStaff.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        ViewData["StaffSection"] = true;
        return user is null ? NotFound() : View(user);
    }
    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var user = await db.AitsStaff.FindAsync(id);
        if (user is null) return NotFound();
        if (await db.Tickets.AnyAsync(t => t.RequesterId == id || t.AssigneeId == id))
        {
            TempData["Error"] = "This user has tickets. Reassign or remove those tickets before deleting the user.";
            return RedirectToAction(user.UserType == UserType.AitsStaff ? nameof(AitsStaff) : nameof(ClientUsers), new { clientId = user.ClientId });
        }
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.DeskUserId == id);
        if (account is not null) db.Accounts.Remove(account);
        db.Users.Remove(user);
        try { await db.SaveChangesAsync(); TempData["Message"] = "User deleted."; }
        catch (DbUpdateException) { TempData["Error"] = "This user is still referenced by a ticket and cannot be deleted."; }
        return RedirectToAction(user.UserType == UserType.AitsStaff ? nameof(AitsStaff) : nameof(ClientUsers), new { clientId = user.ClientId });
    }
}
