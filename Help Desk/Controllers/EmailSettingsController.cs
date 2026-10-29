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
            UserName=settings.UserName, FromAddress=settings.FromAddress, FromName=settings.FromName,
            PublicBaseUrl=settings.PublicBaseUrl, HasPassword=HasCredential() });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Index([Bind("Enabled,Host,Port,UserName,FromAddress,FromName,Password,PublicBaseUrl")] EmailSettingsViewModel model)
    {
        model.Host = model.Host?.Trim() ?? ""; model.UserName = model.UserName?.Trim() ?? "";
        model.FromAddress = model.FromAddress?.Trim() ?? ""; model.FromName = model.FromName?.Trim() ?? "";
        model.PublicBaseUrl = model.PublicBaseUrl?.Trim(); model.HasPassword = HasCredential();
        if (string.IsNullOrWhiteSpace(model.Password)) model.Password = null;
        if (Uri.CheckHostName(model.Host) == UriHostNameType.Unknown) ModelState.AddModelError("Host", "Enter an SMTP hostname, such as smtp.gmail.com.");
        if (!string.IsNullOrWhiteSpace(model.PublicBaseUrl) && (!Uri.TryCreate(model.PublicBaseUrl, UriKind.Absolute, out var url)
            || (url.Scheme != "https" && url.Scheme != "http") || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment)))
            ModelState.AddModelError("PublicBaseUrl", "Enter a full http or https helpdesk URL without credentials, query parameters or fragments.");
        if (model.Enabled && !model.HasPassword && string.IsNullOrWhiteSpace(model.Password)) ModelState.AddModelError("Password", "Enter an app password before enabling email notifications.");
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
        if (model.Enabled && !model.HasPassword) ModelState.AddModelError("", "A valid app password is required to enable sending.");
        return View(model);
    }
}
