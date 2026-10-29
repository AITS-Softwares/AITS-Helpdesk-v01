using System.Text.Json;
using Help_Desk.Models;
using Microsoft.EntityFrameworkCore;

namespace Help_Desk.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(HelpDeskDbContext db, IWebHostEnvironment environment, IConfiguration configuration)
    {
        await db.Database.MigrateAsync();
        if (await db.DatabaseStates.AnyAsync()) return;
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!await db.Companies.AnyAsync() && !await db.Users.AnyAsync() && !await db.Tickets.AnyAsync())
        {
            var legacyPath = Path.Combine(environment.ContentRootPath, "App_Data", "helpdesk.json");
            DeskData data;
            var sqlitePath = Path.Combine(environment.ContentRootPath, "App_Data", "helpdesk.db");
            if (configuration.GetValue<bool>("Data:ImportLegacy") && File.Exists(sqlitePath))
            {
                // Prefer the most recent database over the older JSON backup; never write to it.
                var sourceConnection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {
                    DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                };
                await using var source = new LegacyImportDbContext(new DbContextOptionsBuilder<LegacyImportDbContext>()
                    .UseSqlite(sourceConnection.ToString()).Options);
                data = new DeskData {
                    SchemaVersion = 1,
                    Clients = await source.Companies.AsNoTracking().ToListAsync(),
                    Users = (await source.Users.AsNoTracking().ToListAsync()).Select(u => u.ToEntity()).ToList(),
                    // Keep the legacy projection explicit: the read-only SQLite schema has no project fields.
                    Tickets = await source.Tickets.AsNoTracking().Select(t => new Ticket {
                        Id = t.Id, ClientId = t.ClientId, Title = t.Title, Description = t.Description,
                        Status = t.Status, Priority = t.Priority, RequesterId = t.RequesterId,
                        AssigneeId = t.AssigneeId, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt,
                        Comments = t.Comments.ToList()
                    }).ToListAsync()
                };
            }
            else if (configuration.GetValue<bool>("Data:ImportLegacy") && File.Exists(legacyPath))
            {
                // Never overwrite the original file. Failed imports roll back and can be retried.
                data = (JsonSerializer.Deserialize<LegacyDeskData>(await File.ReadAllTextAsync(legacyPath))
                    ?? throw new InvalidDataException("The existing JSON data could not be imported.")).ToDeskData();
                if (data.SchemaVersion < 1)
                {
                    data.Clients.Add(new Client { Id = 1, Name = "Existing client", Email = "client@example.com" });
                    foreach (var user in data.Users)
                    {
                        if (data.Tickets.Any(t => t.AssigneeId == user.Id) && !data.Tickets.Any(t => t.RequesterId == user.Id)) user.UserType = UserType.AitsStaff;
                        user.ClientId = user.UserType == UserType.ClientUser ? 1 : null;
                    }
                    foreach (var ticket in data.Tickets) ticket.ClientId = data.Users.Find(u => u.Id == ticket.RequesterId)?.ClientId ?? 1;
                }
            }
            else
            {
                data = new DeskData();
            }
            var comments = data.Tickets.SelectMany(ticket => {
                foreach (var comment in ticket.Comments) { comment.Id = 0; comment.TicketId = ticket.Id; comment.Ticket = null; }
                return ticket.Comments;
            }).ToList();
            foreach (var ticket in data.Tickets) ticket.Comments = [];
            await InsertWithIdentityAsync(db, data.Clients, "SET IDENTITY_INSERT [Companies] ON", "SET IDENTITY_INSERT [Companies] OFF");
            // Normalize pre-company JSON imports after their legacy user types have been inferred.
            db.Users.AddRange(data.Users.Select(u => UserEditViewModel.From(u).ToEntity()));
            await db.SaveChangesAsync();
            var nextUserId = data.Users.Select(u => u.Id).DefaultIfEmpty(0).Max() + 1;
            await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @next int = {nextUserId}; DECLARE @sql nvarchar(100) = N'ALTER SEQUENCE [UserIds] RESTART WITH ' + CONVERT(nvarchar(20), @next); EXEC sys.sp_executesql @sql;");
            await InsertWithIdentityAsync(db, data.Tickets, "SET IDENTITY_INSERT [Tickets] ON", "SET IDENTITY_INSERT [Tickets] OFF");
            db.Comments.AddRange(comments);
        }
        db.DatabaseStates.Add(new DatabaseState { InitializedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task InsertWithIdentityAsync<T>(HelpDeskDbContext db, List<T> entities, string enableSql, string disableSql) where T : class
    {
        if (entities.Count == 0) return;
        // Commands are fixed internal table names, never user-provided SQL.
        await db.Database.ExecuteSqlRawAsync(enableSql);
        try {
            db.Set<T>().AddRange(entities);
            await db.SaveChangesAsync();
        }
        finally { await db.Database.ExecuteSqlRawAsync(disableSql); }
    }
}

