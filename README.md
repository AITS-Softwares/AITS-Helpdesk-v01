# AITS Helpdesk

ASP.NET Core MVC (.NET 8) with Entity Framework Core 8 Code First and SQL Server.

## Company and user workflow

1. Open **Companies** and create a client company with its name, contact email, and optional phone number.
2. Open that company and choose **Add user**, or choose **Users > Add user**.
3. Choose **ClientUser**, select a company in the company dropdown, and enter the user's name, email, and department. A company can have multiple users.
4. For internal AITS support people, choose **AitsStaff**. These users do not belong to a client company.
5. Create a ticket for a company, select a requester from that company's users, and optionally assign AITS staff.

Companies and users have separate create, list, edit, and confirmed delete pages. Company details show that company's users and tickets. Companies with users or tickets cannot be deleted; users referenced by tickets cannot be deleted. Ticket deletion also deletes its comments. Existing relationships must be removed or reassigned first.

## Sign-in, sign-up, and roles

1. Run the app and open `/Account/Setup` from localhost to create the **first admin**. Choose your own name, email, and password (at least 12 characters). No default credentials are created. Setup is restricted to loopback requests and closes after the first admin exists.
2. AITS administrators and staff share `/Account/AitsLogin`. Client users sign in at `/Account/Login`. The two pages validate the account group, and the database determines the admin/staff role. Only admins can create, edit, or delete companies, manage directory users, and enable sign-in accounts.
3. To add an AITS support account: **Users > Add user**, choose **AitsStaff**, save, then choose **Enable sign-in** for that user (or open **Sign-in accounts > Enable user sign-in**). Set an initial password and share it securely. Staff sign in at `/Account/AitsLogin`. They can also register at `/Account/AitsSignup`; self-registered staff remain pending until an admin selects **Approve staff** under **Sign-in accounts**. Public AITS signup cannot create an admin or bypass approval.
4. Client users register at `/Account/Signup`, selecting an existing company, and sign in at `/Account/Login`. Public registration always creates a ClientUser; submitted role or user-type fields cannot grant privileges. For a client who already has a directory record, the admin enables sign-in on that record rather than letting public registration claim its email.
5. AITS staff can read companies and manage all tickets, but cannot manage companies, users, or accounts. Client users can create and follow only their own tickets within their registered company. The server enforces access for list, detail, edit, comment, and delete actions, independently of visible controls.

### JWT behavior

Passwords use ASP.NET Core's salted password hasher. Signed JWTs expire after one hour. JWT bearer authentication validates signing algorithm/signature, issuer, audience, and expiry, then verifies the account's current role, security stamp, user link, and company membership against SQL Server. The MVC browser stores its JWT in an HttpOnly, SameSite=Strict cookie; tokens are not exposed in HTML or stored in browser localStorage. `Authorization: Bearer <token>` is also accepted. `/Account/Me` returns the authenticated account's role and user/company identifiers without returning a token.

Cookies require HTTPS outside Development. Use the HTTPS launch profile for normal use. Development also supports local HTTP for testing. JWT issuer/audience are in appsettings; signing material is generated once in the ignored `App_Data/jwt-signing.key`, or can be supplied through the `Jwt__SigningKey` environment variable (at least 32 bytes). Keep the signing key private and stable across restarts. Multi-instance hosting needs a shared configured key.

Sign-out is a POST protected by anti-forgery validation and revokes all of that account's outstanding JWTs. Admin edits to a linked user revoke their sessions; deleting an unused user also removes their sign-in account. Login has five-failure/15-minute account lockout, and login/signup/setup POSTs are rate limited per IP. All mutations retain anti-forgery validation. Password reset, email verification, and MFA are not included.

## Run

Use a .NET SDK supporting .NET 8, the .NET 8 runtime, and a running SQL Server instance at `localhost`. The configured Windows account needs access to the database and permission to apply migrations (including CREATE DATABASE for a fresh installation). From the solution directory:

```powershell
dotnet restore "Help Desk.sln"
dotnet run --project "Help Desk/Help Desk.csproj"
```

Open the localhost URL printed by the app, or use Visual Studio F5. Stop debugging with Shift+F5 before rebuilding to release the executable file.

## Entity Framework Core Code First

- `Help Desk/Data/HelpDeskDbContext.cs`: DbSets and relationship configuration.
- `Help Desk/Models/HelpDeskModels.cs`: company, user, ticket, and comment entities.
- `Help Desk/Models/AccountModels.cs`: sign-in accounts and authentication view models.
- `Help Desk/Services/JwtService.cs`: JWT creation and browser cookie.
- `Help Desk/Services/CurrentAccess.cs`: authenticated ticket/company/user scopes.
- `Help Desk/Migrations/`: generated InitialSqlServer migration and model snapshot.
- `Help Desk/appsettings.json`: `ConnectionStrings:HelpDesk`.

The SQL Server migration creates Companies, Users, Tickets, Comments, and DatabaseStates; AddJwtAccounts adds Accounts, with foreign keys, case-insensitive unique company names and user emails, and relationship restrictions. Normal reads and writes use EF Core, not JSON. The dashboard uses a read-only EF projection.

The database is **AitsHelpDesk** on **localhost**, using Windows integrated authentication. The application's configured connection is:

```text
Data Source=localhost;Initial Catalog=AitsHelpDesk;Integrated Security=True;Persist Security Info=False;Pooling=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Application Name=AITS Helpdesk;Command Timeout=0
```

Change `ConnectionStrings:HelpDesk` in `Help Desk/appsettings.json` to use a different server or database. Run Visual Studio as a Windows account with SQL Server access.

Startup applies the SQL Server migration and initializes data once. If an earlier `App_Data/helpdesk.db` SQLite database exists, its companies, users, tickets, and comments are imported into an empty SQL Server database. Otherwise the existing `App_Data/helpdesk.json` is imported, or sample company/user records are created. Company, user, and ticket IDs are preserved; comments receive SQL Server-generated IDs. Imports run in a transaction, and a database marker prevents duplicate imports on restart. The old files are retained unchanged. The SQLite package remains solely to read these older databases; all normal application reads and writes use SQL Server.

The superseded SQLite migration source is archived as noncompiled text under `Data/LegacySqliteMigrations/`. The active `Migrations/` folder contains SQL Server migrations only. No old database is dropped by the switch.

Back up existing databases before deployment. For shared/production environments, apply reviewed migrations through the deployment process instead of relying on startup migrations.

CLI migration commands from the solution directory:

```powershell
dotnet tool restore
dotnet ef database update --project "Help Desk/Help Desk.csproj"
# After changing entity models:
dotnet ef migrations add YourChangeName --project "Help Desk/Help Desk.csproj"
```

In Visual Studio's Package Manager Console, select **Help Desk** as the default project:

```powershell
Update-Database
# After changing entity models:
Add-Migration YourChangeName
```

InitialSqlServer, AddJwtAccounts, and AddStaffApproval are already included; do not add them again. See Microsoft's [EF Core migrations documentation](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/) for the Code First workflow.

## Verification

```powershell
dotnet build "Help Desk.sln"
python tests/smoke.py
python tests/smoke.py --fresh
python tests/auth_smoke.py
```

The Windows Python standard-library smoke test uses PowerShell's built-in SQL client to inspect a uniquely named disposable `AitsHelpDeskSmoke_<random>` SQL Server database. It creates a test-only admin, starts a hidden local app server, and checks migration application, legacy SQLite/JSON import, company/user/ticket CRUD, company dropdown choices, multiple users per company, duplicate validation, requester/assignee relationships, comments, delete restrictions and cascade behavior, anti-forgery protection, and persistence across restarts. `--fresh` verifies initialization without legacy files. The test drops only its own disposable database and does not modify AitsHelpDesk. The Windows account running tests needs database create/drop permission.

The provider setup follows Microsoft's [SQL Server EF Core documentation](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/).

The authentication smoke test additionally checks first-admin setup closure, AITS/client sign-in separation, both signup pages, staff approval, and database-assigned roles, public signup privilege restrictions, password hashing, HttpOnly token cookies, admin-only company creation, staff permissions, ticket owner/company isolation, forced ownership on client writes, invalid/expired JWT rejection, anti-forgery, sign-out revocation, and session revocation after profile changes. All test accounts live only in disposable databases.

Authentication configuration follows Microsoft's [JWT bearer documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-8.0).
