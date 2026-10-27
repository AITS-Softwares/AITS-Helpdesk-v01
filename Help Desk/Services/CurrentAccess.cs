using System.Security.Claims;
using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Services;

public class CurrentAccess(IHttpContextAccessor accessor, HelpDeskDbContext db)
{
    private ClaimsPrincipal Principal => accessor.HttpContext?.User ?? new ClaimsPrincipal();
    public bool IsInternal => Principal.IsInRole(nameof(AccountRole.Admin)) || Principal.IsInRole(nameof(AccountRole.AitsUser));
    public bool IsAdmin => Principal.IsInRole(nameof(AccountRole.Admin));
    public int UserId => int.TryParse(Principal.FindFirstValue("desk_user_id"), out var id) ? id : -1;
    public int CompanyId => int.TryParse(Principal.FindFirstValue("company_id"), out var id) ? id : -1;
    public IQueryable<Ticket> Tickets => IsInternal ? db.Tickets : db.Tickets.Where(t => t.RequesterId == UserId && t.ClientId == CompanyId);
    public IQueryable<Client> Companies => IsInternal ? db.Companies : db.Companies.Where(c => c.Id == CompanyId);
    public IQueryable<DeskUser> Users => IsInternal ? db.Users : db.Users.Where(u => u.Id == UserId && u.ClientId == CompanyId);
}
