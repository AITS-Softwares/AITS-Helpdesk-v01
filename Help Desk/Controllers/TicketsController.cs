using Microsoft.AspNetCore.Authorization;
using Help_Desk.Services;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Controllers;

[Authorize]
public class TicketsController(HelpDeskDbContext db, CurrentAccess access, TicketCreatedEmails emails, TicketTatPolicy tatPolicy) : Controller
{
    [HttpGet, Authorize(Roles = "Admin,AitsUser")]
    public async Task<IActionResult> TatAlerts([FromServices] TatAlerts alerts, CancellationToken cancellationToken)
        => Json(await alerts.ReadAsync(cancellationToken));

    private IQueryable<DeskUser> VisibleUsers()
    {
        var ownUser = access.UserId;
        var assignedIds = access.Tickets.Where(t => t.AssigneeId != null).Select(t => t.AssigneeId);
        return access.IsInternal ? db.Users : db.Users.Where(u => u.Id == ownUser || assignedIds.Contains(u.Id));
    }
    private async Task LoadOptions(int ticketId = 0)
    {
        ViewBag.StaffMemberships = await db.StaffProjects.AsNoTracking().ToListAsync();
        ViewBag.HasPhoto = ticketId > 0 && await db.TicketPhotos.AnyAsync(p => p.TicketId == ticketId);
        ViewBag.Projects = await access.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        ViewBag.Users = await VisibleUsers().AsNoTracking().OrderBy(u => u.Name).ToListAsync();
        ViewBag.Clients = await access.TicketCompanies.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    }
    public async Task<IActionResult> Index(string? search, TicketStatus? status, TicketPriority? priority, int? clientId, int? projectId, string? scope)
    {
        if (scope is not null) {
            clientId = null; projectId = null;
            if (scope.StartsWith("company:") && int.TryParse(scope[8..], out var company)) clientId = company;
            else if (scope.StartsWith("project:") && int.TryParse(scope[8..], out var project)) projectId = project;
        }
        ViewBag.Scope = projectId.HasValue ? $"project:{projectId}" : clientId.HasValue ? $"company:{clientId}" : "";
        var tickets = access.Tickets.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            int.TryParse(term.TrimStart('#'), out var number);
            tickets = tickets.Where(t => t.Title.ToLower().Contains(term.ToLower()) || t.Description.ToLower().Contains(term.ToLower()) || t.Id == number);
        }
        if (clientId.HasValue) tickets = tickets.Where(t => t.ClientId == clientId);
        if (projectId.HasValue) tickets = tickets.Where(t => t.ProjectId == projectId);
        if (status.HasValue) tickets = tickets.Where(t => t.Status == status);
        if (priority.HasValue) tickets = tickets.Where(t => t.Priority == priority);
        return View(new TicketListViewModel {
            Projects = await access.Projects.AsNoTracking().Include(p => p.Client).OrderBy(p => p.Client!.Name).ThenBy(p => p.Name).ToListAsync(),
            ProjectId = projectId,
            Tickets = await tickets.Include(t => t.Project).OrderByDescending(t => t.UpdatedAt).ToListAsync(),
            Clients = await access.TicketCompanies.AsNoTracking().OrderBy(c => c.Name).ToListAsync(),
            Users = await VisibleUsers().AsNoTracking().ToListAsync(), ClientId = clientId, Search = search, Status = status, Priority = priority
        });
    }
    [Authorize(Roles = "Admin,ClientUser")]
    public async Task<IActionResult> Create(int? clientId)
    {
        await LoadOptions(); return View("Edit", new Ticket { ClientId = access.IsInternal ? clientId ?? 0 : access.CompanyId, RequesterId = access.IsInternal ? 0 : access.UserId });
    }
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Edit(int id)
    {
        var ticket = await access.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        await LoadOptions(ticket.Id); return View(ticket);
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(6 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    [Authorize(Roles = "Admin,ClientUser")]
    public async Task<IActionResult> Save([Bind("Id,Title,Description,Status,Priority,RequesterId,AssigneeId,ClientId,ProjectId")] Ticket ticket, IFormFile? photo, bool removePhoto = false, int? tatHours = null)
    {
        if (ticket.Id != 0 && !access.IsAdmin) return Forbid();
        var existing = ticket.Id == 0 ? null : await access.Tickets.SingleOrDefaultAsync(t => t.Id == ticket.Id);
        if (ticket.Id != 0 && existing is null) return NotFound();
        if (!access.IsInternal) {
            ticket.ClientId = access.CompanyId; ticket.RequesterId = access.UserId;
            ticket.AssigneeId = existing?.AssigneeId; ticket.Status = existing?.Status ?? TicketStatus.Open;
            foreach (var field in new[] { "ClientId", "RequesterId", "AssigneeId", "Status" }) ModelState.Remove(field);
        }
        if (existing is not null) {
            ticket.ClientId = existing.ClientId;
            ModelState.Remove("ClientId");
        }
        if (access.IsInternal && !access.IsAdmin) {
            ticket.AssigneeId = existing?.AssigneeId;
            ModelState.Remove("AssigneeId");
        }
        var projects = await access.Projects.Where(p => p.ClientId == ticket.ClientId).ToListAsync();
        if (ticket.ProjectId is null && projects.Count == 1) {
            ticket.ProjectId = projects[0].Id;
            ModelState.Remove("ProjectId");
        }
        if (ticket.ProjectId.HasValue && !projects.Any(p => p.Id == ticket.ProjectId))
            ModelState.AddModelError("ProjectId", "Select a project belonging to this company.");
        if (projects.Count > 0 && ticket.ProjectId is null)
            ModelState.AddModelError("ProjectId", "Select a project.");
        if (access.IsInternal && !access.IsAdmin && (ticket.ProjectId is null || !await access.Projects.AnyAsync(p => p.Id == ticket.ProjectId && p.ClientId == ticket.ClientId)))
            ModelState.AddModelError("ProjectId", "You can create or move tickets only within your assigned company projects.");
        ticket.Title = ticket.Title?.Trim() ?? ""; ticket.Description = ticket.Description?.Trim() ?? "";
        if (!await db.Users.AnyAsync(u => u.Id == ticket.RequesterId && u.UserType == UserType.ClientUser && u.ClientId == ticket.ClientId)) ModelState.AddModelError("RequesterId", "Select a requester belonging to the selected company.");
        if (ticket.AssigneeId.HasValue && !await db.Users.AnyAsync(u => u.Id == ticket.AssigneeId && u.UserType == UserType.AitsStaff)) ModelState.AddModelError("AssigneeId", "Select an AITS support staff member.");
        if (ticket.AssigneeId.HasValue && existing?.AssigneeId != ticket.AssigneeId && !await db.AitsStaff.AnyAsync(s => s.Id == ticket.AssigneeId && s.IsActive))
            ModelState.AddModelError("AssigneeId", "Select an active AITS support staff member.");
        if (ticket.AssigneeId.HasValue && (existing?.AssigneeId != ticket.AssigneeId || existing?.ProjectId != ticket.ProjectId)
            && !await db.StaffProjects.AnyAsync(m => m.StaffId == ticket.AssigneeId && m.ProjectId == ticket.ProjectId))
            ModelState.AddModelError("AssigneeId", "Select a staff member assigned to this project, or leave the ticket unassigned.");
        if (!await db.Companies.AnyAsync(c => c.Id == ticket.ClientId)) ModelState.AddModelError("ClientId", "Select an existing company.");
        TicketPhoto? uploadedPhoto = null;
        if (photo is not null) {
            var result = await TicketPhotoUpload.ReadAsync(photo, HttpContext.RequestAborted);
            uploadedPhoto = result.Photo;
            if (result.Error is not null) ModelState.AddModelError("photo", result.Error);
        }
        if (!access.IsAdmin) { tatHours = null; ModelState.Remove("tatHours"); }
        if (tatHours.HasValue && (tatHours < 1 || tatHours > 8760)) ModelState.AddModelError("tatHours", "TAT must be between 1 and 8760 calendar hours.");
        if (!ModelState.IsValid) { await LoadOptions(ticket.Id); return View("Edit", ticket); }
        var priorityTargets = await tatPolicy.ReadAsync();
        var previousStatus = existing?.Status;
        var previousOwner = existing?.AssigneeId;
        var previousPriority = existing?.Priority;
        var previousDeadline = existing?.ResolutionDueAt;
        var entity = existing ?? ticket;
        if (existing is null) {
            entity.ResolutionDueAt = entity.CreatedAt.AddHours(priorityTargets.Hours(entity.Priority));
            entity.SupportLevel = SupportLevel.L1;
            entity.ClosedAt = ticket.Status is TicketStatus.Resolved or TicketStatus.Closed ? DateTime.UtcNow : null;
            db.Tickets.Add(entity);
        }
        else { existing.ProjectId = ticket.ProjectId; existing.ClientId = ticket.ClientId; existing.Title = ticket.Title; existing.Description = ticket.Description; existing.Status = ticket.Status; existing.Priority = ticket.Priority; existing.RequesterId = ticket.RequesterId; existing.AssigneeId = ticket.AssigneeId; existing.ClosedAt = ticket.Status is TicketStatus.Resolved or TicketStatus.Closed ? existing.ClosedAt ?? DateTime.UtcNow : null; existing.UpdatedAt = DateTime.UtcNow; }
        if (existing is not null) {
            if ((!entity.HasCustomTat && previousPriority != entity.Priority) || entity.ResolutionDueAt is null || (access.IsAdmin && entity.HasCustomTat && !tatHours.HasValue)) entity.ResolutionDueAt = entity.CreatedAt.AddHours(priorityTargets.Hours(entity.Priority));
            if (entity.Status == TicketStatus.InProgress && entity.WorkStartedAt is null) entity.WorkStartedAt = DateTime.UtcNow;
        }
        if (access.IsAdmin) {
            entity.HasCustomTat = tatHours.HasValue;
            if (tatHours.HasValue) entity.ResolutionDueAt = entity.CreatedAt.AddHours(tatHours.Value);
        }
        if (existing is not null && previousDeadline != entity.ResolutionDueAt) db.TicketActivities.Add(new TicketActivity { Ticket = entity, ActorName = (User.Identity?.Name ?? "Admin"), Message = $"Resolution TAT updated. Deadline (UTC): {entity.ResolutionDueAt:dd MMM yyyy, HH:mm}." });
        if (uploadedPhoto is not null || removePhoto) {
            var currentPhoto = existing is null ? null : await db.TicketPhotos.SingleOrDefaultAsync(p => p.TicketId == entity.Id);
            if (uploadedPhoto is not null) {
                if (currentPhoto is null) { uploadedPhoto.Ticket = entity; db.TicketPhotos.Add(uploadedPhoto); }
                else { currentPhoto.Data = uploadedPhoto.Data; currentPhoto.ContentType = uploadedPhoto.ContentType; currentPhoto.FileName = uploadedPhoto.FileName; currentPhoto.UploadedAt = DateTime.UtcNow; }
            } else if (currentPhoto is not null) db.TicketPhotos.Remove(currentPhoto);
        }
        // Add recipient rows before saving so the ticket, optional photo and email queue commit together.
        if (entity.AssigneeId.HasValue && !await db.StaffProjects.AnyAsync(m => m.ProjectId == entity.ProjectId && m.StaffId == entity.AssigneeId && m.SupportLevel == entity.SupportLevel && m.Staff!.IsActive))
            entity.AssigneeId = null;
        if (existing is not null) {
            if (previousStatus != entity.Status) db.TicketActivities.Add(new TicketActivity { Ticket = entity, ActorName = User.Identity?.Name ?? "Admin", Message = $"Status changed from {previousStatus} to {entity.Status}." });
            if (previousOwner != entity.AssigneeId) {
                var ownerName = entity.AssigneeId.HasValue ? await db.Users.Where(u => u.Id == entity.AssigneeId).Select(u => u.Name).SingleAsync() : "Unassigned";
                db.TicketActivities.Add(new TicketActivity { Ticket = entity, ActorName = User.Identity?.Name ?? "Admin", Message = "Assigned staff: " + ownerName + "." });
            }
            if (previousPriority != entity.Priority) db.TicketActivities.Add(new TicketActivity { Ticket = entity, ActorName = User.Identity?.Name ?? "Admin", Message = $"Priority changed to {entity.Priority}; resolution target is {(entity.ResolutionDueAt!.Value - entity.CreatedAt).TotalHours:0.#} calendar hours from creation." });
        }
        var queuedEmails = existing is null ? await emails.QueueAsync(entity) : 0;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to save. A selected company or user may have changed. Reload and try again."); await LoadOptions(ticket.Id); return View("Edit", ticket); }
        TempData["Message"] = queuedEmails > 0 ? $"Ticket saved. Email notifications queued for {queuedEmails} assigned staff {(queuedEmails == 1 ? "member" : "members")}." : "Ticket saved successfully.";
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }
    public async Task<IActionResult> Details(int id)
    {
        var ticket = await access.Tickets.AsNoTracking().Include(t => t.Comments.OrderBy(c => c.CreatedAt)).SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        ViewBag.EmailNotifications = access.IsAdmin
            ? await db.TicketEmailNotifications.AsNoTracking().Where(n => n.TicketId == id).OrderBy(n => n.Id).ToListAsync()
            : new List<TicketEmailNotification>();
        ViewBag.Activities = await db.TicketActivities.AsNoTracking().Where(a => a.TicketId == id).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        ViewBag.Escalations = await db.TicketEscalations.AsNoTracking().Include(e => e.Staff).Where(e => e.TicketId == id).OrderBy(e => e.CreatedAt).ToListAsync();
        await LoadOptions(ticket.Id); return View(ticket);
    }
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryEmail(int id)
    {
        var notification = await db.TicketEmailNotifications.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id);
        if (notification is null) return NotFound();
        var updated = await db.TicketEmailNotifications.Where(n => n.Id == id && n.Status == TicketEmailStatus.Failed)
            .ExecuteUpdateAsync(update => update.SetProperty(n => n.Status, TicketEmailStatus.Pending)
                .SetProperty(n => n.Attempts, 0).SetProperty(n => n.NextAttemptAt, DateTime.UtcNow)
                .SetProperty(n => n.LeaseUntil, (DateTime?)null).SetProperty(n => n.LastError, (string?)null));
        if (updated == 0) TempData["Error"] = "Only failed notifications can be retried. Sent emails are not queued again.";
        else TempData["Message"] = "Email queued for another attempt. Staff access will be checked again before delivery.";
        return RedirectToAction(nameof(Details), new { id = notification.TicketId });
    }
    public async Task<IActionResult> Photo(int id)
    {
        if (!await access.Tickets.AnyAsync(t => t.Id == id)) return NotFound();
        var photo = await db.TicketPhotos.AsNoTracking().SingleOrDefaultAsync(p => p.TicketId == id);
        if (photo is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return File(photo.Data, photo.ContentType);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Comment(int id, string? body)
    {
        var ticket = await access.Tickets.SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        if (string.IsNullOrWhiteSpace(body) || body.Length > 3000) { TempData["Error"] = "Enter a comment between 1 and 3,000 characters."; return RedirectToAction(nameof(Details), new { id }); }
        db.Comments.Add(new TicketComment { TicketId = id, Body = body.Trim(), AuthorName = User.Identity?.Name, AuthorRole = access.IsAdmin ? "Admin" : access.IsInternal ? "AITS staff" : "Client user" });
        ticket.UpdatedAt = DateTime.UtcNow;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { TempData["Error"] = "The comment could not be saved. The ticket may have been removed."; }
        return RedirectToAction(nameof(Details), new { id });
    }
    [Authorize(Roles = "AitsUser"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Escalate(int id, SupportLevel level, string? reason)
    {
        var ticket = await access.Tickets.SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        if (!Enum.IsDefined(level) || level <= ticket.SupportLevel || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) {
            TempData["Error"] = "Choose a higher support level and enter a reason (up to 1,000 characters).";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (ticket.Status is TicketStatus.Resolved or TicketStatus.Closed) {
            TempData["Error"] = "A resolved or closed ticket cannot be escalated.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (ticket.AssigneeId.HasValue && ticket.AssigneeId != access.UserId) return Forbid();
        if (!await db.StaffProjects.AnyAsync(m => m.ProjectId == ticket.ProjectId && m.SupportLevel == level && m.Staff!.IsActive)) {
            TempData["Error"] = "No active team members are assigned to that level. Contact Admin.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var previous = ticket.SupportLevel;
        // Optimistic concurrency protects the owner and current routing level during handover.
        ticket.SupportLevel = level; ticket.AssigneeId = null; ticket.UpdatedAt = DateTime.UtcNow;
        db.TicketEscalations.Add(new TicketEscalation { TicketId = id, StaffId = access.UserId, FromLevel = previous, ToLevel = level, Reason = reason.Trim() });
        db.TicketActivities.Add(new TicketActivity { TicketId = id, ActorName = User.Identity?.Name ?? "AITS staff", Message = $"Escalated from {previous} to {level}. Waiting for the {level} team to take ownership." });
        await emails.QueueAsync(ticket);
        try { await db.SaveChangesAsync(); TempData["Message"] = $"Ticket escalated to {level}. The next team can take ownership."; }
        catch (DbUpdateException) { TempData["Error"] = "The ticket or project team changed during escalation. Refresh and try again."; }
        return RedirectToAction(nameof(Details), new { id });
    }
    [Authorize(Roles = "AitsUser"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> WorkStatus(int id, TicketStatus status)
    {
        var ticket = await access.Tickets.SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        if (ticket.AssigneeId != access.UserId) return Forbid();
        if (!((ticket.Status == TicketStatus.Open && status == TicketStatus.InProgress) || (ticket.Status == TicketStatus.InProgress && status == TicketStatus.Resolved)))
            return BadRequest("Start work on an open ticket before marking it resolved.");
        var now = DateTime.UtcNow;
        ticket.Status = status; ticket.UpdatedAt = now;
        if (status == TicketStatus.InProgress) ticket.WorkStartedAt ??= now;
        if (status == TicketStatus.Resolved) ticket.ClosedAt = now;
        db.TicketActivities.Add(new TicketActivity { TicketId = id, ActorName = User.Identity?.Name ?? "AITS staff", Message = status == TicketStatus.InProgress ? "Started working on this ticket." : "Resolved this ticket." });
        try { await db.SaveChangesAsync(); TempData["Message"] = status == TicketStatus.Resolved ? "Ticket resolved. The client can view the resolution time." : "Work started. The client can see your progress."; }
        catch (DbUpdateException) { TempData["Error"] = "The ticket changed. Refresh and try again."; }
        return RedirectToAction(nameof(Details), new { id });
    }
    [Authorize(Roles = "AitsUser"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(int id)
    {
        if (!await access.Tickets.AnyAsync(t => t.Id == id)) return NotFound();
        var eligible = await access.Tickets.AnyAsync(t => t.Id == id && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed && db.StaffProjects.Any(m => m.ProjectId == t.ProjectId && m.StaffId == access.UserId && m.SupportLevel == t.SupportLevel && m.Staff!.IsActive));
        if (!eligible) return Forbid();
        var staffId = access.UserId;
        var now = DateTime.UtcNow;
        // Conditional update ensures that two simultaneous claims cannot replace each other.
        await using var transaction = await db.Database.BeginTransactionAsync();
        var claimed = await access.Tickets.Where(t => t.Id == id && t.AssigneeId == null && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed && db.StaffProjects.Any(m => m.ProjectId == t.ProjectId && m.StaffId == staffId && m.SupportLevel == t.SupportLevel && m.Staff!.IsActive))
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.AssigneeId, (int?)staffId)
                .SetProperty(t => t.UpdatedAt, now));
        if (claimed == 1) {
            db.TicketActivities.Add(new TicketActivity { TicketId = id, ActorName = User.Identity?.Name ?? "AITS staff", Message = "Took ownership of this ticket." });
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
        TempData[claimed == 1 ? "Message" : "Error"] = claimed == 1
            ? "You are now responsible for this ticket. Your name is shown under Assigned to."
            : "This ticket already has an owner. Refresh to see who is handling it.";
        return RedirectToAction(nameof(Details), new { id });
    }
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var ticket = await access.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);
        return ticket is null ? NotFound() : View(ticket);
    }
    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
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




