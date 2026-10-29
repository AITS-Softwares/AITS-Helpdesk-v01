using System.Text.Json.Nodes;
namespace Help_Desk.Services;
public class EmailSettingsStore(IWebHostEnvironment environment, IConfiguration configuration, EmailCredentialStore credentials)
{
    private readonly object gate = new();
    public void Save(Help_Desk.Models.EmailSettingsViewModel model)
    {
        lock (gate) {
            var path = Path.Combine(environment.ContentRootPath, "App_Data", "email.local.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            var email = root["Email"] as JsonObject ?? new JsonObject();
            root["Email"] = email;
            email["Enabled"] = model.Enabled; email["Host"] = model.Host; email["Port"] = model.Port;
            email["UserName"] = model.UserName; email["FromAddress"] = model.FromAddress;
            email["FromName"] = model.FromName; email["PublicBaseUrl"] = model.PublicBaseUrl ?? "";
            // Passwords are protected separately; never serialize a submitted password into JSON.
            if (!string.IsNullOrEmpty(model.Password)) credentials.Save(model.Password);
            email.Remove("Password");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary, root.ToJsonString(new() { WriteIndented = true }));
                File.Move(temporary, path, true);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            if (configuration is IConfigurationRoot configurable) configurable.Reload();
        }
    }
}
