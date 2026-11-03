using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        var lastUid = initializeCursor ? (allUids.Count == 0 ? 0u : allUids.Max(uid => uid.Id)) : cursor!.LastUid;
        IEnumerable<UniqueId> candidates = initializeCursor
            ? unreadUids
            : allUids.Where(uid => uid.Id > lastUid).Concat(unreadUids).Distinct().OrderBy(uid => uid.Id);
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
                lastUid = Math.Max(lastUid, uid.Id);
            }
            catch (DbUpdateException exception) {
                db.ChangeTracker.Clear();
                var sourceId = SourceId(message);
                var alreadyProcessed = await db.Tickets.AnyAsync(ticket => ticket.SourceEmailMessageId == sourceId, cancellationToken) ||
                    await db.Comments.AnyAsync(comment => comment.SourceEmailMessageId == sourceId, cancellationToken);
                if (alreadyProcessed) await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                else {
                    await inbox.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                    logger.LogWarning("Inbound message could not be committed and remains unread ({ErrorType}).", exception.GetType().Name);
                }
                lastUid = Math.Max(lastUid, uid.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) {
                await inbox.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
                logger.LogWarning("Inbound message was not processed ({ErrorType}); it remains unread for retry.", exception.GetType().Name);
                lastUid = Math.Max(lastUid, uid.Id);
            }
        }
        WriteCursor(new InboundMailboxCursor(inbox.UidValidity, lastUid));
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task<bool> ProcessMessageAsync(MimeMessage message, MailAddress sender, CancellationToken cancellationToken)
    {
        var email = sender.Address.Trim().ToLowerInvariant();
        var sourceId = SourceId(message);
        var duplicateReply = await db.Comments.Include(comment => comment.Ticket).ThenInclude(ticket => ticket!.Requester)
            .SingleOrDefaultAsync(comment => comment.SourceEmailMessageId == sourceId, cancellationToken);
        if (duplicateReply is not null) {
            if (duplicateReply.AuthorRole == "AITS staff") await SendAgentReplyToCustomerAsync(duplicateReply.Ticket!, duplicateReply.Body, cancellationToken);
            else await SendConfirmationAsync(sender, duplicateReply.Ticket!, true, 0, cancellationToken);
            return true;
        }
        var staff = await db.AitsStaff.AsNoTracking().SingleOrDefaultAsync(user => user.Email == email && user.IsActive &&
            db.InternalAccounts.Any(account => account.DeskUserId == user.Id && account.Role == AccountRole.AitsUser && account.IsApproved && account.IsActive), cancellationToken);
        if (staff is not null) return await ProcessStaffReplyAsync(message, staff, sourceId, cancellationToken);
        var domain = email[(email.LastIndexOf('@') + 1)..];
        var customers = await db.Companies.AsNoTracking().ToListAsync(cancellationToken);
        var customerUserIds = await db.ClientUsers.AsNoTracking().Where(user => user.Email == email).Select(user => user.ClientId).ToListAsync(cancellationToken);
        var rulesByCustomer = customers.Select(customer => new {
            Customer = customer,
            Rules = customer.EmailDomains.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(rule => rule.TrimStart('@').ToLowerInvariant()).ToList()
        }).ToList();
        var explicitMatches = rulesByCustomer.Where(entry =>
            string.Equals(entry.Customer.Email, email, StringComparison.OrdinalIgnoreCase) ||
            entry.Rules.Contains(email)).Select(entry => entry.Customer).ToList();
        var userMatches = customers.Where(customer => customerUserIds.Contains(customer.Id)).ToList();
        var matched = explicitMatches.Count > 0
            ? explicitMatches
            : userMatches.Count > 0
                ? userMatches
                : rulesByCustomer.Where(entry => entry.Rules.Contains(domain) ||
                (entry.Rules.Count == 0 && string.Equals(entry.Customer.Email.Split('@').LastOrDefault(), domain, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry.Customer).ToList();
        if (matched.Count != 1) {
            logger.LogWarning("Inbound email from {Sender} matched {CustomerCount} customers; it remains unread and no ticket was created.", email, matched.Count);
            return false;
        }
        var customerRecord = matched[0];
        var requester = await db.ClientUsers.SingleOrDefaultAsync(user => user.Email == email && user.ClientId == customerRecord.Id, cancellationToken);
        if (requester is null) {
            if (await db.Users.AnyAsync(user => user.Email == email, cancellationToken)) {
                logger.LogWarning("Inbound sender {Sender} already belongs to a different directory user; no requester could be linked to customer {CustomerId}.", email, customerRecord.Id);
                return false;
            }
            var displayName = string.IsNullOrWhiteSpace(sender.DisplayName) ? email[..email.LastIndexOf('@')].Replace('.', ' ').Replace('_', ' ') : sender.DisplayName.Trim();
            requester = new ClientDeskUser { Name = displayName.Length > 80 ? displayName[..80] : displayName, Email = email, Department = "Email", ClientId = customerRecord.Id };
            db.ClientUsers.Add(requester);
        }

        var duplicateTicket = await db.Tickets.SingleOrDefaultAsync(ticket => ticket.SourceEmailMessageId == sourceId, cancellationToken);
        if (duplicateTicket is not null) {
            await SendConfirmationAsync(sender, duplicateTicket, false, 0, cancellationToken);
            return true;
        }
        var attachments = ReadAttachments(message);
        var ticketNumber = ReadTicketNumber(message.Subject);
        if (ticketNumber.HasValue) {
            var existing = await db.Tickets.SingleOrDefaultAsync(ticket => ticket.Id == ticketNumber && ticket.ClientId == customerRecord.Id, cancellationToken);
            if (existing is not null) {
                var replyBody = ReadBody(message);
                if (!string.IsNullOrWhiteSpace(replyBody) || attachments.Count > 0) {
                    var commentBody = string.IsNullOrWhiteSpace(replyBody) ? "Customer sent attachments by email." : replyBody;
                    db.Comments.Add(new TicketComment { Ticket = existing, Body = Clip(commentBody, 3000), AuthorName = requester.Name, AuthorRole = "Customer user", SourceEmailMessageId = sourceId });
                    foreach (var attachment in attachments) { attachment.Ticket = existing; db.TicketAttachments.Add(attachment); }
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.SupportLevelStartedAt = DateTime.UtcNow;
                    db.TicketActivities.Add(new TicketActivity { Ticket = existing, ActorName = requester.Name, Message = "Customer replied by email." });
                    await db.SaveChangesAsync(cancellationToken);
                    await SendConfirmationAsync(sender, existing, true, attachments.Count, cancellationToken);
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
            ClientId = customerRecord.Id, ProjectId = project.Id, Requester = requester,
            Title = Clip(title, 160), Description = Clip(string.IsNullOrWhiteSpace(body) ? "Ticket submitted by email." : body, 5000),
            Priority = priority, Status = TicketStatus.Open, SupportLevel = SupportLevel.L1, AssigneeId = assigneeId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, SourceEmailMessageId = sourceId
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
        var ticket = await db.Tickets.Include(t => t.Requester).SingleOrDefaultAsync(t => t.Id == ticketNumber.Value, cancellationToken);
        if (ticket is null || !ticket.ProjectId.HasValue || !await db.StaffProjects.AnyAsync(m => m.StaffId == staff.Id && m.ProjectId == ticket.ProjectId && m.SupportLevel == ticket.SupportLevel && m.Project!.ClientId == ticket.ClientId, cancellationToken)) {
            logger.LogWarning("Staff email reply from {StaffId} did not match an assigned project and support level for ticket {TicketId}.", staff.Id, ticketNumber.Value);
            return false;
        }
        var body = ReadBody(message);
        var attachments = ReadAttachments(message);
        if (string.IsNullOrWhiteSpace(body) && attachments.Count == 0) return true;
        var commentText = string.IsNullOrWhiteSpace(body) ? "AITS staff sent attachments by email." : Clip(body, 3000);
        db.Comments.Add(new TicketComment { Ticket = ticket, Body = commentText, AuthorName = staff.Name, AuthorRole = "AITS staff", SourceEmailMessageId = sourceId });
        foreach (var attachment in attachments) { attachment.Ticket = ticket; db.TicketAttachments.Add(attachment); }
        ticket.UpdatedAt = DateTime.UtcNow;
        db.TicketActivities.Add(new TicketActivity { Ticket = ticket, ActorName = staff.Name, Message = "Agent replied by email." });
        await db.SaveChangesAsync(cancellationToken);
        await SendAgentReplyToCustomerAsync(ticket, commentText, cancellationToken);
        return true;
    }

    private async Task SendAgentReplyToCustomerAsync(Ticket ticket, string body, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var password = settings.Password ?? credentials.Read();
        if (!MailAddress.TryCreate(ticket.Requester?.Email, out var recipient)) throw new InvalidOperationException("Agent reply could not be sent because the ticket requester email is invalid.");
        if (string.IsNullOrWhiteSpace(password) || !MailAddress.TryCreate(settings.FromAddress, out var from)) throw new InvalidOperationException("Agent reply could not be sent because outgoing mailbox settings are incomplete.");
        using var mail = new System.Net.Mail.MailMessage {
            From = new MailAddress(from.Address, settings.FromName),
            Subject = $"Re: [Ticket #{ticket.Id:D4}] {ticket.Title}",
            Body = $"AITS support replied to ticket #{ticket.Id:D4}: {ticket.Title}.\r\n\r\n{body}\r\n\r\nReply to this email and keep the ticket number in the subject to add another update.",
            BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8
        };
        mail.To.Add(recipient);
        using var smtp = new SmtpClient(settings.Host, settings.Port) { EnableSsl = true, UseDefaultCredentials = false, Credentials = new NetworkCredential(settings.UserName, password), DeliveryMethod = SmtpDeliveryMethod.Network };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(mail, timeout.Token);
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

    private sealed record InboundMailboxCursor(uint UidValidity, uint LastUid);

    private async Task SendConfirmationAsync(MailAddress sender, Ticket ticket, bool reply, int attachmentCount, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var password = settings.Password ?? credentials.Read();
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Ticket acknowledgement could not be sent because outgoing mailbox credentials are not configured.");
        if (!MailAddress.TryCreate(settings.FromAddress, out var from)) throw new InvalidOperationException("Ticket acknowledgement could not be sent because the sender email address is invalid.");
        using var mail = new System.Net.Mail.MailMessage {
            From = new MailAddress(from.Address, settings.FromName),
            Subject = reply ? $"Re: Ticket #{ticket.Id:D4} update received" : $"Ticket #{ticket.Id:D4} created: {ticket.Title}",
            Body = reply
                ? $"Your reply was added to ticket #{ticket.Id:D4}: {ticket.Title}.{AttachmentConfirmation(attachmentCount)}"
                : $"We received your support request and created ticket #{ticket.Id:D4}: {ticket.Title}.\r\n\r\nTicket number: #{ticket.Id:D4}\r\nPriority: {ticket.Priority}\r\nSupport team: {ticket.SupportLevel}.{AttachmentConfirmation(attachmentCount)}\r\n\r\nOur team will follow up by email. Reply to this message and keep the ticket number in the subject to add an update to this ticket.",
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        mail.To.Add(sender);
        using var smtp = new SmtpClient(settings.Host, settings.Port) {
            EnableSsl = true, UseDefaultCredentials = false, Credentials = new NetworkCredential(settings.UserName, password), DeliveryMethod = SmtpDeliveryMethod.Network
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(mail, timeout.Token);
    }

    private static string AttachmentConfirmation(int attachmentCount) => attachmentCount == 0
        ? ""
        : $"\r\n\r\n{attachmentCount} attachment{(attachmentCount == 1 ? "" : "s")} added to the ticket.";

    private List<TicketAttachment> ReadAttachments(MimeMessage message)
    {
        var result = new List<TicketAttachment>();
        var totalBytes = 0;
        foreach (var entity in message.Attachments) {
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
                        part.Content.DecodeTo(buffer);
                        fileName = part.FileName ?? "attachment";
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
        var body = message.TextBody;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.HtmlBody))
            body = WebUtility.HtmlDecode(Regex.Replace(message.HtmlBody, "<[^>]+>", " "));
        // Keep the newest reply and discard quoted history/signatures where common markers exist.
        var cut = new[] { "\r\nFrom:", "\nFrom:", "\r\nOn ", "\nOn ", "\r\n-- ", "\n-- " }
            .Select(marker => body?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1).Where(index => index >= 0).DefaultIfEmpty(-1).Min();
        return (cut >= 0 ? body![..cut] : body ?? "").Trim();
    }
    private static string Clip(string? value, int max) => (value ?? "").Trim() is var text && text.Length > max ? text[..max] : text;
}
