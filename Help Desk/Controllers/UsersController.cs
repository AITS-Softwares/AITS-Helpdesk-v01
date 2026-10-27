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
    public async Task<IActionResult> Index(int? clientId)
    {
        await LoadCompanies(); ViewBag.ClientId = clientId;
        return View(await db.Users.AsNoTracking().Where(u => !clientId.HasValue || u.ClientId == clientId).OrderBy(u => u.Name).ToListAsync());
    }
    public async Task<IActionResult> Create(int? clientId)
    {
        await LoadCompanies(); return View("Edit", new DeskUser { ClientId = clientId });
    }
    public async Task<IActionResult> Edit(int id)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();
        await LoadCompanies(); return View(user);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([Bind("Id,Name,Email,Department,UserType,ClientId")] DeskUser user)
    {
        var existing = user.Id == 0 ? null : await db.Users.FindAsync(user.Id);
        if (user.Id != 0 && existing is null) return NotFound();
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
        if (account is not null) {
            account.Name = user.Name; account.Email = user.Email;
            account.Role = user.UserType == UserType.AitsStaff ? AccountRole.AitsUser : AccountRole.ClientUser;
            account.SecurityStamp = Guid.NewGuid();
        }
        if (existing is null) db.Users.Add(user);
        else { existing.Name = user.Name; existing.Email = user.Email; existing.Department = user.Department; existing.UserType = user.UserType; existing.ClientId = user.ClientId; }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to save. The email may be in use or the company may have changed. Reload and try again."); await LoadCompanies(); return View("Edit", user); }
        TempData["Message"] = "User saved successfully.";
        return RedirectToAction(nameof(Index), new { clientId = user.ClientId });
    }
    public async Task<IActionResult> Delete(int id)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        return user is null ? NotFound() : View(user);
    }
    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();
        if (await db.Tickets.AnyAsync(t => t.RequesterId == id || t.AssigneeId == id))
        {
            TempData["Error"] = "This user has tickets. Reassign or remove those tickets before deleting the user.";
            return RedirectToAction(nameof(Index));
        }
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.DeskUserId == id);
        if (account is not null) db.Accounts.Remove(account);
        db.Users.Remove(user);
        try { await db.SaveChangesAsync(); TempData["Message"] = "User deleted."; }
        catch (DbUpdateException) { TempData["Error"] = "This user is still referenced by a ticket and cannot be deleted."; }
        return RedirectToAction(nameof(Index));
    }
}
