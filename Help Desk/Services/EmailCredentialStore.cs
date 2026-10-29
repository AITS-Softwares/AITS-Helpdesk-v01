using Microsoft.AspNetCore.DataProtection;

namespace Help_Desk.Services;

public class EmailCredentialStore(IDataProtectionProvider provider, IWebHostEnvironment environment)
{
    private string CredentialPath => Path.Combine(environment.ContentRootPath, "App_Data", "smtp-password.protected");
    private IDataProtector Protector => provider.CreateProtector("AitsHelpDesk.SmtpPassword.v1");

    public void Save(string password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("An SMTP password is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialPath)!);
        var temporary = CredentialPath + ".tmp";
        File.WriteAllText(temporary, Protector.Protect(password));
        File.Move(temporary, CredentialPath, true);
    }

    public string? Read() => File.Exists(CredentialPath) ? Protector.Unprotect(File.ReadAllText(CredentialPath)) : null;
}
