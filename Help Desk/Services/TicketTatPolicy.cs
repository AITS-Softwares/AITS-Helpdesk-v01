using Help_Desk.Data;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;
namespace Help_Desk.Services;
public class TicketTatPolicy(HelpDeskDbContext db)
{
    public async Task<TatPolicy> ReadAsync() => await db.TatPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == 1) ?? new TatPolicy();
}
