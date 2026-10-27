using Microsoft.AspNetCore.Authorization;
using Help_Desk.Data;
using Help_Desk.Models;
using Help_Desk.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Controllers;

// Clients are the customer companies supported by AITS.
[Authorize(Roles = "Admin,AitsUser")]
public class ClientsController(HelpDeskDbContext db, DeskStore store) : Controller
{
    public IActionResult Index() => View(store.Read());
    [Authorize(Roles = "Admin")]
    public IActionResult Create() => View("Edit", new Client());
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Edit(int id)
    {
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        return company is null ? NotFound() : View(company);
    }
    public async Task<IActionResult> Details(int id)
    {
        if (!await db.Companies.AnyAsync(c => c.Id == id)) return NotFound();
        ViewBag.ClientId = id;
        return View(store.Read());
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Save([Bind("Id,Name,Email,Phone")] Client company)
    {
        var existing = company.Id == 0 ? null : await db.Companies.FindAsync(company.Id);
        if (company.Id != 0 && existing is null) return NotFound();
        company.Name = company.Name?.Trim() ?? "";
        company.Email = company.Email?.Trim() ?? "";
        company.Phone = company.Phone?.Trim();
        if (await db.Companies.AnyAsync(c => c.Id != company.Id && c.Name == company.Name))
            ModelState.AddModelError("Name", "A company with this name already exists.");
        if (!ModelState.IsValid) return View("Edit", company);
        var entity = existing ?? company;
        if (existing is null) db.Companies.Add(entity);
        else { existing.Name = company.Name; existing.Email = company.Email; existing.Phone = company.Phone; }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to save. The company may already exist or have changed. Reload and try again."); return View("Edit", company); }
        TempData["Message"] = "Company saved successfully.";
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        return company is null ? NotFound() : View(company);
    }
    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var company = await db.Companies.FindAsync(id);
        if (company is null) return NotFound();
        if (await db.Users.AnyAsync(u => u.ClientId == id) || await db.Tickets.AnyAsync(t => t.ClientId == id))
        {
            TempData["Error"] = "This company has users or tickets. Remove or reassign them before deleting the company.";
            return RedirectToAction(nameof(Details), new { id });
        }
        db.Companies.Remove(company);
        try { await db.SaveChangesAsync(); TempData["Message"] = "Company deleted."; }
        catch (DbUpdateException) { TempData["Error"] = "The company could not be deleted because it is still in use."; }
        return RedirectToAction(nameof(Index));
    }
}
