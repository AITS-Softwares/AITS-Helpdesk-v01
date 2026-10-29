using Help_Desk.Data;
using Help_Desk.Models;
using Help_Desk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Help_Desk.Controllers;
[Authorize(Roles = "AitsUser")]
public class MyWorkController(HelpDeskDbContext db, CurrentAccess access) : Controller
{
    public async Task<IActionResult> Index(int? companyId)
    {
        var projects = access.Projects.AsNoTracking().Include(p => p.Client);
        var list = await projects.OrderBy(p => p.Client!.Name).ThenBy(p => p.Name).ToListAsync();
        ViewBag.Companies = list.Select(p => p.Client!).DistinctBy(c => c.Id).ToList();
        ViewBag.CompanyId = companyId;
        return View(companyId.HasValue ? list.Where(p => p.ClientId == companyId).ToList() : list);
    }
    public async Task<IActionResult> ClientUsers(int? companyId, int? projectId)
    {
        var projects = await access.Projects.AsNoTracking().Include(p => p.Client)
            .OrderBy(p => p.Client!.Name).ThenBy(p => p.Name).ToListAsync();
        if (projectId.HasValue) {
            var project = projects.SingleOrDefault(p => p.Id == projectId);
            if (project is null || (companyId.HasValue && companyId != project.ClientId)) return NotFound();
            companyId = project.ClientId;
        }
        if (companyId.HasValue && !projects.Any(p => p.ClientId == companyId)) return NotFound();
        var companyIds = projects.Select(p => p.ClientId).Distinct().ToList();
        ViewBag.Projects = projects;
        ViewBag.CompanyId = companyId;
        ViewBag.ProjectId = projectId;
        ViewBag.EnabledUserIds = (await db.ClientAccounts.AsNoTracking().Where(a => a.DeskUserId != null).Select(a => a.DeskUserId!.Value).ToListAsync()).ToHashSet();
        return View(await db.ClientUsers.AsNoTracking().Include(u => u.Client)
            .Where(u => companyIds.Contains(u.ClientId!.Value) && (!companyId.HasValue || u.ClientId == companyId))
            .OrderBy(u => u.Client!.Name).ThenBy(u => u.Name).ToListAsync());
    }
    private async Task LoadClientCompanies() => ViewBag.Clients = await access.TicketCompanies.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    [HttpGet]
    public async Task<IActionResult> CreateClient(int? companyId)
    {
        if (companyId.HasValue && !await access.TicketCompanies.AnyAsync(c => c.Id == companyId)) return NotFound();
        await LoadClientCompanies();
        return View(new UserEditViewModel { ClientId = companyId, UserType = UserType.ClientUser });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateClient([Bind("Name,Email,Department,ClientId")] UserEditViewModel model)
    {
        if (!await access.TicketCompanies.AnyAsync(c => c.Id == model.ClientId)) return Forbid();
        model.Name = model.Name?.Trim() ?? "";
        model.Email = model.Email?.Trim() ?? "";
        model.Department = model.Department?.Trim() ?? "";
        if (await db.Users.AnyAsync(u => u.Email == model.Email) || await db.Accounts.AnyAsync(a => a.Email == model.Email))
            ModelState.AddModelError("Email", "This email address is already in use.");
        if (!ModelState.IsValid) { await LoadClientCompanies(); return View(model); }
        var client = new ClientDeskUser { Name = model.Name, Email = model.Email, Department = model.Department, ClientId = model.ClientId };
        db.ClientUsers.Add(client);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to create this client user. Reload and try again."); await LoadClientCompanies(); return View(model); }
        TempData["Message"] = "Client user created. You can now enable their sign-in.";
        return RedirectToAction("Create", "Account", new { userId = client.Id });
    }
    public async Task<IActionResult> Project(int id)
    {
        var project = await access.Projects.AsNoTracking().Include(p => p.Client).SingleOrDefaultAsync(p => p.Id == id);
        if (project is null) return NotFound();
        return View(new ProjectTeamViewModel { Project = project,
            Members = await db.StaffProjects.AsNoTracking().Include(m => m.Staff).Where(m => m.ProjectId == id).OrderBy(m => m.SupportLevel).ThenBy(m => m.Staff!.Name).ToListAsync() });
    }
}
