using Microsoft.AspNetCore.Authorization;
using Help_Desk.Services;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Controllers;

[Authorize]
public class TicketsController(HelpDeskDbContext db, CurrentAccess access, TicketCreatedEmails emails, TicketTatPolicy tatPolicy, TicketReplyEmails replyEmails) : Controller
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
        ViewBag.StaffMemberships = await db.StaffProjects.AsNoTracking().Include(m => m.Project).Where(m => m.Staff!.IsActive &&
            db.InternalAccounts.Any(a => a.DeskUserId == m.StaffId && a.Role == AccountRole.AitsUser && a.IsApproved && a.IsActive && a.Email == m.Staff.Email)).ToListAsync();
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
    public async Task<IActionResult> Save([Bind("Id,Title,Description,Status,Priority,RequesterId,AssigneeId,ClientId")] Ticket ticket, IFormFile? photo, bool removePhoto = false, int? tatHours = null)
    {
        if (ticket.Id != 0 && !access.IsAdmin) return Forbid();
        var existing = ticket.Id == 0 ? null : await access.Tickets.SingleOrDefaultAsync(t => t.Id == ticket.Id);
        if (ticket.Id != 0 && existing is null) return NotFound();
        ticket.ProjectId = existing?.ProjectId;
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
            if (!await access.TicketCompanies.AnyAsync(c => c.Id == ticket.ClientId))
                ModelState.AddModelError("ClientId", "Select a customer assigned to your agent account.");
        }
        var projects = await access.Projects.Where(p => p.ClientId == ticket.ClientId).OrderBy(p => p.Id).ToListAsync();
        // Projects are no longer exposed in the ticket workflow. Keep legacy routing data
        // intact and use the customer's first existing support route for new tickets.
        if (ticket.ProjectId is null && projects.Count > 0) {
            ticket.ProjectId = projects[0].Id;
            ModelState.Remove("ProjectId");
        }
        if (ticket.ProjectId.HasValue && !await db.Projects.AnyAsync(p => p.Id == ticket.ProjectId && p.ClientId == ticket.ClientId))
            ticket.ProjectId = null;
        ticket.Title = ticket.Title?.Trim() ?? ""; ticket.Description = ticket.Description?.Trim() ?? "";
        if (!await db.Users.AnyAsync(u => u.Id == ticket.RequesterId && u.UserType == UserType.ClientUser && u.ClientId == ticket.ClientId)) ModelState.AddModelError("RequesterId", "Select a requester belonging to the selected customer.");
        if (ticket.AssigneeId.HasValue && !await db.Users.AnyAsync(u => u.Id == ticket.AssigneeId && u.UserType == UserType.AitsStaff)) ModelState.AddModelError("AssigneeId", "Select an agent.");
        if (ticket.AssigneeId.HasValue && existing?.AssigneeId != ticket.AssigneeId && !await db.AitsStaff.AnyAsync(s => s.Id == ticket.AssigneeId && s.IsActive))
            ModelState.AddModelError("AssigneeId", "Select an active agent.");
        if (ticket.AssigneeId.HasValue && !await db.StaffProjects.AnyAsync(m => m.StaffId == ticket.AssigneeId && m.ProjectId == ticket.ProjectId))
            ModelState.AddModelError("AssigneeId", "Select an agent assigned to this customer, or leave the ticket unassigned.");
        if (!await db.Companies.AnyAsync(c => c.Id == ticket.ClientId)) ModelState.AddModelError("ClientId", "Select an existing customer.");
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
                db.TicketActivities.Add(new TicketActivity { Ticket = entity, ActorName = User.Identity?.Name ?? "Admin", Message = "Assigned agent: " + ownerName + "." });
            }
            if (previousPriority != entity.Priority) db.TicketActivities.Add(new TicketActivity { Ticket = entity, ActorName = User.Identity?.Name ?? "Admin", Message = $"Priority changed to {entity.Priority}; resolution target is {(entity.ResolutionDueAt!.Value - entity.CreatedAt).TotalHours:0.#} calendar hours from creation." });
        }
        var queuedEmails = existing is null ? await emails.QueueAsync(entity) : 0;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "Unable to save. A selected customer or user may have changed. Reload and try again."); await LoadOptions(ticket.Id); return View("Edit", ticket); }
        TempData["Message"] = existing is null
            ? queuedEmails > 0
                ? $"New ticket #{entity.Id:D4} created. Email notifications queued for {queuedEmails} assigned agent{(queuedEmails == 1 ? "" : "s")}."
                : $"New ticket #{entity.Id:D4} created successfully."
            : "Ticket saved successfully.";
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }
    public async Task<IActionResult> Details(int id)
    {
        var ticket = await access.Tickets.AsNoTracking().Include(t => t.Comments.OrderBy(c => c.CreatedAt)).SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        ViewBag.EmailNotifications = access.IsAdmin
            ? await db.TicketEmailNotifications.AsNoTracking().Where(n => n.TicketId == id).OrderBy(n => n.Id).ToListAsync()
            : new List<TicketEmailNotification>();
        ViewBag.Escalations = await db.TicketEscalations.AsNoTracking().Include(e => e.Staff).Where(e => e.TicketId == id).OrderBy(e => e.CreatedAt).ToListAsync();
        ViewBag.Attachments = await db.TicketAttachments.AsNoTracking().Where(a => a.TicketId == id)
            .OrderBy(a => a.UploadedAt).Select(a => new TicketAttachmentInfo { Id = a.Id, FileName = a.FileName, ContentType = a.ContentType, UploadedAt = a.UploadedAt, CommentId = a.CommentId }).ToListAsync();
        ViewBag.AssignableStaff = ticket.ProjectId.HasValue
            ? await db.AitsStaff.AsNoTracking().Where(staff => staff.IsActive &&
                db.StaffProjects.Any(m => m.StaffId == staff.Id && m.ProjectId == ticket.ProjectId && m.Project!.ClientId == ticket.ClientId) &&
                db.InternalAccounts.Any(a => a.DeskUserId == staff.Id && a.Role == AccountRole.AitsUser && a.IsApproved && a.IsActive && a.Email == staff.Email))
                .OrderBy(staff => db.StaffProjects.Where(m => m.StaffId == staff.Id && m.ProjectId == ticket.ProjectId)
                    .Select(m => (int?)m.SupportLevel).FirstOrDefault() ?? 4)
                .ThenBy(staff => staff.Name).ToListAsync()
            : new List<AitsStaffUser>();
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
        else TempData["Message"] = "Email queued for another attempt. Agent access will be checked again before delivery.";
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
    public async Task<IActionResult> Attachment(int id)
    {
        if (!await access.Tickets.AnyAsync(ticket => ticket.Attachments.Any(attachment => attachment.Id == id))) return NotFound();
        var attachment = await db.TicketAttachments.AsNoTracking().SingleOrDefaultAsync(file => file.Id == id);
        if (attachment is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return File(attachment.Data, attachment.ContentType, attachment.FileName);
    }
    public async Task<IActionResult> AttachmentPreview(int id)
    {
        if (!await access.Tickets.AnyAsync(ticket => ticket.Attachments.Any(attachment => attachment.Id == id))) return NotFound();
        var attachment = await db.TicketAttachments.AsNoTracking().SingleOrDefaultAsync(file => file.Id == id);
        if (attachment is null) return NotFound();
        var previewTypes = new[] { "image/jpeg", "image/png", "image/gif", "image/webp", "application/pdf" };
        if (!previewTypes.Contains(attachment.ContentType, StringComparer.OrdinalIgnoreCase)) return BadRequest();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return File(attachment.Data, attachment.ContentType);
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(26 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 26 * 1024 * 1024)]
    public async Task<IActionResult> Comment(int id, string? body, IFormFile[]? files)
    {
        var ticket = await access.Tickets.Include(t => t.Requester).Include(t => t.Client).SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        files ??= [];
        var uploads = files.Where(file => file.Length > 0).ToList();
        if (string.IsNullOrWhiteSpace(body) && uploads.Count == 0 || body?.Length > 3000) {
            TempData["Error"] = "Write a reply or attach a file. Replies can be up to 3,000 characters.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (uploads.Count > 10 || uploads.Any(file => file.Length > 10 * 1024 * 1024) || uploads.Sum(file => file.Length) > 25 * 1024 * 1024) {
            TempData["Error"] = "Attach up to 10 files, with a 10 MB per-file and 25 MB total limit.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var reply = body?.Trim() ?? "";
        var comment = new TicketComment { TicketId = id, Body = reply, AuthorName = User.Identity?.Name, AuthorRole = access.IsAdmin ? "Admin" : access.IsInternal ? "AITS staff" : "Customer user" };
        foreach (var file in uploads) {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, HttpContext.RequestAborted);
            var fileName = Path.GetFileName(file.FileName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "attachment";
            if (fileName.Length > 255) fileName = fileName[^255..];
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType.Length > 160 ? "application/octet-stream" : file.ContentType;
            comment.Attachments.Add(new TicketAttachment { TicketId = id, FileName = fileName, ContentType = contentType, Data = stream.ToArray() });
        }
        db.Comments.Add(comment);
        ticket.UpdatedAt = DateTime.UtcNow;
        try {
            await db.SaveChangesAsync();
            if (access.IsInternal || access.IsAdmin) {
                var sentMessageId = await replyEmails.SendToRequesterAsync(ticket, reply, comment.Attachments, HttpContext.RequestAborted);
                if (sentMessageId is not null) {
                    ticket.EmailThreadMessageId = sentMessageId;
                    await db.SaveChangesAsync();
                    TempData["Message"] = $"Reply sent to {ticket.Requester?.Email}.";
                } else TempData["Message"] = "Reply saved. Email notifications are disabled in settings.";
            } else TempData["Message"] = "Reply saved successfully.";
        }
        catch (DbUpdateException) { TempData["Error"] = "The comment could not be saved. The ticket may have been removed."; }
        catch (Exception) { TempData["Error"] = "Reply saved, but the email could not be sent. Check the outgoing email settings and try again."; }
        return RedirectToAction(nameof(Details), new { id });
    }
    [Authorize(Roles = "AitsUser"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> WorkStatus(int id, TicketStatus status)
    {
        var ticket = await access.Tickets.SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        var canUpdateStatus = ticket.ProjectId.HasValue && await db.StaffProjects.AnyAsync(m => m.ProjectId == ticket.ProjectId && m.StaffId == access.UserId && m.Project!.ClientId == ticket.ClientId && m.Staff!.IsActive);
        if (!canUpdateStatus) return Forbid();
        if (!Enum.IsDefined(status)) return BadRequest("Choose a valid ticket status.");
        var now = DateTime.UtcNow;
        var previous = ticket.Status;
        ticket.Status = status; ticket.UpdatedAt = now;
        if (status == TicketStatus.InProgress && previous != TicketStatus.InProgress) ticket.WorkStartedAt = now;
        else if (status == TicketStatus.Open) ticket.WorkStartedAt = null;
        ticket.ClosedAt = status is TicketStatus.Resolved or TicketStatus.Closed ? now : null;
        db.TicketActivities.Add(new TicketActivity { TicketId = id, ActorName = User.Identity?.Name ?? "AITS staff", Message = $"Status changed from {previous} to {status}." });
        try { await db.SaveChangesAsync(); TempData["Message"] = $"Ticket status changed to {status}."; }
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
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int id, int? assigneeId, string? search, TicketStatus? status, TicketPriority? priority, int? clientId, int? projectId, bool returnToDetails = false)
    {
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        if (ticket.Status is TicketStatus.Resolved or TicketStatus.Closed) TempData["Error"] = "Reopen the ticket before changing its assignment.";
        else {
            SupportLevel? destinationLevel = null;
            if (assigneeId.HasValue && ticket.ProjectId.HasValue) {
                destinationLevel = await db.StaffProjects.AsNoTracking()
                    .Where(m => m.StaffId == assigneeId && m.ProjectId == ticket.ProjectId && m.Project!.ClientId == ticket.ClientId && m.Staff!.IsActive &&
                        db.InternalAccounts.Any(a => a.DeskUserId == m.StaffId && a.Role == AccountRole.AitsUser && a.IsApproved && a.IsActive && a.Email == m.Staff.Email))
                    .Select(m => (SupportLevel?)m.SupportLevel).SingleOrDefaultAsync();
            }
            if (assigneeId.HasValue && destinationLevel is null)
                TempData["Error"] = "Choose an active, approved agent assigned to this customer's support team.";
            else {
                var previousAssigneeId = ticket.AssigneeId;
                var previousLevel = ticket.SupportLevel;
                var previousLevelChanged = destinationLevel.HasValue && destinationLevel.Value != previousLevel;
                ticket.AssigneeId = assigneeId;
                if (destinationLevel.HasValue) ticket.SupportLevel = destinationLevel.Value;
                ticket.UpdatedAt = DateTime.UtcNow;
                var assigneeName = assigneeId.HasValue ? await db.AitsStaff.Where(s => s.Id == assigneeId).Select(s => s.Name).SingleAsync() : "Unassigned";
                if (previousAssigneeId != assigneeId || previousLevelChanged) {
                    ticket.WorkStartedAt = null;
                    if (ticket.Status == TicketStatus.InProgress) ticket.Status = TicketStatus.Open;
                    var routeMessage = previousLevelChanged
                        ? $"Ticket routed from {previousLevel} to {ticket.SupportLevel} and assigned to {assigneeName}."
                        : $"Assigned ticket to {assigneeName}. Work is waiting to start under the new owner.";
                    db.TicketActivities.Add(new TicketActivity { TicketId = ticket.Id, ActorName = User.Identity?.Name ?? "Admin", Message = routeMessage });
                }
                if (previousLevelChanged) await emails.QueueAsync(ticket);
                try { await db.SaveChangesAsync(); TempData["Message"] = assigneeId.HasValue ? $"Ticket assigned to {assigneeName} ({ticket.SupportLevel})." : "Ticket is now unassigned."; }
                catch (DbUpdateException) { TempData["Error"] = "The ticket assignment could not be saved. Refresh and try again."; }
            }
        }
        return returnToDetails ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(Index), new { search, status, priority, clientId, projectId });
    }
    [Authorize(Roles = "Admin"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, TicketStatus status)
    {
        var ticket = await access.Tickets.SingleOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        if (!Enum.IsDefined(status)) {
            TempData["Error"] = "Choose a valid ticket status.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (ticket.Status != status) {
            var previous = ticket.Status;
            var now = DateTime.UtcNow;
            ticket.Status = status;
            ticket.UpdatedAt = now;
            ticket.ClosedAt = status is TicketStatus.Resolved or TicketStatus.Closed ? now : null;
            if (status == TicketStatus.InProgress) ticket.WorkStartedAt ??= now;
            db.TicketActivities.Add(new TicketActivity { TicketId = id, ActorName = User.Identity?.Name ?? "Admin", Message = $"Status changed from {previous} to {status}." });
            try { await db.SaveChangesAsync(); TempData["Message"] = $"Ticket status changed to {status}."; }
            catch (DbUpdateException) { TempData["Error"] = "The ticket status could not be saved. Refresh and try again."; }
        }
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




