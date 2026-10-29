using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.Encodings.Web;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Help_Desk.Services;

public class TicketEmailDispatcher(HelpDeskDbContext db, IOptionsMonitor<TicketEmailOptions> options,
    EmailCredentialStore credentials, IWebHostEnvironment environment, ILogger<TicketEmailDispatcher> logger)
{
    public async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.Enabled) return;
        var now = DateTime.UtcNow;
        var ids = await db.TicketEmailNotifications.AsNoTracking().Where(n =>
            ((n.Status == TicketEmailStatus.Pending || n.Status == TicketEmailStatus.Retry) && n.NextAttemptAt <= now) ||
            (n.Status == TicketEmailStatus.Processing && (n.LeaseUntil == null || n.LeaseUntil <= now)))
            .OrderBy(n => n.CreatedAt).Select(n => n.Id).Take(20).ToListAsync(cancellationToken);
        foreach (var id in ids) {
            cancellationToken.ThrowIfCancellationRequested();
            now = DateTime.UtcNow;
            var claimed = await db.TicketEmailNotifications.Where(n => n.Id == id &&
                (((n.Status == TicketEmailStatus.Pending || n.Status == TicketEmailStatus.Retry) && n.NextAttemptAt <= now) ||
                (n.Status == TicketEmailStatus.Processing && (n.LeaseUntil == null || n.LeaseUntil <= now))))
                .ExecuteUpdateAsync(update => update.SetProperty(n => n.Status, TicketEmailStatus.Processing)
                    .SetProperty(n => n.LeaseUntil, now.AddMinutes(2)).SetProperty(n => n.Attempts, n => n.Attempts + 1), cancellationToken);
            if (claimed == 0) continue;
            var item = await db.TicketEmailNotifications.Include(n => n.Ticket)!.ThenInclude(t => t!.Client)
                .Include(n => n.Ticket)!.ThenInclude(t => t!.Project)
                .Include(n => n.Ticket)!.ThenInclude(t => t!.Requester)
                .SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
            if (item is null) continue;
            // Recheck current membership, activity, account approval and address immediately before delivery.
            var eligible = item.StaffId.HasValue && await db.StaffProjects.AnyAsync(m => m.StaffId == item.StaffId &&
                m.ProjectId == item.ProjectId && (item.TargetLevel == null || m.SupportLevel == item.TargetLevel) && m.Project!.ClientId == item.CompanyId && m.Staff!.IsActive &&
                m.Staff.Email == item.RecipientEmail && db.InternalAccounts.Any(a => a.DeskUserId == m.StaffId &&
                    a.IsApproved && a.IsActive && a.Role == AccountRole.AitsUser && a.Email == item.RecipientEmail) &&
                db.Tickets.Any(t => t.Id == item.TicketId && t.ClientId == item.CompanyId && t.ProjectId == item.ProjectId), cancellationToken);
            if (eligible && item.Kind == TicketEmailKind.TatOverdue) {
                var ticket = item.Ticket;
                eligible = ticket is not null && ticket.AssigneeId == item.StaffId &&
                    ticket.Status != TicketStatus.Resolved && ticket.Status != TicketStatus.Closed &&
                    TicketTiming.DueAt(ticket) == item.AlertDueAt && item.AlertDueAt <= DateTime.UtcNow;
            }
            if (!eligible) {
                item.Status = TicketEmailStatus.Skipped;
                item.LastError = "Ticket or project access changed; email was not sent.";
                item.LeaseUntil = null;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }
            try {
                await SendAsync(item, cancellationToken);
                item.Status = TicketEmailStatus.Sent;
                item.SentAt = DateTime.UtcNow;
                item.LastError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) {
                item.Status = item.Attempts >= Math.Clamp(options.CurrentValue.MaxAttempts, 1, 10) ? TicketEmailStatus.Failed : TicketEmailStatus.Retry;
                item.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, item.Attempts - 1)));
                item.LastError = "Delivery failed. Check SMTP settings and sender credentials. (" + exception.GetType().Name + ")";
                // SMTP exception messages can include provider/account data; never log credentials or message contents.
                logger.LogWarning("Ticket email {NotificationId}, attempt {Attempt}, failed ({ErrorType}).", item.Id, item.Attempts, exception.GetType().Name);
            }
            item.LeaseUntil = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SendAsync(TicketEmailNotification notification, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var from = string.IsNullOrWhiteSpace(settings.FromAddress) ? settings.UserName : settings.FromAddress;
        using var mail = Compose(notification, from, settings);
        using var smtp = new SmtpClient();
        if (string.Equals(settings.Transport, "Pickup", StringComparison.OrdinalIgnoreCase)) {
            if (!environment.IsDevelopment()) throw new InvalidOperationException("Pickup delivery is only allowed in Development.");
            var directory = Path.Combine(environment.ContentRootPath, "App_Data", "email-pickup");
            Directory.CreateDirectory(directory);
            smtp.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            smtp.PickupDirectoryLocation = directory;
        } else {
            if (!string.Equals(settings.Transport, "Smtp", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unknown email transport.");
            var password = settings.Password ?? credentials.Read();
            if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.UserName) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("SMTP credentials are not configured.");
            smtp.Host = settings.Host; smtp.Port = settings.Port;
            smtp.EnableSsl = true;
            smtp.UseDefaultCredentials = false;
            smtp.Credentials = new NetworkCredential(settings.UserName, password);
            smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(mail, timeout.Token);
    }

    private static MailMessage Compose(TicketEmailNotification notification, string from, TicketEmailOptions settings)
    {
        var ticket = notification.Ticket!;
        var company = ticket.Client?.Name ?? "Company";
        var project = ticket.Project?.Name ?? "Project";
        var overdue = notification.Kind == TicketEmailKind.TatOverdue;
        var heading = overdue ? "TAT deadline missed — your assigned ticket needs attention." : "A ticket requires attention from your project support team.";
        var deadline = TicketTiming.DueAt(ticket);
        var subject = (overdue ? "[TAT OVERDUE] " : "") + $"[Ticket #{ticket.Id:D4}] {company} / {project}: {ticket.Title}".Replace('\r', ' ').Replace('\n', ' ');
        string? link = null;
        if (Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var baseUri) &&
            (baseUri.Scheme == "https" || baseUri.Scheme == "http") && string.IsNullOrEmpty(baseUri.UserInfo) && string.IsNullOrEmpty(baseUri.Query) && string.IsNullOrEmpty(baseUri.Fragment))
            link = settings.PublicBaseUrl.TrimEnd('/') + "/Tickets/Details/" + ticket.Id;
        var text = $"{heading}\nResolution deadline (UTC): {deadline:dd MMM yyyy, HH:mm}\n\nTicket: #{ticket.Id:D4}\nCompany: {company}\nProject: {project}\nTitle: {ticket.Title}\nPriority: {ticket.Priority}\nSupport level: {ticket.SupportLevel}\nStatus: {ticket.Status}\nRequester: {ticket.Requester?.Name} ({ticket.Requester?.Email})\nCreated (UTC): {ticket.CreatedAt:dd MMM yyyy, HH:mm}\n\nDescription:\n{ticket.Description}\n\n" +
            (link is null ? "Open AITS Helpdesk and search for this ticket number." : "View ticket: " + link + "\nSign in with your AITS account to view the ticket and any photo.");
        static string H(string? value) => HtmlEncoder.Default.Encode(value ?? "");
        var color = ticket.Priority switch { TicketPriority.Urgent => "#ae213d", TicketPriority.High => "#9a4c00", TicketPriority.Low => "#245ca7", _ => "#725b00" };
        var html = $"<div style='font-family:Arial,sans-serif;color:#222a40;max-width:640px;margin:auto'><h2 style='color:#6255d9'>AITS Helpdesk · Ticket #{ticket.Id:D4}</h2><p>{H(heading)}</p><p><strong>Resolution deadline (UTC): {deadline:dd MMM yyyy, HH:mm}</strong></p><h3>{H(ticket.Title)}</h3><table cellpadding='8' style='border-collapse:collapse;background:#f5f6fa;width:100%'><tr><td>Company</td><td>{H(company)}</td></tr><tr><td>Project</td><td>{H(project)}</td></tr><tr><td>Priority</td><td style='color:{color};font-weight:bold'>{ticket.Priority}</td></tr><tr><td>Support level</td><td>{ticket.SupportLevel}</td></tr><tr><td>Status</td><td>{ticket.Status}</td></tr><tr><td>Requester</td><td>{H(ticket.Requester?.Name)} · {H(ticket.Requester?.Email)}</td></tr><tr><td>Created (UTC)</td><td>{ticket.CreatedAt:dd MMM yyyy, HH:mm}</td></tr></table><h4>Description</h4><p style='white-space:pre-wrap'>{H(ticket.Description)}</p>" +
            (link is null ? "<p>Open AITS Helpdesk and search for this ticket number.</p>" : $"<p><a href='{H(link)}' style='color:#6255d9'>Open ticket →</a></p><small>Sign in with your AITS account to view the ticket and any photo.</small>") + "</div>";
        var mail = new MailMessage { From = new MailAddress(from, settings.FromName), Subject = subject, SubjectEncoding = Encoding.UTF8, Body = text, BodyEncoding = Encoding.UTF8 };
        mail.To.Add(notification.RecipientEmail);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));
        mail.Headers.Add("Message-ID", $"<aits-ticket-{notification.TicketId}-staff-{notification.StaffId}-kind-{(int)notification.Kind}-notification-{notification.Id}@{new MailAddress(from).Host}>");
        return mail;
    }
}

