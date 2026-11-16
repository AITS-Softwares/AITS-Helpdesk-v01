using Help_Desk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Help_Desk.Controllers;
[Authorize(Roles = "AitsUser")]
public class MyWorkController(CurrentAccess access) : Controller
{
    public async Task<IActionResult> Index(int? companyId)
    {
        var projects = access.Projects.AsNoTracking().Include(p => p.Client);
        var list = await projects.OrderBy(p => p.Client!.Name).ThenBy(p => p.Name).ToListAsync();
        ViewBag.Companies = list.Select(p => p.Client!).DistinctBy(c => c.Id).ToList();
        ViewBag.CompanyId = companyId;
        return View(companyId.HasValue ? list.Where(p => p.ClientId == companyId).ToList() : list);
    }
}
