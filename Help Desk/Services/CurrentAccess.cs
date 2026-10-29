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
    public IQueryable<CompanyProject> Projects => IsAdmin ? db.Projects
        : IsInternal ? db.Projects.Where(p => db.StaffProjects.Any(m => m.StaffId == UserId && m.ProjectId == p.Id))
        : db.Projects.Where(p => p.ClientId == CompanyId);
    public IQueryable<Client> TicketCompanies => IsAdmin ? db.Companies
        : IsInternal ? db.Companies.Where(c => Projects.Any(p => p.ClientId == c.Id))
        : db.Companies.Where(c => c.Id == CompanyId);
    public IQueryable<Ticket> Tickets => IsAdmin ? db.Tickets
        : IsInternal ? db.Tickets.Where(t => t.ProjectId != null && Projects.Any(p => p.Id == t.ProjectId && p.ClientId == t.ClientId))
        : db.Tickets.Where(t => t.RequesterId == UserId && t.ClientId == CompanyId);
    public IQueryable<Client> Companies => IsAdmin ? db.Companies : IsInternal ? TicketCompanies : db.Companies.Where(c => c.Id == CompanyId);
    public IQueryable<DeskUser> Users => IsAdmin ? db.Users : IsInternal ? db.Users.Where(u => u.Id == UserId || db.StaffProjects.Any(m => m.StaffId == u.Id && Projects.Any(p => p.Id == m.ProjectId)) || Tickets.Any(t => t.RequesterId == u.Id)) : db.Users.Where(u => u.Id == UserId && u.ClientId == CompanyId);
}
