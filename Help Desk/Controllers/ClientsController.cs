using Microsoft.AspNetCore.Authorization;
using Help_Desk.Data;
using Help_Desk.Models;
using Help_Desk.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Controllers;

// Clients are the customer companies supported by AITS.
[Authorize(Roles = "Admin")]
public class ClientsController(HelpDeskDbContext db, DeskStore store) : Controller
{
    public async Task<IActionResult> Index()
    {
        ViewBag.Projects = await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        return View(store.Read());
    }
    public async Task<IActionResult> Project(int id)
    {
        var project = await db.Projects.AsNoTracking().Include(p => p.Client).SingleOrDefaultAsync(p => p.Id == id);
        if (project is null) return NotFound();
        return View(new ProjectTeamViewModel { Project = project,
            Staff = await db.AitsStaff.AsNoTracking().OrderBy(s => s.Name).ToListAsync(),
            Members = await db.StaffProjects.AsNoTracking().Where(m => m.ProjectId == id).ToListAsync() });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignTeam(int id, List<int> l1, List<int> l2, List<int> l3)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == id)) return NotFound();
        var selected = l1.Select(s => (Id: s, Level: SupportLevel.L1)).Concat(l2.Select(s => (Id: s, Level: SupportLevel.L2)))
            .Concat(l3.Select(s => (Id: s, Level: SupportLevel.L3))).ToList();
        if (selected.Select(s => s.Id).Distinct().Count() != selected.Count) {
            TempData["Error"] = "Assign each staff member to only one level in this project.";
            return RedirectToAction(nameof(Project), new { id });
        }
        var ids = selected.Select(s => s.Id).ToList();
        if (await db.AitsStaff.CountAsync(s => ids.Contains(s.Id) && s.IsActive) != ids.Count) {
            TempData["Error"] = "Select active AITS staff only.";
            return RedirectToAction(nameof(Project), new { id });
        }
        var current = await db.StaffProjects.Where(m => m.ProjectId == id).ToListAsync();
        db.StaffProjects.RemoveRange(current.Where(m => !ids.Contains(m.StaffId)));
        foreach (var member in selected) {
            var existing = current.SingleOrDefault(m => m.StaffId == member.Id);
            if (existing is null) db.StaffProjects.Add(new StaffProject { ProjectId = id, StaffId = member.Id, SupportLevel = member.Level });
            else existing.SupportLevel = member.Level;
        }
        await db.SaveChangesAsync();
        TempData["Message"] = "Project team saved. Company/project access follows these assignments.";
        return RedirectToAction(nameof(Project), new { id });
    }
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
        ViewBag.Projects = await db.Projects.AsNoTracking().Where(p => p.ClientId == id).OrderBy(p => p.Name).ToListAsync();
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
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> AddProject(int clientId, string? name)
    {
        if (!await db.Companies.AnyAsync(c => c.Id == clientId)) return NotFound();
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            TempData["Error"] = "Enter a project name between 1 and 120 characters.";
        else {
            db.Projects.Add(new CompanyProject { ClientId = clientId, Name = name });
            try { await db.SaveChangesAsync(); TempData["Message"] = "Project added."; }
            catch (DbUpdateException) { TempData["Error"] = "Unable to add project. This name may already exist for this company."; }
        }
        return RedirectToAction(nameof(Details), new { id = clientId });
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> RemoveProject(int id)
    {
        var project = await db.Projects.FindAsync(id);
        if (project is null) return NotFound();
        if (await db.Tickets.AnyAsync(t => t.ProjectId == id)) TempData["Error"] = "Projects used by tickets cannot be removed.";
        else {
            db.Projects.Remove(project);
            try { await db.SaveChangesAsync(); TempData["Message"] = "Project removed."; }
            catch (DbUpdateException) { TempData["Error"] = "This project is still in use."; }
        }
        return RedirectToAction(nameof(Details), new { id = project.ClientId });
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
        db.Projects.RemoveRange(db.Projects.Where(p => p.ClientId == id));
        db.Companies.Remove(company);
        try { await db.SaveChangesAsync(); TempData["Message"] = "Company deleted."; }
        catch (DbUpdateException) { TempData["Error"] = "The company could not be deleted because it is still in use."; }
        return RedirectToAction(nameof(Index));
    }
}
