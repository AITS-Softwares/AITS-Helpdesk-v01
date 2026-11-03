using Help_Desk.Models;
using Help_Desk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
namespace Help_Desk.Controllers;
[Authorize(Roles = "Admin")]
public class EmailSettingsController(IOptionsMonitor<TicketEmailOptions> options, EmailSettingsStore store, EmailCredentialStore credentials) : Controller
{
    private bool HasCredential() { try { return !string.IsNullOrWhiteSpace(options.CurrentValue.Password ?? credentials.Read()); } catch (System.Security.Cryptography.CryptographicException) { return false; } }
    [HttpGet]
    public IActionResult Index()
    {
        var settings = options.CurrentValue;
        return View(new EmailSettingsViewModel { Enabled=settings.Enabled, Host=settings.Host, Port=settings.Port,
            IncomingEnabled=settings.IncomingEnabled, IncomingHost=settings.IncomingHost, IncomingPort=settings.IncomingPort,
            SupportAddress=string.IsNullOrWhiteSpace(settings.SupportAddress) ? settings.FromAddress : settings.SupportAddress,
            UserName=settings.UserName, FromAddress=settings.FromAddress, FromName=settings.FromName,
            PublicBaseUrl=settings.PublicBaseUrl, HasPassword=HasCredential() });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Index([Bind("Enabled,Host,Port,IncomingEnabled,IncomingHost,IncomingPort,SupportAddress,UserName,FromAddress,FromName,Password,PublicBaseUrl")] EmailSettingsViewModel model)
    {
        model.Host = model.Host?.Trim() ?? ""; model.UserName = model.UserName?.Trim() ?? "";
        model.FromAddress = model.FromAddress?.Trim() ?? ""; model.FromName = model.FromName?.Trim() ?? "";
        model.IncomingHost = model.IncomingHost?.Trim() ?? "";
        model.SupportAddress = string.IsNullOrWhiteSpace(model.SupportAddress) ? model.FromAddress : model.SupportAddress.Trim();
        model.PublicBaseUrl = model.PublicBaseUrl?.Trim(); model.HasPassword = HasCredential();
        if (string.IsNullOrWhiteSpace(model.Password)) model.Password = null;
        if (Uri.CheckHostName(model.Host) == UriHostNameType.Unknown) ModelState.AddModelError("Host", "Enter an SMTP hostname, such as smtp.gmail.com.");
        if (model.IncomingEnabled && Uri.CheckHostName(model.IncomingHost) == UriHostNameType.Unknown) ModelState.AddModelError("IncomingHost", "Enter an IMAP hostname, such as imap.gmail.com.");
        if (model.IncomingEnabled && string.IsNullOrWhiteSpace(model.SupportAddress)) ModelState.AddModelError("SupportAddress", "Enter the address customers email to create tickets.");
        if (!string.IsNullOrWhiteSpace(model.PublicBaseUrl) && (!Uri.TryCreate(model.PublicBaseUrl, UriKind.Absolute, out var url)
            || (url.Scheme != "https" && url.Scheme != "http") || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment)))
            ModelState.AddModelError("PublicBaseUrl", "Enter a full http or https helpdesk URL without credentials, query parameters or fragments.");
        if ((model.Enabled || model.IncomingEnabled) && !model.HasPassword && string.IsNullOrWhiteSpace(model.Password)) ModelState.AddModelError("Password", "Enter a mailbox app password before enabling email.");
        if (!string.IsNullOrEmpty(model.Password) && !string.IsNullOrEmpty(options.CurrentValue.Password))
            ModelState.AddModelError("Password", "The SMTP password is managed by environment configuration. Update it there before using this password field.");
        if (ModelState.IsValid) {
            if (model.Host.Equals("smtp.gmail.com", StringComparison.OrdinalIgnoreCase) && model.Password is not null)
                model.Password = string.Concat(model.Password.Where(c => !char.IsWhiteSpace(c)));
            try { store.Save(model); TempData["Message"] = "Email settings saved. New ticket notifications use these settings. No test email was sent."; return RedirectToAction(nameof(Index)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException) {
                ModelState.AddModelError("", "Settings could not be saved. Check application storage access and try again.");
            }
        }
        var passwordErrors = ModelState["Password"]?.Errors.Select(e => e.ErrorMessage).ToList() ?? [];
        model.Password = null; ModelState.Remove("Password");
        foreach (var error in passwordErrors) ModelState.AddModelError("", error);
        // Keep password errors without returning any submitted secret in the rendered page.
        if ((model.Enabled || model.IncomingEnabled) && !model.HasPassword) ModelState.AddModelError("", "A valid app password is required to enable email.");
        return View(model);
    }
}
