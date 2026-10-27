using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Help_Desk.Models;
using Microsoft.IdentityModel.Tokens;

namespace Help_Desk.Services;

public class JwtService
{
    public const string CookieName = "Aits.AccessToken";
    private readonly bool requireSecure;
    public SymmetricSecurityKey Key { get; }
    public string Issuer { get; }
    public string Audience { get; }
    public TimeSpan Lifetime { get; } = TimeSpan.FromHours(1);
    public JwtService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        requireSecure = !environment.IsDevelopment();
        Issuer = configuration["Jwt:Issuer"] ?? "AitsHelpDesk";
        Audience = configuration["Jwt:Audience"] ?? "AitsHelpDesk.Web";
        var configured = configuration["Jwt:SigningKey"];
        byte[] key;
        if (!string.IsNullOrEmpty(configured)) key = Encoding.UTF8.GetBytes(configured);
        else
        {
            var directory = Path.Combine(environment.ContentRootPath, "App_Data");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "jwt-signing.key");
            if (!File.Exists(path))
            {
                try { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(RandomNumberGenerator.GetBytes(64)); }
                catch (IOException) when (File.Exists(path)) { }
            }
            key = File.ReadAllBytes(path);
        }
        if (key.Length < 32) throw new InvalidOperationException("JWT signing key must contain at least 32 bytes.");
        Key = new SymmetricSecurityKey(key);
    }
    public string Issue(DeskAccount account)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> {
            new(JwtRegisteredClaimNames.Sub, account.Id.ToString()),
            new("name", account.Name), new("role", account.Role.ToString()),
            new("stamp", account.SecurityStamp.ToString()), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (account.DeskUserId.HasValue) claims.Add(new("desk_user_id", account.DeskUserId.Value.ToString()));
        if (account.DeskUser?.ClientId is int companyId) claims.Add(new("company_id", companyId.ToString()));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
            now, now.Add(Lifetime), new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
    }
    public void SetCookie(HttpContext context, DeskAccount account) => context.Response.Cookies.Append(CookieName, Issue(account), new CookieOptions {
        HttpOnly = true, Secure = requireSecure || context.Request.IsHttps, SameSite = SameSiteMode.Strict,
        Path = "/", IsEssential = true, Expires = DateTimeOffset.UtcNow.Add(Lifetime)
    });
}
