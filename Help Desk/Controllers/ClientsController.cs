using Microsoft.AspNetCore.Authorization;
using Help_Desk.Data;
using Help_Desk.Models;
using Help_Desk.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Controllers;

// Customers are supported by AITS.
[Authorize(Roles = "Admin")]
public class ClientsController(HelpDeskDbContext db, DeskStore store) : Controller
{
    public async Task<IActionResult> Index()
    {
        var data = store.Read();
        ViewBag.Agents = await db.AitsStaff.AsNoTracking().Where(staff => staff.IsActive).OrderBy(staff => staff.Name).ToListAsync();
        var memberships = await db.StaffProjects.AsNoTracking().Include(membership => membership.Project).ToListAsync();
        ViewBag.TeamAssignments = memberships
            .Where(membership => membership.Project is not null)
            .GroupBy(membership => membership.Project!.ClientId)
            .ToDictionary(group => group.Key, group => group
                .GroupBy(membership => membership.SupportLevel)
                .ToDictionary(level => level.Key, level => level.Select(membership => membership.StaffId).Distinct().OrderBy(id => id).ToList()));
        return View(data);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignTeam(int clientId, int[]? l1StaffIds, int[]? l2StaffIds, int[]? l3StaffIds)
    {
        var projects = await db.Projects.Where(project => project.ClientId == clientId).ToListAsync();
        if (projects.Count == 0 || !await db.Companies.AnyAsync(customer => customer.Id == clientId)) return NotFound();
        var selected = new Dictionary<SupportLevel, List<int>> {
            [SupportLevel.L1] = (l1StaffIds ?? []).Distinct().ToList(),
            [SupportLevel.L2] = (l2StaffIds ?? []).Distinct().ToList(),
            [SupportLevel.L3] = (l3StaffIds ?? []).Distinct().ToList()
        };
        var submittedCount = (l1StaffIds?.Length ?? 0) + (l2StaffIds?.Length ?? 0) + (l3StaffIds?.Length ?? 0);
        var selectedIds = selected.Values.SelectMany(ids => ids).Distinct().ToList();
        if (submittedCount != selectedIds.Count) {
            TempData["Error"] = "Choose a different agent for each support level.";
            return RedirectToAction(nameof(Index));
        }
        if (selectedIds.Count > 0 && await db.AitsStaff.CountAsync(staff => selectedIds.Contains(staff.Id) && staff.IsActive) != selectedIds.Count) {
            TempData["Error"] = "Choose active agents only. Reload the page and try again.";
            return RedirectToAction(nameof(Index));
        }
        var projectIds = projects.Select(project => project.Id).ToList();
        db.StaffProjects.RemoveRange(db.StaffProjects.Where(membership => projectIds.Contains(membership.ProjectId)));
        foreach (var project in projects)
            foreach (var assignment in selected)
                foreach (var staffId in assignment.Value)
                    db.StaffProjects.Add(new StaffProject { ProjectId = project.Id, StaffId = staffId, SupportLevel = assignment.Key });
        try {
            await db.SaveChangesAsync();
            TempData["Message"] = "L1, L2, and L3 agent assignments saved for this customer's projects.";
        }
        catch (DbUpdateException) { TempData["Error"] = "Agent assignments could not be saved. Reload and try again."; }
        return RedirectToAction(nameof(Index));
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
        ViewBag.ClientId = id;
        return View(store.Read());
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Save([Bind("Id,Name,Email,EmailDomains,Phone")] Client company)
    {
        var existing = company.Id == 0 ? null : await db.Companies.FindAsync(company.Id);
        if (company.Id != 0 && existing is null) return NotFound();
        company.Name = company.Name?.Trim() ?? "";
        company.Email = company.Email?.Trim() ?? "";
        var emailRules = (company.EmailDomains ?? "").Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var invalidRule = emailRules.FirstOrDefault(rule => rule.Contains('@')
            ? !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(rule)
            : !System.Text.RegularExpressions.Regex.IsMatch(rule, @"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$"));
        if (invalidRule is not null) ModelState.AddModelError("EmailDomains", $"'{invalidRule}' is not a valid email address or domain.");
        company.EmailDomains = string.Join("\n", emailRules);
        company.Phone = company.Phone?.Trim();
        if (await db.Companies.AnyAsync(c => c.Id != company.Id && c.Name == company.Name))
            ModelState.AddModelError("Name", "A customer with this name already exists.");
        if (!ModelState.IsValid) return View("Edit", company);
        var entity = existing ?? company;
        if (existing is null) {
            db.Companies.Add(entity);
            db.Projects.Add(new CompanyProject { Client = entity, Name = "General" });
        }
        else { existing.Name = company.Name; existing.Email = company.Email; existing.EmailDomains = company.EmailDomains; existing.Phone = company.Phone; }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to save. The customer may already exist or have changed. Reload and try again."); return View("Edit", company); }
        TempData["Message"] = "Customer saved successfully.";
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
            TempData["Error"] = "This customer has users or tickets. Remove or reassign them before deleting the customer.";
            return RedirectToAction(nameof(Details), new { id });
        }
        db.Projects.RemoveRange(db.Projects.Where(p => p.ClientId == id));
        db.Companies.Remove(company);
        try { await db.SaveChangesAsync(); TempData["Message"] = "Customer deleted."; }
        catch (DbUpdateException) { TempData["Error"] = "The company could not be deleted because it is still in use."; }
        return RedirectToAction(nameof(Index));
    }
}
