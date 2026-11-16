using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Help_Desk.Models;

namespace Help_Desk.Services;

public static class TicketEmailSubject
{
    public static string Base(Ticket ticket) => $"[Ticket #{ticket.Id:D4}] {ticket.Client?.Name ?? "Customer"}: {ticket.Title}".Replace('\r', ' ').Replace('\n', ' ');
    public static string Reply(Ticket ticket) => $"Re: {Base(ticket)}";
}

public static class TicketEmailHtml
{
    public static string Render(string heading, string? intro, IEnumerable<(string Label, string Value)> details,
        string? messageTitle = null, string? message = null, string? actionLabel = null, string? actionUrl = null, string? footer = null)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        var html = new StringBuilder("<!doctype html><html><body style='margin:0;padding:24px 12px;background:#f3f5fb;font-family:Arial,Helvetica,sans-serif;color:#202b45'>");
        html.Append("<table role='presentation' align='center' width='640' cellpadding='0' cellspacing='0' border='0' style='width:100%;max-width:640px;border:1px solid #e3e7f0;border-radius:14px;overflow:hidden;background:#fff'><tr><td bgcolor='#5d4cdf' style='padding:18px 24px;background:#5d4cdf;color:#fff'><div style='font-family:Arial,Helvetica,sans-serif;color:#fff'><div style='font-size:12px;font-weight:bold;letter-spacing:1px;text-transform:uppercase;line-height:18px'>AITS Helpdesk</div><div style='margin-top:4px;font-size:22px;font-weight:bold;line-height:28px'>").Append(E(heading)).Append("</div></div></td></tr><tr><td style='padding:24px 26px;font-family:Arial,Helvetica,sans-serif;color:#202b45'>");
        if (!string.IsNullOrWhiteSpace(intro)) html.Append("<p style='margin:0 0 18px;color:#4e5a73;font-size:15px;line-height:1.6;white-space:pre-wrap'>").Append(E(intro)).Append("</p>");
        html.Append("<table role='presentation' style='width:100%;border-collapse:collapse;border:1px solid #e6e9f1;border-radius:10px;background:#f8f9fc'>");
        foreach (var (label, value) in details) {
            html.Append("<tr><td style='width:34%;padding:10px 12px;border-bottom:1px solid #e6e9f1;color:#68758d;font-size:13px;vertical-align:top'>")
                .Append(E(label)).Append("</td><td style='padding:10px 12px;border-bottom:1px solid #e6e9f1;color:#202b45;font-size:13px;font-weight:600;overflow-wrap:anywhere'>")
                .Append(E(value)).Append("</td></tr>");
        }
        html.Append("</table>");
        if (!string.IsNullOrWhiteSpace(message)) {
            html.Append("<h2 style='margin:22px 0 7px;font-size:15px;color:#202b45'>").Append(E(messageTitle ?? "Message")).Append("</h2>")
                .Append("<div style='padding:14px 16px;border:1px solid #e6e9f1;border-radius:10px;background:#fff;color:#34415b;font-size:14px;line-height:1.65;white-space:pre-wrap;overflow-wrap:anywhere'>")
                .Append(E(message)).Append("</div>");
        }
        if (!string.IsNullOrWhiteSpace(actionLabel) && Uri.TryCreate(actionUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto)) {
            html.Append("<p style='margin:22px 0 4px'><a href='").Append(E(uri.AbsoluteUri)).Append("' style='display:inline-block;padding:11px 17px;border-radius:9px;background:#5a4be0;color:#fff;font-size:14px;font-weight:bold;text-decoration:none'>")
                .Append(E(actionLabel)).Append("</a></p>");
        }
        if (!string.IsNullOrWhiteSpace(footer)) html.Append("<p style='margin:20px 0 0;color:#748097;font-size:12px;line-height:1.55'>").Append(E(footer)).Append("</p>");
        html.Append("</td></tr><tr><td style='padding:13px 26px;border-top:1px solid #edf0f5;background:#fafbfe;font-family:Arial,Helvetica,sans-serif;color:#8791a4;font-size:11px'>AITS Support Workspace</td></tr></table></body></html>");
        return html.ToString();
    }

    public static AlternateView CreateView(string html)
    {
        return AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html);
    }
}
