using System.Threading.RateLimiting;
using Help_Desk.Models;
using Help_Desk.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Help_Desk.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("App_Data/email.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables().AddCommandLine(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<HelpDeskDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("HelpDesk")
    ?? throw new InvalidOperationException("The HelpDesk SQL Server connection string is missing.")));
builder.Services.AddScoped<Help_Desk.Services.DeskStore>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentAccess>();
builder.Services.AddScoped<TatAlerts>();
builder.Services.AddScoped<TicketTatPolicy>();
builder.Services.AddScoped<TatOverdueEmails>();
builder.Services.Configure<TicketEmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<EmailCredentialStore>();
builder.Services.AddSingleton<EmailSettingsStore>();
builder.Services.AddScoped<TicketCreatedEmails>();
builder.Services.AddScoped<TicketEmailDispatcher>();
builder.Services.AddHostedService<TicketEmailWorker>();
builder.Services.AddScoped<IPasswordHasher<DeskAccount>, PasswordHasher<DeskAccount>>();
var jwt = new JwtService(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => {
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = jwt.Key, ValidateLifetime = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name", RoleClaimType = "role"
    };
    options.Events = new JwtBearerEvents {
        OnMessageReceived = context => {
            if (!context.Request.Headers.ContainsKey("Authorization")) context.Token = context.Request.Cookies[JwtService.CookieName];
            return Task.CompletedTask;
        },
        OnTokenValidated = async context => {
            var principal = context.Principal!;
            if (!int.TryParse(principal.FindFirst("sub")?.Value, out var id)) { context.Fail("Invalid account."); return; }
            var db = context.HttpContext.RequestServices.GetRequiredService<HelpDeskDbContext>();
            var account = await db.AccountsWithUsers.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
            if (account is null || !account.IsApproved || !account.IsActive || account.SecurityStamp.ToString() != principal.FindFirst("stamp")?.Value ||
                account.Role.ToString() != principal.FindFirst("role")?.Value ||
                account.DeskUserId?.ToString() != principal.FindFirst("desk_user_id")?.Value ||
                account.DeskUser?.ClientId?.ToString() != principal.FindFirst("company_id")?.Value ||
                (account.Role == AccountRole.AitsUser && (account.DeskUser is not AitsStaffUser staff || !staff.IsActive)) ||
                (account.Role == AccountRole.ClientUser && account.DeskUser?.UserType != UserType.ClientUser))
                context.Fail("Account access changed. Sign in again.");
        },
        OnChallenge = context => {
            if (!context.Request.Headers.ContainsKey("Authorization") && context.Request.Method == "GET" && context.Request.Path != "/Account/Me") {
                context.HandleResponse();
                context.Response.Redirect("/Account/Login?returnUrl=" + Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
            }
            return Task.CompletedTask;
        },
        OnForbidden = context => {
            if (!context.Request.Headers.ContainsKey("Authorization") && context.Request.Method == "GET") context.Response.Redirect("/Account/Denied");
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<HelpDeskDbContext>(), app.Environment, builder.Configuration);

if (builder.Configuration.GetValue<bool>("Email:Configure"))
{
    var password = (await Console.In.ReadToEndAsync()).Trim();
    if (string.Equals(builder.Configuration["Email:Host"] ?? "smtp.gmail.com", "smtp.gmail.com", StringComparison.OrdinalIgnoreCase))
        password = string.Concat(password.Where(c => !char.IsWhiteSpace(c)));
    app.Services.GetRequiredService<EmailCredentialStore>().Save(password);
    Console.WriteLine("SMTP password saved using protected local storage. No message was sent.");
    return;
}
if (builder.Configuration.GetValue<bool>("Email:CheckCredentials"))
{
    var password = builder.Configuration["Email:Password"] ?? app.Services.GetRequiredService<EmailCredentialStore>().Read();
    if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("An SMTP password has not been configured.");
    Console.WriteLine("The protected SMTP credential is readable. No message was sent.");
    return;
}


// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.Use(async (context, next) => {
    context.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseRouting();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();




