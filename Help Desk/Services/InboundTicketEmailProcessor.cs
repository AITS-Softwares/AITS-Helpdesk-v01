using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Mime;
using Help_Desk.Data;
using Help_Desk.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Help_Desk.Services;

public sealed class InboundTicketEmailProcessor(
    HelpDeskDbContext db,
    IOptionsMonitor<TicketEmailOptions> options,
    EmailCredentialStore credentials,
    TicketCreatedEmails ticketCreatedEmails,
    IWebHostEnvironment environment,
    ILogger<InboundTicketEmailProcessor> logger)
{
    private const int MaxEmailAttachmentCount = 10;
    private const int MaxEmailAttachmentBytes = 10 * 1024 * 1024;
    private const int MaxEmailAttachmentTotalBytes = 25 * 1024 * 1024;

    public async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var password = settings.Password ?? credentials.Read();
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(settings.UserName) || string.IsNullOrWhiteSpace(settings.IncomingHost))
            throw new InvalidOperationException("Incoming mailbox credentials are not configured.");

        using var client = new ImapClient();
        var socket = settings.IncomingPort == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(settings.IncomingHost, settings.IncomingPort, socket, cancellationToken);
        await client.AuthenticateAsync(settings.UserName, password, cancellationToken);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        var allUids = await inbox.SearchAsync(SearchQuery.All, cancellationToken);
        var unreadUids = await inbox.SearchAsync(SearchQuery.NotSeen, cancellationToken);
        var cursor = ReadCursor();
        var initializeCursor = cursor is null || cursor.UidValidity != inbox.UidValidity;
        // On first startup, or once after upgrading the cursor format, recover
        // recent messages even if Gmail has already marked them as read.
        var recoverRecent = initializeCursor || cursor!.RetryUids is null;
        var recentUids = recoverRecent
            ? await inbox.SearchAsync(SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-1)), cancellationToken)
            : [];
        var lastUid = initializeCursor ? (allUids.Count == 0 ? 0u : allUids.Max(uid => uid.Id)) : cursor!.LastUid;
        var retryUids = initializeCursor ? new HashSet<uint>() : (cursor!.RetryUids ?? []).ToHashSet();
        retryUids.IntersectWith(allUids.Select(uid => uid.Id));
        IEnumerable<UniqueId> candidates = initializeCursor
            ? unreadUids.Concat(recentUids).Concat(retryUids.Select(id => new UniqueId(id))).Distinct().OrderBy(uid => uid.Id)
            : allUids.Where(uid => uid.Id > lastUid).Concat(unreadUids).Concat(recentUids).Concat(retryUids.Select(id => new UniqueId(id))).Distinct().OrderBy(uid => uid.Id);
        foreach (var uid in candidates.Take(20)) {
            cancellationToken.ThrowIfCancellationRequested();
            var message = await inbox.GetMessageAsync(uid, cancellationToken);
            try {
                var sender = message.From.Mailboxes.FirstOrDefault()?.Address?.Trim();
                if (!MailAddress.TryCreate(sender, out var from) || from.Address.Equals(settings.SupportAddress, StringComparison.OrdinalIgnoreCase)) {
                    await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                    lastUid = Math.Max(lastUid, uid.Id);
                    continue;
                }
                var handled = await ProcessMessageAsync(message, from, cancellationToken);
                if (handled) await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                else await inbox.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                if (handled) retryUids.Remove(uid.Id);
                else retryUids.Add(uid.Id);
                lastUid = Math.Max(lastUid, uid.Id);
            }
            catch (DbUpdateException exception) {
                db.ChangeTracker.Clear();
                var sourceId = SourceId(message);
                var alreadyProcessed = await db.Tickets.AnyAsync(ticket => ticket.SourceEmailMessageId == sourceId, cancellationToken) ||
                    await db.Comments.AnyAsync(comment => comment.SourceEmailMessageId == sourceId, cancellationToken);
                if (alreadyProcessed) {
                    await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                    retryUids.Remove(uid.Id);
                }
                else {
                    await inbox.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                    retryUids.Add(uid.Id);
                    var rootCause = exception.GetBaseException();
                    var databaseErrorNumber = rootCause.GetType().GetProperty("Number")?.GetValue(rootCause)?.ToString() ?? "n/a";
                    var affectedEntities = string.Join(",", exception.Entries.Select(entry => entry.Metadata.ClrType.Name).Distinct());
                    logger.LogWarning("Inbound message could not be committed and remains unread ({ErrorType}; database error {DatabaseErrorNumber}; entities {AffectedEntities}).",
                        exception.GetType().Name, databaseErrorNumber, string.IsNullOrEmpty(affectedEntities) ? "unknown" : affectedEntities);
                }
                lastUid = Math.Max(lastUid, uid.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) {
                await inbox.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                retryUids.Add(uid.Id);
                logger.LogWarning("Inbound message was not processed ({ErrorType}); it remains unread for retry.", exception.GetType().Name);
                lastUid = Math.Max(lastUid, uid.Id);
            }
        }
        WriteCursor(new InboundMailboxCursor(inbox.UidValidity, lastUid, retryUids.Order().ToArray()));
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task<bool> ProcessMessageAsync(MimeMessage message, MailAddress sender, CancellationToken cancellationToken)
    {
        var email = sender.Address.Trim().ToLowerInvariant();
        var sourceId = SourceId(message);
        var duplicateReply = await db.Comments.Include(comment => comment.Ticket).ThenInclude(ticket => ticket!.Requester)
            .SingleOrDefaultAsync(comment => comment.SourceEmailMessageId == sourceId, cancellationToken);
        if (duplicateReply is not null) return true;
        var staff = await db.AitsStaff.AsNoTracking().SingleOrDefaultAsync(user => user.Email == email && user.IsActive &&
            db.InternalAccounts.Any(account => account.DeskUserId == user.Id && account.Role == AccountRole.AitsUser && account.IsApproved && account.IsActive), cancellationToken);
        if (staff is not null) return await ProcessStaffReplyAsync(message, staff, sourceId, cancellationToken);
        var domain = email[(email.LastIndexOf('@') + 1)..];
        // Keep companies tracked because a newly created ticket references the
        // selected company navigation. Adding a ticket graph with an untracked
        // company would make EF try to insert that company again.
        var customers = await db.Companies.ToListAsync(cancellationToken);
        var customerUserIds = await db.ClientUsers.AsNoTracking().Where(user => user.Email == email).Select(user => user.ClientId).ToListAsync(cancellationToken);
        var rulesByCustomer = customers.Select(customer => new {
            Customer = customer,
            Rules = customer.EmailDomains.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(rule => rule.TrimStart('@').ToLowerInvariant()).ToList()
        }).ToList();
        // Prefer an exact sender allowlist over a broader domain rule. A shared
        // domain can be accepted by several customers while one exact address
        // still identifies the intended customer unambiguously.
        var exactMatches = rulesByCustomer.Where(entry =>
            (entry.Customer.Email.Contains('@') && string.Equals(entry.Customer.Email, email, StringComparison.OrdinalIgnoreCase)) ||
            entry.Rules.Contains(email)).Select(entry => entry.Customer).ToList();
        var domainMatches = rulesByCustomer.Where(entry => entry.Rules.Contains(domain) ||
            string.Equals(entry.Customer.Email, domain, StringComparison.OrdinalIgnoreCase) ||
            (entry.Rules.Count == 0 && string.Equals(entry.Customer.Email.Split('@').LastOrDefault(), domain, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Customer).ToList();
        var userMatches = customers.Where(customer => customerUserIds.Contains(customer.Id)).ToList();
        var matched = exactMatches.Count > 0
            ? exactMatches
            : userMatches.Count > 0
                ? userMatches
                : domainMatches;
        if (matched.Count != 1) {
            logger.LogWarning("Inbound email from {Sender} matched {CustomerCount} customers; it remains unread and no ticket was created.", email, matched.Count);
            return false;
        }
        var customerRecord = matched[0];
        var requester = await db.ClientUsers.SingleOrDefaultAsync(user => user.Email == email && user.ClientId == customerRecord.Id, cancellationToken);
        if (requester is null) {
            // The global email address may already belong to a customer user at another company.
            // An explicit inbound allowlist match is sufficient to create a ticket for this customer;
            // reuse that contact record rather than requiring a separate login or duplicate email.
            requester = await db.ClientUsers.SingleOrDefaultAsync(user => user.Email == email, cancellationToken);
            if (requester is null && await db.AitsStaff.AnyAsync(user => user.Email == email, cancellationToken)) {
                logger.LogWarning("Inbound sender {Sender} already belongs to a different directory user; no requester could be linked to customer {CustomerId}.", email, customerRecord.Id);
                return false;
            }
            if (requester is null) {
                var displayName = string.IsNullOrWhiteSpace(sender.DisplayName) ? email[..email.LastIndexOf('@')].Replace('.', ' ').Replace('_', ' ') : sender.DisplayName.Trim();
                requester = new ClientDeskUser { Name = displayName.Length > 80 ? displayName[..80] : displayName, Email = email, Department = "Email", ClientId = customerRecord.Id };
                db.ClientUsers.Add(requester);
            }
        }

        var duplicateTicket = await db.Tickets.SingleOrDefaultAsync(ticket => ticket.SourceEmailMessageId == sourceId, cancellationToken);
        if (duplicateTicket is not null) return true;
        var attachments = ReadAttachments(message);
        var ticketNumber = ReadTicketNumber(message.Subject);
        if (ticketNumber.HasValue) {
            var existing = await db.Tickets.Include(ticket => ticket.Client).SingleOrDefaultAsync(ticket => ticket.Id == ticketNumber && ticket.ClientId == customerRecord.Id, cancellationToken);
            if (existing is not null) {
                var replyBody = ReadBody(message);
                if (!string.IsNullOrWhiteSpace(replyBody) || attachments.Count > 0) {
                    var commentBody = string.IsNullOrWhiteSpace(replyBody) ? "Customer sent attachments by email." : replyBody;
                    var comment = new TicketComment { Ticket = existing, Body = Clip(commentBody, 3000), AuthorName = requester.Name, AuthorRole = "Customer user", SourceEmailMessageId = sourceId };
                    db.Comments.Add(comment);
                    foreach (var attachment in attachments) { attachment.Ticket = existing; attachment.Comment = comment; db.TicketAttachments.Add(attachment); }
                    existing.EmailThreadMessageId = message.MessageId is { Length: > 0 } replyMessageId ? replyMessageId[..Math.Min(replyMessageId.Length, 320)] : existing.EmailThreadMessageId;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.SupportLevelStartedAt = DateTime.UtcNow;
                    db.TicketActivities.Add(new TicketActivity { Ticket = existing, ActorName = requester.Name, Message = "Customer replied by email." });
                    await db.SaveChangesAsync(cancellationToken);
                    await NotifyAgentsOfCustomerReplyAsync(existing, message, commentBody, attachments, cancellationToken);
                }
                return true;
            }
        }

        var project = await db.Projects.AsNoTracking().Where(project => project.ClientId == customerRecord.Id).OrderBy(project => project.Id).FirstOrDefaultAsync(cancellationToken);
        if (project is null) {
            logger.LogWarning("Inbound email matched customer {CustomerId}, but the customer has no project; no ticket was created.", customerRecord.Id);
            return false;
        }
        var assigneeId = await db.StaffProjects.AsNoTracking()
            .Where(membership => membership.ProjectId == project.Id && membership.SupportLevel == SupportLevel.L1 && membership.Staff!.IsActive)
            .OrderBy(membership => membership.StaffId)
            .Select(membership => (int?)membership.StaffId)
            .FirstOrDefaultAsync(cancellationToken);
        var title = CleanSubject(message.Subject);
        if (string.IsNullOrWhiteSpace(title)) title = "Email support request";
        var body = ReadBody(message);
        var priority = Regex.IsMatch($"{message.Subject} {body}", @"\burgent\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? TicketPriority.Urgent
            : TicketPriority.Medium;
        var ticket = new Ticket {
            ClientId = customerRecord.Id, Client = customerRecord, ProjectId = project.Id, Requester = requester,
            Title = Clip(title, 160), Description = Clip(string.IsNullOrWhiteSpace(body) ? "Ticket submitted by email." : body, 5000),
            Priority = priority, Status = TicketStatus.Open, SupportLevel = SupportLevel.L1, AssigneeId = assigneeId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, SourceEmailMessageId = sourceId, EmailThreadMessageId = message.MessageId is { Length: > 0 } originalMessageId ? originalMessageId[..Math.Min(originalMessageId.Length, 320)] : null
        };
        foreach (var attachment in attachments) ticket.Attachments.Add(attachment);
        db.Tickets.Add(ticket);
        await ticketCreatedEmails.QueueAsync(ticket);
        await db.SaveChangesAsync(cancellationToken);
        await SendConfirmationAsync(sender, ticket, false, attachments.Count, cancellationToken);
        return true;
    }

    private string CursorPath => Path.Combine(environment.ContentRootPath, "App_Data", "inbound-mail-cursor.json");

    private async Task<bool> ProcessStaffReplyAsync(MimeMessage message, AitsStaffUser staff, string sourceId, CancellationToken cancellationToken)
    {
        var reference = message.Subject + " " + message.InReplyTo + " " + string.Join(" ", message.References);
        var ticketNumber = ReadTicketNumber(reference);
        if (!ticketNumber.HasValue) {
            logger.LogWarning("Staff email reply from {StaffId} has no ticket number/reference; it remains unread.", staff.Id);
            return false;
        }
        var ticket = await db.Tickets.Include(t => t.Requester).Include(t => t.Client).SingleOrDefaultAsync(t => t.Id == ticketNumber.Value, cancellationToken);
        if (ticket is null || !ticket.ProjectId.HasValue || !await db.StaffProjects.AnyAsync(m => m.StaffId == staff.Id && m.ProjectId == ticket.ProjectId && m.SupportLevel == ticket.SupportLevel && m.Project!.ClientId == ticket.ClientId, cancellationToken)) {
            logger.LogWarning("Staff email reply from {StaffId} did not match an assigned project and support level for ticket {TicketId}.", staff.Id, ticketNumber.Value);
            return false;
        }
        var body = ReadBody(message);
        // Inline images in an agent's mail are commonly signature logos. They
        // are not ticket files and should not be forwarded to the customer.
        var attachments = ReadAttachments(message, includeInlineImages: false);
        if (string.IsNullOrWhiteSpace(body) && attachments.Count == 0) return true;
        var commentText = string.IsNullOrWhiteSpace(body) ? "AITS staff sent attachments by email." : Clip(body, 3000);
        var comment = new TicketComment { Ticket = ticket, Body = commentText, AuthorName = staff.Name, AuthorRole = "AITS staff", SourceEmailMessageId = sourceId };
        db.Comments.Add(comment);
        foreach (var attachment in attachments) { attachment.Ticket = ticket; attachment.Comment = comment; db.TicketAttachments.Add(attachment); }
        ticket.UpdatedAt = DateTime.UtcNow;
        db.TicketActivities.Add(new TicketActivity { Ticket = ticket, ActorName = staff.Name, Message = "Agent replied by email." });
        await db.SaveChangesAsync(cancellationToken);
        await SendAgentReplyToCustomerAsync(ticket, commentText, attachments, cancellationToken);
        return true;
    }

    private async Task SendAgentReplyToCustomerAsync(Ticket ticket, string body, IReadOnlyCollection<TicketAttachment> files, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var password = settings.Password ?? credentials.Read();
        if (!MailAddress.TryCreate(ticket.Requester?.Email, out var recipient)) throw new InvalidOperationException("Agent reply could not be sent because the ticket requester email is invalid.");
        if (string.IsNullOrWhiteSpace(password) || !MailAddress.TryCreate(settings.FromAddress, out var from)) throw new InvalidOperationException("Agent reply could not be sent because outgoing mailbox settings are incomplete.");
        var subject = TicketEmailSubject.Reply(ticket);
        using var mail = new System.Net.Mail.MailMessage {
            From = new MailAddress(from.Address, settings.FromName),
            Subject = subject,
            Body = body,
            BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8
        };
        if (MailAddress.TryCreate(settings.SupportAddress, out var supportAddress) && supportAddress is not null) mail.ReplyToList.Add(supportAddress);
        var outgoingMessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        mail.Headers.Add("Message-ID", outgoingMessageId);
        if (!string.IsNullOrWhiteSpace(ticket.EmailThreadMessageId)) {
            mail.Headers.Add("In-Reply-To", ticket.EmailThreadMessageId);
            mail.Headers.Add("References", ticket.EmailThreadMessageId);
        }
        mail.To.Add(recipient);
        foreach (var file in files) {
            var stream = new MemoryStream(file.Data, writable: false);
            mail.Attachments.Add(new Attachment(stream, file.FileName, file.ContentType));
        }
        using var smtp = new SmtpClient(settings.Host, settings.Port) { EnableSsl = true, UseDefaultCredentials = false, Credentials = new NetworkCredential(settings.UserName, password), DeliveryMethod = SmtpDeliveryMethod.Network };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(mail, timeout.Token);
        ticket.EmailThreadMessageId = outgoingMessageId;
        await db.SaveChangesAsync(cancellationToken);
    }

    private InboundMailboxCursor? ReadCursor()
    {
        try { return File.Exists(CursorPath) ? JsonSerializer.Deserialize<InboundMailboxCursor>(File.ReadAllText(CursorPath)) : null; }
        catch (Exception exception) when (exception is IOException or JsonException) {
            logger.LogWarning("Inbound mailbox cursor could not be read ({ErrorType}); initializing from the current mailbox.", exception.GetType().Name);
            return null;
        }
    }

    private void WriteCursor(InboundMailboxCursor cursor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CursorPath)!);
        var temporaryPath = CursorPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(cursor));
        File.Move(temporaryPath, CursorPath, true);
    }

    private sealed record InboundMailboxCursor(uint UidValidity, uint LastUid, uint[]? RetryUids = null);

    private async Task SendConfirmationAsync(MailAddress sender, Ticket ticket, bool reply, int attachmentCount, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var password = settings.Password ?? credentials.Read();
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Ticket acknowledgement could not be sent because outgoing mailbox credentials are not configured.");
        if (!MailAddress.TryCreate(settings.FromAddress, out var from)) throw new InvalidOperationException("Ticket acknowledgement could not be sent because the sender email address is invalid.");
        var subject = reply ? TicketEmailSubject.Reply(ticket) : TicketEmailSubject.Base(ticket);
        var intro = reply
            ? $"Your update was added to ticket #{ticket.Id:D4}: {ticket.Title}."
            : $"We received your support request and created ticket #{ticket.Id:D4}: {ticket.Title}.";
        var body = reply
            ? $"{intro}{AttachmentConfirmation(attachmentCount)}"
            : $"{intro}\r\n\r\nTicket number: #{ticket.Id:D4}\r\nPriority: {ticket.Priority}\r\nSupport team: {ticket.SupportLevel}.{AttachmentConfirmation(attachmentCount)}\r\n\r\nOur team will follow up by email.";
        var supportAddressIsValid = MailAddress.TryCreate(settings.SupportAddress, out var supportAddress);
        var replySubject = TicketEmailSubject.Reply(ticket);
        var replyUrl = supportAddressIsValid
            ? $"mailto:{Uri.EscapeDataString(supportAddress!.Address)}?subject={Uri.EscapeDataString(replySubject)}"
            : null;
        var details = new List<(string Label, string Value)> {
            ("Ticket", $"#{ticket.Id:D4}"), ("Title", ticket.Title),
            ("Priority", ticket.Priority.ToString()), ("Support team", ticket.SupportLevel.ToString()),
            ("Status", ticket.Status.ToString())
        };
        if (attachmentCount > 0) details.Add(("Attachments", $"{attachmentCount} file(s) added to the ticket"));
        var html = TicketEmailHtml.Render(reply ? "Update received" : $"Ticket #{ticket.Id:D4} created", intro, details,
            actionLabel: replyUrl is null ? null : "Reply to this ticket", actionUrl: replyUrl,
            footer: "You can reply to this email to add another update to the same ticket.");
        using var mail = new System.Net.Mail.MailMessage {
            From = new MailAddress(from.Address, settings.FromName),
            Subject = subject,
            Body = body,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        if (supportAddressIsValid)
            mail.ReplyToList.Add(supportAddress);
        var outgoingMessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        mail.Headers.Add("Message-ID", outgoingMessageId);
        if (!string.IsNullOrWhiteSpace(ticket.EmailThreadMessageId)) {
            mail.Headers.Add("In-Reply-To", ticket.EmailThreadMessageId);
            mail.Headers.Add("References", ticket.EmailThreadMessageId);
        }
        // Reply acknowledgements should contain only the confirmation text and any
        // customer files; embedding the brand logo makes it appear as an attachment.
        mail.AlternateViews.Add(TicketEmailHtml.CreateView(html));
        mail.To.Add(sender);
        using var smtp = new SmtpClient(settings.Host, settings.Port) {
            EnableSsl = true, UseDefaultCredentials = false, Credentials = new NetworkCredential(settings.UserName, password), DeliveryMethod = SmtpDeliveryMethod.Network
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(mail, timeout.Token);
        ticket.EmailThreadMessageId = outgoingMessageId;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string AttachmentConfirmation(int attachmentCount) => attachmentCount == 0
        ? ""
        : $"\r\n\r\n{attachmentCount} attachment{(attachmentCount == 1 ? "" : "s")} added to the ticket.";

    private async Task NotifyAgentsOfCustomerReplyAsync(Ticket ticket, MimeMessage customerMessage, string body, IReadOnlyCollection<TicketAttachment> files, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.Enabled || !ticket.ProjectId.HasValue) return;
        var recipients = await db.StaffProjects.AsNoTracking()
            .Where(m => m.ProjectId == ticket.ProjectId && m.SupportLevel == ticket.SupportLevel && m.Project!.ClientId == ticket.ClientId &&
                m.Staff!.IsActive && db.InternalAccounts.Any(a => a.DeskUserId == m.StaffId && a.Role == AccountRole.AitsUser && a.IsApproved && a.IsActive && a.Email == m.Staff.Email))
            .Select(m => new { m.StaffId, Email = m.Staff!.Email }).Distinct().ToListAsync(cancellationToken);
        var settings = options.CurrentValue;
        var password = settings.Password ?? credentials.Read();
        var fromAddress = string.IsNullOrWhiteSpace(settings.FromAddress) ? settings.UserName : settings.FromAddress;
        if (string.IsNullOrWhiteSpace(password) || !MailAddress.TryCreate(fromAddress, out var from)) return;
        foreach (var staffRecipient in recipients) {
            if (!MailAddress.TryCreate(staffRecipient.Email, out var recipient)) continue;
            using var mail = new System.Net.Mail.MailMessage {
                From = new MailAddress(from.Address, settings.FromName),
                Subject = TicketEmailSubject.Reply(ticket),
                Body = body, BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8
            };
            var initialAssignmentId = await db.TicketEmailNotifications.AsNoTracking()
                .Where(n => n.TicketId == ticket.Id && n.StaffId == staffRecipient.StaffId && n.Kind == TicketEmailKind.Created)
                .OrderBy(n => n.Id).Select(n => (int?)n.Id).FirstOrDefaultAsync(cancellationToken);
            if (initialAssignmentId.HasValue && MailAddress.TryCreate(fromAddress, out var senderAddress)) {
                var originalMessageId = StaffNotificationMessageId(ticket.Id, staffRecipient.StaffId, TicketEmailKind.Created, initialAssignmentId.Value, senderAddress.Host);
                mail.Headers.Add("In-Reply-To", originalMessageId);
                mail.Headers.Add("References", originalMessageId);
            } else if (!string.IsNullOrWhiteSpace(customerMessage.MessageId)) {
                // If an older ticket has no queued assignment message, use the
                // customer's reference chain as the best available thread anchor.
                mail.Headers.Add("In-Reply-To", $"<{customerMessage.MessageId}>");
                var references = customerMessage.References.ToList();
                if (!references.Contains(customerMessage.MessageId, StringComparer.OrdinalIgnoreCase)) references.Add(customerMessage.MessageId);
                mail.Headers.Add("References", string.Join(" ", references.Select(reference => reference.StartsWith('<') ? reference : $"<{reference}>")));
            }
            mail.To.Add(recipient);
            foreach (var file in files)
                mail.Attachments.Add(new Attachment(new MemoryStream(file.Data, writable: false), file.FileName, file.ContentType));
            try {
                using var smtp = new SmtpClient(settings.Host, settings.Port) { EnableSsl = true, UseDefaultCredentials = false, Credentials = new NetworkCredential(settings.UserName, password), DeliveryMethod = SmtpDeliveryMethod.Network };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await smtp.SendMailAsync(mail, timeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
                logger.LogWarning("Customer reply notification for ticket {TicketId} could not be sent ({ErrorType}).", ticket.Id, exception.GetType().Name);
            }
        }
    }

    private static string StaffNotificationMessageId(int ticketId, int staffId, TicketEmailKind kind, int notificationId, string domain) =>
        $"<aits-ticket-{ticketId}-staff-{staffId}-kind-{(int)kind}-notification-{notificationId}@{domain}>";

    private List<TicketAttachment> ReadAttachments(MimeMessage message, bool includeInlineImages = true)
    {
        var result = new List<TicketAttachment>();
        var totalBytes = 0;
        var inlineContentIds = ReadBodyContentIds(message);
        // Gmail and other clients may send pasted images as inline MIME resources
        // rather than as entries in MimeMessage.Attachments. Include those resources
        // so they are stored with the ticket and can be forwarded to the assigned agent.
        var entities = message.Attachments.ToList();
        foreach (var part in message.BodyParts.OfType<MimePart>()) {
            var isTextBody = part.ContentType.IsMimeType("text", "plain") || part.ContentType.IsMimeType("text", "html");
            var hasInlineResource = !isTextBody && (!string.IsNullOrWhiteSpace(part.ContentId) ||
                string.Equals(part.ContentDisposition?.Disposition, "inline", StringComparison.OrdinalIgnoreCase));
            if ((part.IsAttachment || !string.IsNullOrWhiteSpace(part.FileName) || hasInlineResource) && !entities.Contains(part))
                entities.Add(part);
        }
        foreach (var entity in entities) {
            if (result.Count >= MaxEmailAttachmentCount) {
                logger.LogWarning("Inbound email has more than {MaxCount} attachments; extra files were skipped.", MaxEmailAttachmentCount);
                break;
            }
            try {
                using var buffer = new LimitedAttachmentStream(MaxEmailAttachmentBytes);
                string fileName;
                string contentType;
                switch (entity) {
                    case MimePart part:
                        if (part.Content is null) continue;
                        var isInlineImage = part.ContentType.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                            (string.Equals(part.ContentDisposition?.Disposition, "inline", StringComparison.OrdinalIgnoreCase) ||
                             (!string.IsNullOrWhiteSpace(part.ContentId) && !string.Equals(part.ContentDisposition?.Disposition, "attachment", StringComparison.OrdinalIgnoreCase)));
                        if (isInlineImage && !includeInlineImages) continue;
                        if (isInlineImage && !string.IsNullOrWhiteSpace(part.ContentId) && !inlineContentIds.Contains(NormalizeContentId(part.ContentId)))
                            continue;
                        part.Content.DecodeTo(buffer);
                        fileName = isInlineImage
                            ? $"inline-{ContentIdKey(part.ContentId ?? Guid.NewGuid().ToString("N"))}-{SafeFileName(part.FileName ?? $"screenshot.{part.ContentType.MediaSubtype}")}" 
                            : part.FileName ?? "attachment";
                        contentType = part.ContentType.MimeType;
                        break;
                    case MessagePart attachedMessage:
                        if (attachedMessage.Message is null) continue;
                        attachedMessage.Message.WriteTo(buffer);
                        fileName = attachedMessage.ContentDisposition?.FileName ?? "attached-message.eml";
                        contentType = "message/rfc822";
                        break;
                    default:
                        logger.LogWarning("Inbound attachment type {AttachmentType} is not supported and was skipped.", entity.GetType().Name);
                        continue;
                }
                if (buffer.Length == 0) continue;
                if (totalBytes + buffer.Length > MaxEmailAttachmentTotalBytes) {
                    logger.LogWarning("Inbound email attachment budget of {MaxMegabytes} MB was exceeded; the remaining files were skipped.", MaxEmailAttachmentTotalBytes / 1024 / 1024);
                    break;
                }
                totalBytes += (int)buffer.Length;
                result.Add(new TicketAttachment {
                    FileName = SafeFileName(fileName),
                    ContentType = contentType.Length <= 160 ? contentType : "application/octet-stream",
                    Data = buffer.ToArray(),
                    UploadedAt = DateTime.UtcNow
                });
            }
            catch (AttachmentTooLargeException) {
                logger.LogWarning("Inbound attachment exceeded the {MaxMegabytes} MB per-file limit and was skipped.", MaxEmailAttachmentBytes / 1024 / 1024);
            }
        }
        return result;
    }

    private static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "attachment").Replace('\\', '/'));
        name = string.Concat(name.Where(character => !char.IsControl(character)));
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        return name.Length > 255 ? name[..255] : name;
    }

    private sealed class LimitedAttachmentStream(long maxBytes) : MemoryStream
    {
        private void EnsureFits(long incomingBytes)
        {
            if (Length + incomingBytes > maxBytes) throw new AttachmentTooLargeException();
        }

        public override void Write(byte[] buffer, int offset, int count) { EnsureFits(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { EnsureFits(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { EnsureFits(1); base.WriteByte(value); }
    }

    private sealed class AttachmentTooLargeException : Exception;

    private static string SourceId(MimeMessage message)
    {
        var stable = string.IsNullOrWhiteSpace(message.MessageId) ? message.Date.ToUniversalTime().ToString("O") + message.Subject + message.From : message.MessageId;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable)));
    }
    private static int? ReadTicketNumber(string? subject)
    {
        var match = Regex.Match(subject ?? "", @"(?:ticket\s*#?\s*|#)(\d{1,10})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }
    private static string CleanSubject(string? subject) => Regex.Replace(subject ?? "", @"^(?:(?:re|fw|fwd)\s*:\s*)+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    private static string ReadBody(MimeMessage message)
    {
        var body = !string.IsNullOrWhiteSpace(message.HtmlBody)
            ? Regex.Replace(RemoveHtmlSignature(message.HtmlBody), "<(br|/p|/div|/li|/tr|/h[1-6])\\b[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : message.TextBody ?? "";
        body = Regex.Replace(body, "<script\\b[^>]*>[\\s\\S]*?</script>|<style\\b[^>]*>[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        body = Regex.Replace(body, "<(br)\\b[^>]*>|</(?:p|div|li|tr|h[1-6])\\s*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        body = WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", ""));
        var signature = Regex.Match(body, @"(?im)^\s*(?:--\s*$|regards[,]?\s*$|best regards[,]?\s*$|kind regards[,]?\s*$|thanks[,]?\s*$|sent from my .*$)");
        if (!signature.Success)
            signature = Regex.Match(body, @"(?im)(?:^|\n)\s*(?:Regards|Best regards|Kind regards|Thanks|Thank you)[,!]?(?:\s|$)");
        if (signature.Success) body = body[..signature.Index];
        // Keep the newest reply and discard quoted history.
        var cut = new[] { "\r\nFrom:", "\nFrom:", "\r\nOn ", "\nOn " }
            .Select(marker => body.IndexOf(marker, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(-1).Min();
        var text = cut >= 0 ? body[..cut] : body;
        text = Regex.Replace(text, @"[ \t]*\r?\n[ \t]*", "\n");
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    private static string RemoveHtmlSignature(string html)
    {
        var signature = Regex.Match(html, "<(?:div|table|section)\\b[^>]*(?:class|id)=[\\\"'][^\\\"']*(?:gmail_signature|moz-signature|signature)[^\\\"']*[\\\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!signature.Success)
            signature = Regex.Match(html, @"(?is)(?:</?(?:div|p|span|td|br)\b[^>]*>\s*){0,5}(?:Regards|Best regards|Kind regards|Thanks|Thank you)\b");
        return signature.Success ? html[..signature.Index] : html;
    }

    private static HashSet<string> ReadBodyContentIds(MimeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.HtmlBody)) return [];
        var body = RemoveHtmlSignature(message.HtmlBody);
        return Regex.Matches(body, "<img\\b[^>]*\\bsrc=[\\\"']cid:([^\\\"']+)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => NormalizeContentId(match.Groups[1].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeContentId(string contentId) => contentId.Trim().Trim('<', '>');
    private static string ContentIdKey(string contentId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeContentId(contentId))))[..12].ToLowerInvariant();
    private static string Clip(string? value, int max) => (value ?? "").Trim() is var text && text.Length > max ? text[..max] : text;
}
