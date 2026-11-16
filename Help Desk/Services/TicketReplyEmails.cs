using System.Net;
using System.Net.Mail;
using System.Text;
using Help_Desk.Models;
using Microsoft.Extensions.Options;

namespace Help_Desk.Services;

public sealed class TicketReplyEmails(IOptionsMonitor<TicketEmailOptions> options, EmailCredentialStore credentials)
{
    public async Task<string?> SendToRequesterAsync(Ticket ticket, string body, IReadOnlyCollection<TicketAttachment> files, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled) return null;

        if (!MailAddress.TryCreate(ticket.Requester?.Email, out var recipient))
            throw new InvalidOperationException("The ticket requester email address is invalid.");
        var fromAddress = string.IsNullOrWhiteSpace(settings.FromAddress) ? settings.UserName : settings.FromAddress;
        if (!MailAddress.TryCreate(fromAddress, out var from))
            throw new InvalidOperationException("Outgoing email settings are incomplete.");
        var password = settings.Password ?? credentials.Read();
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.UserName) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("SMTP credentials are not configured.");

        var subject = TicketEmailSubject.Reply(ticket);
        using var mail = new MailMessage {
            From = new MailAddress(from.Address, settings.FromName),
            Subject = subject,
            Body = body,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        if (MailAddress.TryCreate(settings.SupportAddress, out var replyAddress) && replyAddress is not null) mail.ReplyToList.Add(replyAddress);
        var messageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        mail.Headers.Add("Message-ID", messageId);
        if (!string.IsNullOrWhiteSpace(ticket.EmailThreadMessageId)) {
            mail.Headers.Add("In-Reply-To", ticket.EmailThreadMessageId);
            mail.Headers.Add("References", ticket.EmailThreadMessageId);
        }
        mail.To.Add(recipient);
        foreach (var file in files) {
            var stream = new MemoryStream(file.Data, writable: false);
            mail.Attachments.Add(new Attachment(stream, file.FileName, file.ContentType));
        }

        using var smtp = new SmtpClient(settings.Host, settings.Port) {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(settings.UserName, password),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(mail, timeout.Token);
        return messageId;
    }
}
