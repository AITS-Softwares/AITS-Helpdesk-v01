using Microsoft.AspNetCore.Authorization;
using Help_Desk.Services;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Controllers;

[Authorize]
public class TicketsController(HelpDeskDbContext db, CurrentAccess access) : Controller
{
    private IQueryable<DeskUser> VisibleUsers()
    {
        var ownUser = access.UserId;
        var assignedIds = access.Tickets.Where(t => t.AssigneeId != null).Select(t => t.AssigneeId);
        return access.IsInternal ? db.Users : db.Users.Where(u => u.Id == ownUser || assignedIds.Contains(u.Id));
    }
    private async Task LoadOptions()
    {
        ViewBag.Users = await VisibleUsers().AsNoTracking().OrderBy(u => u.Name).ToListAsync();
        ViewBag.Clients = await access.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    }
    public async Task<IActionResult> Index(string? search, TicketStatus? status, TicketPriority? priority, int? clientId)
    {
        var tickets = access.Tickets.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            int.TryParse(term.TrimStart('#'), out var number);
            tickets = tickets.Where(t => t.Title.ToLower().Contains(term.ToLower()) || t.Description.ToLower().Contains(term.ToLower()) || t.Id == number);
        }
        if (clientId.HasValue) tickets = tickets.Where(t => t.ClientId == clientId);
        if (status.HasValue) tickets = tickets.Where(t => t.Status == status);
        if (priority.HasValue) tickets = tickets.Where(t => t.Priority == priority);
        return View(new TicketListViewModel {
            Tickets = await tickets.OrderByDescending(t => t.UpdatedAt).ToListAsync(),
            Clients = await access.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync(),
            Users = await VisibleUsers().AsNoTracking().ToListAsync(), ClientId = clientId, Search = search, Status = status, Priority = priority
        });
    }
    public async Task<IActionResult> Create(int? clientId)
    {
        await LoadOptions(); return View("Edit", new Ticket { ClientId = access.IsInternal ? clientId ?? 0 : access.CompanyId, RequesterId = access.IsInternal ? 0 : access.UserId });
    }
    public async Task<IActionResult> Edit(int id)
    {
        var ticket = await access.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        await LoadOptions(); return View(ticket);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([Bind("Id,Title,Description,Status,Priority,RequesterId,AssigneeId,ClientId")] Ticket ticket)
    {
        var existing = ticket.Id == 0 ? null : await access.Tickets.SingleOrDefaultAsync(t => t.Id == ticket.Id);
        if (ticket.Id != 0 && existing is null) return NotFound();
        if (!access.IsInternal) {
            ticket.ClientId = access.CompanyId; ticket.RequesterId = access.UserId;
            ticket.AssigneeId = existing?.AssigneeId; ticket.Status = existing?.Status ?? TicketStatus.Open;
            foreach (var field in new[] { "ClientId", "RequesterId", "AssigneeId", "Status" }) ModelState.Remove(field);
        }
        ticket.Title = ticket.Title?.Trim() ?? ""; ticket.Description = ticket.Description?.Trim() ?? "";
        if (!await db.Users.AnyAsync(u => u.Id == ticket.RequesterId && u.UserType == UserType.ClientUser && u.ClientId == ticket.ClientId)) ModelState.AddModelError("RequesterId", "Select a requester belonging to the selected company.");
        if (ticket.AssigneeId.HasValue && !await db.Users.AnyAsync(u => u.Id == ticket.AssigneeId && u.UserType == UserType.AitsStaff)) ModelState.AddModelError("AssigneeId", "Select an AITS support staff member.");
        if (!await db.Companies.AnyAsync(c => c.Id == ticket.ClientId)) ModelState.AddModelError("ClientId", "Select an existing company.");
        if (!ModelState.IsValid) { await LoadOptions(); return View("Edit", ticket); }
        var entity = existing ?? ticket;
        if (existing is null) db.Tickets.Add(entity);
        else { existing.ClientId = ticket.ClientId; existing.Title = ticket.Title; existing.Description = ticket.Description; existing.Status = ticket.Status; existing.Priority = ticket.Priority; existing.RequesterId = ticket.RequesterId; existing.AssigneeId = ticket.AssigneeId; existing.UpdatedAt = DateTime.UtcNow; }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to save. A selected company or user may have changed. Reload and try again."); await LoadOptions(); return View("Edit", ticket); }
        TempData["Message"] = "Ticket saved successfully.";
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }
    public async Task<IActionResult> Details(int id)
    {
        var ticket = await access.Tickets.AsNoTracking().Include(t => t.Comments.OrderBy(c => c.CreatedAt)).SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        await LoadOptions(); return View(ticket);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Comment(int id, string? body)
    {
        var ticket = await access.Tickets.SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        if (string.IsNullOrWhiteSpace(body) || body.Length > 3000) { TempData["Error"] = "Enter a comment between 1 and 3,000 characters."; return RedirectToAction(nameof(Details), new { id }); }
        db.Comments.Add(new TicketComment { TicketId = id, Body = body.Trim() });
        ticket.UpdatedAt = DateTime.UtcNow;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { TempData["Error"] = "The comment could not be saved. The ticket may have been removed."; }
        return RedirectToAction(nameof(Details), new { id });
    }
    [Authorize(Roles = "Admin,AitsUser")]
    public async Task<IActionResult> Delete(int id)
    {
        var ticket = await access.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);
        return ticket is null ? NotFound() : View(ticket);
    }
    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken, Authorize(Roles = "Admin,AitsUser")]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var ticket = await access.Tickets.SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        db.Tickets.Remove(ticket);
        try { await db.SaveChangesAsync(); TempData["Message"] = "Ticket and its comments deleted."; }
        catch (DbUpdateException) { TempData["Error"] = "Unable to delete this ticket. Reload and try again."; }
        return RedirectToAction(nameof(Index));
    }
}
