using Help_Desk.Data;
using Help_Desk.Models;
using Help_Desk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Help_Desk.Controllers;

[Authorize(Roles = "Admin")]
public class TatSettingsController(HelpDeskDbContext db, TicketTatPolicy policy) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index() => View(await policy.ReadAsync());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index([Bind("LowHours,MediumHours,HighHours,UrgentHours")] TatPolicy model)
    {
        if (!ModelState.IsValid) return View(model);
        var current = await db.TatPolicies.SingleOrDefaultAsync(p => p.Id == 1);
        if (current is null) { current = new TatPolicy(); db.TatPolicies.Add(current); }
        current.LowHours = model.LowHours; current.MediumHours = model.MediumHours;
        current.HighHours = model.HighHours; current.UrgentHours = model.UrgentHours;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) {
            ModelState.AddModelError("", "TAT settings could not be saved. Reload the page and try again.");
            return View(model);
        }
        TempData["Message"] = "Priority TAT settings saved. New tickets use these targets; existing deadlines are preserved.";
        return RedirectToAction(nameof(Index));
    }
}
