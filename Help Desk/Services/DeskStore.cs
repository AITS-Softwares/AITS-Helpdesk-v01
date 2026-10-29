using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Services;

public class DeskStore(CurrentAccess access)
{
    public DeskData Read() => new() {
        Projects = access.Projects.AsNoTracking().Include(p => p.Client).OrderBy(p => p.Client!.Name).ThenBy(p => p.Name).ToList(),
        Clients = access.Companies.AsNoTracking().OrderBy(c => c.Name).ToList(),
        Users = access.Users.AsNoTracking().OrderBy(u => u.Name).ToList(),
        Tickets = access.Tickets.AsNoTracking().OrderByDescending(t => t.UpdatedAt).ToList()
    };
}
