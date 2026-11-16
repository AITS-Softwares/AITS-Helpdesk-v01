using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Help_Desk.Services;

public class TicketEmailDispatcher(HelpDeskDbContext db, IOptionsMonitor<TicketEmailOptions> options,
    EmailCredentialStore credentials, IWebHostEnvironment environment, TicketTatPolicy tatPolicy, ILogger<TicketEmailDispatcher> logger)
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
                .Include(n => n.Ticket)!.ThenInclude(t => t!.Attachments)
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
            if (eligible && item.Kind == TicketEmailKind.AutoEscalated) {
                var ticket = item.Ticket;
                eligible = ticket is not null && ticket.SupportLevel == item.TargetLevel &&
                    ticket.Status != TicketStatus.Resolved && ticket.Status != TicketStatus.Closed;
            }
            if (!eligible) {
                item.Status = TicketEmailStatus.Skipped;
                item.LastError = "Ticket or customer access changed; email was not sent.";
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
        var mailPolicy = await tatPolicy.ReadAsync();
        using var mail = Compose(notification, from, settings, mailPolicy, environment.ContentRootPath);
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

    private static MailMessage Compose(TicketEmailNotification notification, string from, TicketEmailOptions settings, TatPolicy policy, string contentRootPath)
    {
        var ticket = notification.Ticket!;
        var customer = ticket.Client?.Name ?? "Customer";
        var overdue = notification.Kind == TicketEmailKind.TatOverdue;
        var escalated = notification.Kind == TicketEmailKind.AutoEscalated;
        var heading = overdue ? "TAT deadline missed — your assigned ticket needs attention." : escalated ? $"Urgent ticket automatically escalated to {ticket.SupportLevel}." : null;
        var previousLevel = ticket.SupportLevel == SupportLevel.L3 ? SupportLevel.L2 : SupportLevel.L1;
        var explanation = escalated
            ? $"This ticket moved automatically from {previousLevel} to {ticket.SupportLevel} because no staff reply was recorded within the configured {previousLevel} response window. Please review it and respond promptly."
            : notification.Kind == TicketEmailKind.Created && ticket.Priority == TicketPriority.Urgent
                ? ticket.SupportLevel == SupportLevel.L1
                    ? $"Urgent response schedule: L1 has {policy.L1ResponseHours} hour(s) to respond. If no staff reply is recorded, the ticket automatically moves to L2. If L2 does not respond within {policy.L2ResponseHours} hour(s), it moves to L3. A customer reply restarts the active response window."
                    : $"Urgent response schedule: this ticket is at {ticket.SupportLevel}. If no staff reply is recorded within the configured L2 response window ({policy.L2ResponseHours} hour(s)), it automatically moves to L3. A customer reply restarts the active response window."
                : null;
        var deadline = TicketTiming.DueAt(ticket);
        var subject = (overdue ? "[TAT OVERDUE] " : escalated ? $"[URGENT ESCALATED TO {ticket.SupportLevel}] " : "") + TicketEmailSubject.Base(ticket);
        string? link = null;
        if (Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out var baseUri) &&
            (baseUri.Scheme == "https" || baseUri.Scheme == "http") && string.IsNullOrEmpty(baseUri.UserInfo) && string.IsNullOrEmpty(baseUri.Query) && string.IsNullOrEmpty(baseUri.Fragment))
            link = settings.PublicBaseUrl.TrimEnd('/') + "/Tickets/Details/" + ticket.Id;
        var text = $"{(heading is null ? "" : heading + "\n")}{(explanation is null ? "" : explanation + "\n\n")}Resolution deadline (IST): {TicketTiming.ToIndiaTime(deadline):dd MMM yyyy, hh:mm tt}\n\nTicket: #{ticket.Id:D4}\nCustomer: {customer}\nTitle: {ticket.Title}\nPriority: {ticket.Priority}\nSupport level: {ticket.SupportLevel}\nStatus: {ticket.Status}\nRequester: {ticket.Requester?.Name} ({ticket.Requester?.Email})\nCreated (IST): {TicketTiming.ToIndiaTime(ticket.CreatedAt):dd MMM yyyy, hh:mm tt}\n\nDescription:\n{ticket.Description}\n\n" +
            (link is null ? "Open AITS Helpdesk and search for this ticket number." : "View ticket: " + link + "\nSign in with your AITS account to view the ticket and any photo.");
        var intro = string.Join("\n\n", new[] { heading, explanation }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var details = new List<(string Label, string Value)> {
            ("Ticket", $"#{ticket.Id:D4}"), ("Title", ticket.Title), ("Customer", customer),
            ("Priority", ticket.Priority.ToString()), ("Support level", ticket.SupportLevel.ToString()),
            ("Status", ticket.Status.ToString()), ("Requester", $"{ticket.Requester?.Name} · {ticket.Requester?.Email}"),
            ("Created (IST)", TicketTiming.ToIndiaTime(ticket.CreatedAt).ToString("dd MMM yyyy, hh:mm tt")),
            ("Resolution deadline (IST)", TicketTiming.ToIndiaTime(deadline).ToString("dd MMM yyyy, hh:mm tt"))
        };
        var html = TicketEmailHtml.Render($"Ticket #{ticket.Id:D4}", intro, details,
            messageTitle: "Description", message: ticket.Description,
            actionLabel: link is null ? null : "Open ticket", actionUrl: link,
            footer: link is null ? "Open AITS Helpdesk and search for this ticket number. Sign in with your AITS account to view the ticket and any attachment." : "Sign in with your AITS account to view the ticket and any attachment.");
        var mail = new MailMessage { From = new MailAddress(from, settings.FromName), Subject = subject, SubjectEncoding = Encoding.UTF8, Body = text, BodyEncoding = Encoding.UTF8 };
        mail.To.Add(notification.RecipientEmail);
        if (notification.Kind == TicketEmailKind.Created) {
            foreach (var file in ticket.Attachments) {
                var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
                mail.Attachments.Add(new Attachment(new MemoryStream(file.Data, writable: false), file.FileName, contentType));
            }
        }
        mail.AlternateViews.Add(TicketEmailHtml.CreateView(html));
        mail.Headers.Add("Message-ID", $"<aits-ticket-{notification.TicketId}-staff-{notification.StaffId}-kind-{(int)notification.Kind}-notification-{notification.Id}@{new MailAddress(from).Host}>");
        return mail;
    }
}
