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
        data.Clients = data.Clients.OrderByDescending(customer => customer.Id).ToList();
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
    public async Task<IActionResult> AssignAgents(int clientId, int[]? staffIds)
    {
        if (!await db.Companies.AnyAsync(customer => customer.Id == clientId)) return NotFound();
        var selectedIds = (staffIds ?? []).Distinct().ToList();
        if (selectedIds.Count != (staffIds?.Length ?? 0)) {
            TempData["Error"] = "An agent can only be selected once.";
            return RedirectToAction(nameof(Index));
        }
        if (selectedIds.Count > 0 && await db.AitsStaff.CountAsync(staff => selectedIds.Contains(staff.Id) && staff.IsActive) != selectedIds.Count) {
            TempData["Error"] = "Choose active agents only. Reload the page and try again.";
            return RedirectToAction(nameof(Index));
        }
        var current = await db.CustomerAgents.Where(assignment => assignment.ClientId == clientId).ToListAsync();
        db.CustomerAgents.RemoveRange(current);
        foreach (var staffId in selectedIds) db.CustomerAgents.Add(new CustomerAgent { ClientId = clientId, StaffId = staffId });
        try {
            await db.SaveChangesAsync();
            TempData["Message"] = "Customer agent assignments saved.";
        }
        catch (DbUpdateException) { TempData["Error"] = "Customer agent assignments could not be saved. Reload and try again."; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAssignedAgents(int clientId, int[]? staffIds)
    {
        if (!await db.Companies.AnyAsync(customer => customer.Id == clientId)) return NotFound();
        var selectedIds = (staffIds ?? []).Distinct().ToList();
        if (selectedIds.Count != (staffIds?.Length ?? 0) || (selectedIds.Count > 0 && await db.AitsStaff.CountAsync(staff => selectedIds.Contains(staff.Id) && staff.IsActive) != selectedIds.Count))
            return BadRequest(new { message = "Choose active agents only." });
        var current = await db.CustomerAgents.Where(assignment => assignment.ClientId == clientId).ToListAsync();
        var currentIds = current.Select(assignment => assignment.StaffId).ToHashSet();
        db.CustomerAgents.RemoveRange(current.Where(assignment => !selectedIds.Contains(assignment.StaffId)));
        db.CustomerAgents.AddRange(selectedIds.Where(staffId => !currentIds.Contains(staffId))
            .Select(staffId => new CustomerAgent { ClientId = clientId, StaffId = staffId }));
        await db.SaveChangesAsync();
        return Json(new { message = "Saved" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveL1Agents(int clientId, int? staffId)
    {
        if (!await db.Companies.AnyAsync(customer => customer.Id == clientId)) return NotFound();
        var projects = await db.Projects.Where(project => project.ClientId == clientId).ToListAsync();
        if (staffId.HasValue && !await db.AitsStaff.AnyAsync(staff => staff.Id == staffId.Value && staff.IsActive))
            return BadRequest(new { message = "Choose active agents only." });
        var projectIds = projects.Select(project => project.Id).ToList();
        var current = await db.StaffProjects.Where(membership => projectIds.Contains(membership.ProjectId)).ToListAsync();
        db.StaffProjects.RemoveRange(current.Where(membership => membership.SupportLevel == SupportLevel.L1 && membership.StaffId != staffId));
        if (staffId.HasValue) {
            db.StaffProjects.RemoveRange(current.Where(membership => membership.StaffId == staffId.Value && membership.SupportLevel != SupportLevel.L1));
            var currentProjectIds = current.Where(membership => membership.StaffId == staffId.Value && membership.SupportLevel == SupportLevel.L1).Select(membership => membership.ProjectId).ToHashSet();
            foreach (var project in projects.Where(project => !currentProjectIds.Contains(project.Id)))
                db.StaffProjects.Add(new StaffProject { ProjectId = project.Id, StaffId = staffId.Value, SupportLevel = SupportLevel.L1 });
        }
        await db.SaveChangesAsync();
        return Json(new { message = "Saved" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSupportTeam(int clientId, int? l1StaffId, int? l2StaffId, int? l3StaffId)
    {
        if (!await db.Companies.AnyAsync(customer => customer.Id == clientId)) return NotFound();
        var projects = await db.Projects.Where(project => project.ClientId == clientId).ToListAsync();
        if (projects.Count == 0) return BadRequest(new { message = "This customer has no support project yet." });
        var selected = new Dictionary<SupportLevel, List<int>> {
            [SupportLevel.L1] = l1StaffId.HasValue ? [l1StaffId.Value] : [],
            [SupportLevel.L2] = l2StaffId.HasValue ? [l2StaffId.Value] : [],
            [SupportLevel.L3] = l3StaffId.HasValue ? [l3StaffId.Value] : []
        };
        var selectedIds = selected.Values.SelectMany(ids => ids).Distinct().ToList();
        if (selectedIds.Count != selected.Values.Sum(ids => ids.Count))
            return BadRequest(new { message = "Choose different agents for each support level." });
        if (selectedIds.Count > 0 && await db.AitsStaff.CountAsync(staff => selectedIds.Contains(staff.Id) && staff.IsActive) != selectedIds.Count)
            return BadRequest(new { message = "Choose active agents only." });

        var projectIds = projects.Select(project => project.Id).ToList();
        var current = await db.StaffProjects.Where(membership => projectIds.Contains(membership.ProjectId)).ToListAsync();
        var desired = projects.SelectMany(project => selected.SelectMany(level => level.Value.Select(staffId => (ProjectId: project.Id, StaffId: staffId, Level: level.Key))))
            .ToDictionary(assignment => (assignment.ProjectId, assignment.StaffId), assignment => assignment.Level);
        db.StaffProjects.RemoveRange(current.Where(membership => !desired.ContainsKey((membership.ProjectId, membership.StaffId))));
        foreach (var membership in current) {
            if (desired.TryGetValue((membership.ProjectId, membership.StaffId), out var level))
                membership.SupportLevel = level;
        }
        var existingKeys = current.Select(membership => (membership.ProjectId, membership.StaffId)).ToHashSet();
        foreach (var assignment in desired.Where(assignment => !existingKeys.Contains(assignment.Key)))
            db.StaffProjects.Add(new StaffProject { ProjectId = assignment.Key.ProjectId, StaffId = assignment.Key.StaffId, SupportLevel = assignment.Value });
        await db.SaveChangesAsync();
        return Json(new { message = "Support team saved" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignTeam(int clientId, int[]? l1StaffIds, int[]? l2StaffIds, int[]? l3StaffIds, bool returnToDetails = false)
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
        var desired = projects.SelectMany(project => selected.SelectMany(assignment => assignment.Value.Select(staffId => (ProjectId: project.Id, StaffId: staffId, Level: assignment.Key))))
            .ToDictionary(assignment => (assignment.ProjectId, assignment.StaffId), assignment => assignment.Level);
        var currentMemberships = await db.StaffProjects.Where(membership => projectIds.Contains(membership.ProjectId)).ToListAsync();
        db.StaffProjects.RemoveRange(currentMemberships.Where(membership => !desired.ContainsKey((membership.ProjectId, membership.StaffId))));
        foreach (var membership in currentMemberships) {
            if (desired.TryGetValue((membership.ProjectId, membership.StaffId), out var level))
                membership.SupportLevel = level;
        }
        var currentKeys = currentMemberships.Select(membership => (membership.ProjectId, membership.StaffId)).ToHashSet();
        foreach (var assignment in desired.Where(assignment => !currentKeys.Contains(assignment.Key)))
            db.StaffProjects.Add(new StaffProject { ProjectId = assignment.Key.ProjectId, StaffId = assignment.Key.StaffId, SupportLevel = assignment.Value });
        try {
            await db.SaveChangesAsync();
            TempData["Message"] = "L1, L2, and L3 agent assignments saved for this customer's projects.";
        }
        catch (DbUpdateException) { TempData["Error"] = "Agent assignments could not be saved. Reload and try again."; }
        return returnToDetails ? RedirectToAction(nameof(Details), new { id = clientId }) : RedirectToAction(nameof(Index));
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
        ViewBag.Agents = await db.AitsStaff.AsNoTracking().Where(staff => staff.IsActive).OrderBy(staff => staff.Name).ToListAsync();
        ViewBag.TeamAssignments = await db.StaffProjects.AsNoTracking().Include(membership => membership.Project)
            .Where(membership => membership.Project!.ClientId == id).ToListAsync();
        return View(store.Read());
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Save([Bind("Id,Name,Email,EmailDomains,Phone,GstNumber")] Client company)
    {
        var existing = company.Id == 0 ? null : await db.Companies.FindAsync(company.Id);
        if (company.Id != 0 && existing is null) return NotFound();
        company.Name = company.Name?.Trim() ?? "";
        company.Email = company.Email?.Trim() ?? "";
        var domainPattern = @"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$";
        var validEmail = new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(company.Email);
        var validDomain = System.Text.RegularExpressions.Regex.IsMatch(company.Email, domainPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!validEmail && !validDomain) ModelState.AddModelError("Email", "Enter a valid email address or domain.");
        var emailRules = (company.EmailDomains ?? "").Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var invalidRule = emailRules.FirstOrDefault(rule => rule.Contains('@')
            ? !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(rule)
            : !System.Text.RegularExpressions.Regex.IsMatch(rule, domainPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        if (invalidRule is not null) ModelState.AddModelError("EmailDomains", $"'{invalidRule}' is not a valid email address or domain.");
        company.EmailDomains = string.Join("\n", emailRules);
        company.Phone = company.Phone?.Trim();
        company.GstNumber = string.IsNullOrWhiteSpace(company.GstNumber) ? null : company.GstNumber.Trim().ToUpperInvariant();
        if (await db.Companies.AnyAsync(c => c.Id != company.Id && c.Name == company.Name))
            ModelState.AddModelError("Name", "A customer with this name already exists.");
        if (!ModelState.IsValid) return View("Edit", company);
        var entity = existing ?? company;
        if (existing is null) {
            db.Companies.Add(entity);
            db.Projects.Add(new CompanyProject { Client = entity, Name = "General" });
        }
        else { existing.Name = company.Name; existing.Email = company.Email; existing.EmailDomains = company.EmailDomains; existing.Phone = company.Phone; existing.GstNumber = company.GstNumber; }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to save. The customer may already exist or have changed. Reload and try again."); return View("Edit", company); }
        TempData["Message"] = "Customer saved successfully.";
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        if (company is not null) {
            ViewBag.LinkedUserCount = await db.ClientUsers.CountAsync(user => user.ClientId == id);
            ViewBag.LinkedTicketCount = await db.Tickets.CountAsync(ticket => ticket.ClientId == id);
            ViewBag.AssignedAgentCount = await db.StaffProjects.CountAsync(membership => membership.Project!.ClientId == id);
        }
        return company is null ? NotFound() : View(company);
    }
    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var company = await db.Companies.FindAsync(id);
        if (company is null) return NotFound();
        var users = await db.ClientUsers.Where(user => user.ClientId == id).ToListAsync();
        var userIds = users.Select(user => user.Id).ToList();
        var projects = await db.Projects.Where(project => project.ClientId == id).ToListAsync();
        var projectIds = projects.Select(project => project.Id).ToList();
        var tickets = await db.Tickets.Where(ticket => ticket.ClientId == id).ToListAsync();
        var ticketIds = tickets.Select(ticket => ticket.Id).ToList();

        // Remove ticket children explicitly so SQL Server's NO ACTION reply-attachment
        // relationship does not conflict with the ticket's other cascading relationships.
        db.TicketAttachments.RemoveRange(await db.TicketAttachments.Where(item => ticketIds.Contains(item.TicketId)).ToListAsync());
        db.Comments.RemoveRange(await db.Comments.Where(item => ticketIds.Contains(item.TicketId)).ToListAsync());
        db.TicketActivities.RemoveRange(await db.TicketActivities.Where(item => ticketIds.Contains(item.TicketId)).ToListAsync());
        db.TicketEscalations.RemoveRange(await db.TicketEscalations.Where(item => ticketIds.Contains(item.TicketId)).ToListAsync());
        db.TicketEmailNotifications.RemoveRange(await db.TicketEmailNotifications.Where(item => ticketIds.Contains(item.TicketId)).ToListAsync());
        db.TicketPhotos.RemoveRange(await db.TicketPhotos.Where(item => ticketIds.Contains(item.TicketId)).ToListAsync());
        db.Tickets.RemoveRange(tickets);

        db.StaffProjects.RemoveRange(await db.StaffProjects.Where(membership => projectIds.Contains(membership.ProjectId)).ToListAsync());
        db.CustomerAgents.RemoveRange(await db.CustomerAgents.Where(assignment => assignment.ClientId == id).ToListAsync());
        db.ClientAccounts.RemoveRange(await db.ClientAccounts.Where(account => account.DeskUserId.HasValue && userIds.Contains(account.DeskUserId.Value)).ToListAsync());
        db.ClientUsers.RemoveRange(users);
        db.Projects.RemoveRange(projects);
        db.Companies.Remove(company);
        try { await db.SaveChangesAsync(); TempData["Message"] = "Customer deleted."; }
        catch (DbUpdateException) { TempData["Error"] = "The company could not be deleted because it is still in use."; }
        return RedirectToAction(nameof(Index));
    }
}
