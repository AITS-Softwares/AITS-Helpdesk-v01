# AITS Helpdesk

ASP.NET Core MVC (.NET 8) with Entity Framework Core 8 Code First and SQL Server.

## Company and user workflow

1. Open **Companies** and create a client company with its name, contact email, and optional phone number.
2. Open that company and choose **Add user**, or choose **Company users > Add company user**.
3. In the company-user form, select a company in the company dropdown and enter the user's name, email, and department. A company can have multiple users.
4. For internal AITS support people, choose **AITS staff > Add AITS staff**. These users do not belong to a client company.
5. Create a ticket for a company, select a requester from that company's users, and optionally assign AITS staff.

Companies and users have separate create, list, edit, and confirmed delete pages. Company details show that company's users and tickets. Companies with users or tickets cannot be deleted; users referenced by tickets cannot be deleted. Ticket deletion also deletes its comments. Existing relationships must be removed or reassigned first.

## Sign-in, sign-up, and roles

1. Run the app and open `/Account/Setup` from localhost to create the **first admin**. Choose your own name, email, and password (at least 8 characters). No default credentials are created. Setup is restricted to loopback requests and closes after the first admin exists.
2. AITS administrators and staff share `/Account/AitsLogin`. Client users sign in at `/Account/Login`. The two pages validate the account group, and the database determines the admin/staff role. Admins and approved AITS staff have the same management permissions: they can create, edit, or delete companies, manage directory users and projects, enable sign-in accounts, and approve staff.
3. To add an AITS support account: **AITS staff > Add AITS staff**, save, then choose **Enable sign-in** for that user (or open **Sign-in accounts > Enable user sign-in**). Set an initial password and share it securely. Staff sign in at `/Account/AitsLogin`. They can also register at `/Account/AitsSignup`; self-registered staff remain pending until an admin or approved AITS staff member selects **Approve staff** under **Sign-in accounts**. Public AITS signup cannot create an admin or bypass approval.
4. Client users register at `/Account/Signup`, selecting an existing company, and sign in at `/Account/Login`. Public registration always creates a ClientUser; submitted role or user-type fields cannot grant privileges. For a client who already has a directory record, the admin enables sign-in on that record rather than letting public registration claim its email.
5. Approved AITS staff have the same permissions as admins, including company, project, user, sign-in account, staff approval, and ticket management. Client users can create and follow only their own tickets within their registered company. The server enforces access for list, detail, edit, comment, and delete actions, independently of visible controls.

### JWT behavior

Passwords use ASP.NET Core's salted password hasher. Signed JWTs expire after one hour. JWT bearer authentication validates signing algorithm/signature, issuer, audience, and expiry, then verifies the account's current role, security stamp, user link, and company membership against SQL Server. The MVC browser stores its JWT in an HttpOnly, SameSite=Strict cookie; tokens are not exposed in HTML or stored in browser localStorage. `Authorization: Bearer <token>` is also accepted. `/Account/Me` returns the authenticated account's role and user/company identifiers without returning a token.

Cookies require HTTPS outside Development. Use the HTTPS launch profile for normal use. Development also supports local HTTP for testing. JWT issuer/audience are in appsettings; signing material is generated once in the ignored `App_Data/jwt-signing.key`, or can be supplied through the `Jwt__SigningKey` environment variable (at least 32 bytes). Keep the signing key private and stable across restarts. Multi-instance hosting needs a shared configured key.

Sign-out is a POST protected by anti-forgery validation and revokes all of that account's outstanding JWTs. Admin edits to a linked user revoke their sessions; deleting an unused user also removes their sign-in account. Login has five-failure/15-minute account lockout, and login/signup/setup POSTs are rate limited per IP. All mutations retain anti-forgery validation. Admin-assisted password reset is available; email verification, email reset links, and MFA are not included.

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

The initial SQL Server migrations create Companies, Tickets, Comments, DatabaseStates and the original shared Users/Accounts tables. SeparateInternalAndClientStorage migrates the shared tables into AitsStaff, ClientUsers, InternalAccounts, and ClientAccounts. The current schema enforces foreign keys, case-insensitive unique company names and emails, account-group checks, and relationship restrictions. Normal reads and writes use EF Core, not JSON. The dashboard uses a read-only EF projection.

The database is **AitsHelpDesk** on **localhost**, using Windows integrated authentication. The application's configured connection is:

```text
Data Source=localhost;Initial Catalog=AitsHelpDesk;Integrated Security=True;Persist Security Info=False;Pooling=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Application Name=AITS Helpdesk;Command Timeout=0
```

Change `ConnectionStrings:HelpDesk` in `Help Desk/appsettings.json` to use a different server or database. Run Visual Studio as a Windows account with SQL Server access.

Startup applies the SQL Server migration and initializes data once. If an earlier `App_Data/helpdesk.db` SQLite database exists, its companies, users, tickets, and comments are imported into an empty SQL Server database. Otherwise the existing `App_Data/helpdesk.json` is imported, or the database starts empty. Legacy import requires the explicit Data:ImportLegacy=true setting; it is disabled by default. Company, user, and ticket IDs are preserved; comments receive SQL Server-generated IDs. Imports run in a transaction, and a database marker prevents duplicate imports on restart. The old files are retained unchanged. The SQLite package remains solely to read these older databases; all normal application reads and writes use SQL Server.

The superseded SQLite migration source is archived as noncompiled text under `Data/LegacySqliteMigrations/`. The active `Migrations/` folder contains SQL Server migrations only. No old database is dropped by the switch.

Back up existing databases before deployment. For shared/production environments, apply reviewed migrations through the deployment process instead of relying on startup migrations.

CLI migration commands from the solution directory:

```powershell
dotnet tool restore
dotnet ef database update --project "Help Desk/Help Desk.csproj" --context HelpDeskDbContext
# After changing entity models:
dotnet ef migrations add YourChangeName --project "Help Desk/Help Desk.csproj" --context HelpDeskDbContext
```

In Visual Studio's Package Manager Console, select **Help Desk** as the default project:

```powershell
Update-Database -Context HelpDeskDbContext
# After changing entity models:
Add-Migration YourChangeName -Context HelpDeskDbContext
```

InitialSqlServer, AddJwtAccounts, AddStaffApproval, AddCompanyProjects, AddStaffProjectAssignments, and SeparateInternalAndClientStorage are already included; do not add them again. See Microsoft's [EF Core migrations documentation](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/) for the Code First workflow.

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

## Company projects

Admins and AITS staff can add and remove projects (for example Vendor Portal, P2P, Expense Management) under **Companies > company details > Projects**. Each company has its own project list. Ticket creation filters projects by company and automatically selects a sole project; multiple projects require a selection. Companies with no projects can continue creating tickets without a project. Existing tickets are preserved. Used projects cannot be removed. Client accounts use their registered company automatically, and an existing ticket’s company cannot be changed, including through submitted form values. The AddCompanyProjects migration is applied on startup.

## Staff project assignments and ticket visibility

Admin sees every ticket. Under **Users > Assign projects**, Admin can select multiple company projects for each AITS staff member. Multiple staff can work on the same project, and one staff member can work across multiple companies. Only Admin can change these assignments. AITS staff retain their other management permissions, but their ticket lists, company ticket lists, dashboard totals, direct ticket links, edits, comments, and deletion are restricted to assigned projects. Staff can create or move tickets only into their assigned projects. Staff with no assignments see no tickets. Assignment changes take effect on the next request without signing out. Existing tickets without a project remain visible to Admin and their client owner; Admin should select a company project before staff can access them. The AddStaffProjectAssignments migration creates an empty assignment table without guessing existing staff memberships.

## Separate internal and client storage

Admin and AITS sign-ins are stored in **InternalAccounts**, linked to **AitsStaff** for staff accounts. Client sign-ins are stored in **ClientAccounts**, linked to **ClientUsers**. The old shared Users and Accounts tables are removed after their records have been copied. IDs, password hashes, approval/lockout state, security stamps, company membership, tickets, comments, and project assignments are preserved. Shared ID sequences avoid conflicting IDs across groups. Database constraints and triggers reject cross-group email or ID collisions and prevent client users from being ticket assignees or staff from being requesters.

The separate directories are **Company users** and **AITS staff** in the sidebar. **Sign-in accounts** has separate **Admin / AITS accounts** and **Client accounts** pages, and provisioning shows the selected group's directory users. A user's group is fixed at creation; create a new user in the other group rather than changing an existing user's type. Admin still sees all tickets; AITS staff still see only assigned project tickets.

The migration runs on the next app startup. Stop the old app before deploying the updated version. The migration is transactional and includes a data-preserving rollback. Use `python tests/split_migration_smoke.py` after building to verify an upgrade from populated shared tables, exact record preservation, rollback and re-upgrade, existing logins, separate pages, and database constraints. The read-only legacy SQLite importer uses its original schema independently of the current tables.

## Staff activity and project workspace

Admin can activate or deactivate staff under **Sign-in accounts → Admin / AITS accounts**, and clients under **Sign-in accounts → Client accounts**. The AITS staff directory no longer contains a status column or activation controls. Inactive staff cannot sign in; existing JWT sessions are revoked immediately, and staff cannot be newly assigned tickets while inactive. Reactivation preserves project assignments but requires a fresh sign-in. Only Admin can change staff activity. The AddStaffActivity migration marks existing staff active initially.

The staff directory lists each person's assigned company projects and activity status. Staff see their assigned projects on Overview and can open project-specific ticket lists; company and project filters stay within their assigned scope. Admin can select individual projects or use **Select all projects in this company** under **Assign projects**. Staff without assignments see guidance explaining why their ticket list is empty.


**Enable sign-in** now opens a fixed form for the exact directory user clicked. Their name, email, account type (AITS staff or Client user), and client company are read-only. The server derives the role from the saved user and ignores submitted role/type changes; direct links also fix the user ID during submission. Existing sign-ins show a status message instead of falling back to another user. The generic account creation page first selects a user, then opens that fixed form. Passwords require at least 8 characters.

The sidebar now opens distinct directory pages: **Client users** at `/Users/ClientUsers` (company users only, with a company filter) and **AITS staff** at `/Users/AitsStaff` (staff only, with project assignments and activity controls). The shared directory tabs are removed. Old `/Users` links redirect to Client users; old `/Users?staff=true` links redirect to AITS staff. Each page fixes its user group, regardless of submitted filter flags.

## Ticket priorities and optional photos

Ticket priorities have consistent labeled colors: Low (green), Medium (blue), High (orange), and Urgent (red). The priority selector changes color with the selection, and ticket lists/details use matching badges.

Creating or editing a ticket supports one optional PNG, JPG, or WebP photo up to 5 MB. The form previews a selected image and supports clearing it; editing can replace or remove the saved photo. Saving without a photo remains supported. The server checks size and image signatures rather than trusting the submitted MIME type. Photos are stored in TicketPhotos and served through `/Tickets/Photo/{ticketId}`, which enforces the same account/company/project access as the ticket. They are not public files. Deleting a ticket also deletes its photo.

## Admin-assisted forgot password

Both login pages include **Forgot password?**. A user enters their sign-in email to create a pending reset request; responses do not disclose whether the account exists, and duplicate pending requests are suppressed. No reset email is sent. Admin sees **Password reset requests** under **Sign-in accounts**, verifies the user's identity, selects **Reset password**, and sets an 8-character-or-longer password. Admin can also reset directly from an account row or dismiss a request without changing the password. Share the new password with the verified user securely.

Resetting changes the password hash, revokes old sessions, clears login failure/lockout state, and completes pending requests. It does not change roles, approval, activity, company membership, or project assignments. Resetting an inactive staff member's password does not activate them. Only Admin can reset passwords. An Admin who cannot sign in needs another Admin or the system maintainer's help.

The AddTicketPhotosAndPasswordResetRequests migration creates the two new tables on startup. `python tests/ticket_features_smoke.py` verifies optional uploads, image validation and access, replacement/removal/deletion, recovery requests, Admin-only resets, password length, session revocation, and staff activity preservation in a disposable database.

## Ticket-created email notifications

Creating a ticket queues a separate notification for each active, approved AITS sign-in account assigned to that exact company project. A similarly named project under another company does not match. Inactive, unapproved, unassigned, or directory-only staff are excluded. Editing tickets and adding comments do not queue new-ticket emails. Existing tickets are not emailed retroactively.

The ticket, photo, and notification rows commit in one database save. A background worker delivers queued notifications and retries failures with backoff. It rechecks the ticket's current company/project, staff membership, activity, approval, and email before sending, and skips notifications whose access/address changed. Staff receive individual messages with company, project, ticket number, title, description, priority, status, requester, and creation time. Set a shared **Email:PublicBaseUrl** to include a ticket link; otherwise messages contain the details and ticket number without a link. Photos stay private in the app and are not attached to email. SMTP delivery may be retried after an uncertain network result; sent rows are not deliberately queued again.

Admin can inspect **Ticket email notifications** on a ticket's detail page and retry failed notifications after correcting settings. Sent notifications cannot be retried through that action. The AddTicketEmailNotifications migration creates the durable queue and its indexes.

Email is off in tracked appsettings.json. Per-machine settings belong in the ignored `Help Desk/App_Data/email.local.json`, or can be supplied through `Email__...` environment variables. Example settings (no password in this file):

```json
{
  "Email": {
    "Enabled": true,
    "Host": "smtp.gmail.com",
    "Port": 587,
    "UserName": "sender@example.com",
    "FromAddress": "sender@example.com",
    "FromName": "AITS Helpdesk",
    "Transport": "Smtp",
    "PublicBaseUrl": "https://your-helpdesk.example",
    "PollSeconds": 5,
    "MaxAttempts": 5
  }
}
```

SMTP uses STARTTLS with certificate validation. Store the password using the `--Email:Configure=true` maintenance mode, supplying it on standard input; it is encrypted with ASP.NET Core Data Protection in the ignored App_Data/smtp-password.protected file. Run configuration with the same OS account and content root as the app, and preserve its Data Protection keys. `--Email:CheckCredentials=true` verifies that the current app can read the protected credential without sending a message. A deployment secret can alternatively supply Email__Password. Do not place credentials in tracked configuration or command-line arguments.

The Development-only `Email:Transport=Pickup` option writes test .eml files under that environment's App_Data/email-pickup directory and never contacts SMTP. `python tests/email_smoke.py` uses a disposable SQL Server database and this local transport to test routing, content, privacy checks, retries, and creation-only delivery; it does not send real test emails.

### Ticket editing and support levels

Only Admin can edit existing tickets, including requester details and staff assignments. Direct edit requests and non-Admin update submissions are denied. Staff retain project-scoped ownership claiming and comments. L1/L2/L3 teams are now configured per company project, as described below; individual-ticket level assignment controls have been removed. The current owner is shown separately on ticket details. The AddTicketSupportLevels and MoveSupportTeamsToProjects migrations apply on startup. Run python tests/ticket_ownership_smoke.py for concurrent ownership, editing and deletion permissions, level validation and combined-filter checks.

### Administrator and AITS staff permissions

Company and project management, staff and client directories, project assignments, account provisioning, approvals, password resets and all other administration pages are Admin-only. AITS staff have their assigned-project overview and tickets, can claim unassigned tickets and comment, and cannot create, edit or delete tickets. Client users retain ticket creation and their own ticket visibility. Both navigation and server-side endpoints enforce these roles.

### Project support teams, escalation and elapsed time

Admin manages support teams at Companies → company → project. Select multiple active AITS staff for each L1/L2/L3 team; each staff member has one level per project and can belong to multiple company projects. Team assignment also grants project access. Existing per-ticket level values are retained in LegacyL1AssigneeId/LegacyL2AssigneeId/LegacyL3AssigneeId columns; migration carries them into project memberships, choosing the highest recorded level where a staff/project pair previously had multiple levels. Other existing memberships default to L1. Review project teams after upgrading.

Individual-ticket L1/L2/L3 assignment controls are removed. All new tickets start at L1 regardless of priority, including High and Urgent, and new ticket emails go to active approved L1 staff for the exact company/project. Severity (Low/Medium/High/Critical) is recorded separately and does not affect routing. Staff can see all tickets in assigned projects; only members of the current support level can take ownership. Staff with project access can assign an unowned ticket to a higher level without claiming it first. Once owned, only its current owner can escalate to a higher level with a reason; handover clears ownership, records history and notifies the destination team. Email recipients are rechecked before sending and each staff member receives at most one notification per ticket.

AITS staff use My companies & projects to open assigned companies, projects and L1/L2/L3 teammates. Administration remains Admin-only. Clients see elapsed calendar time since ticket creation, not staff labor hours. Resolved/closed tickets stop at ClosedAt; historical resolved tickets use their last UpdatedAt as the available approximation. Escalation controls/history are internal. The MoveSupportTeamsToProjects migration applies on startup. The isolated ticket_ownership_smoke.py suite checks teams, scope, handovers, urgent L1 routing and client elapsed time using local email pickup only.

AITS staff have a scoped Client users page under MyWork/ClientUsers and a link from each assigned project. They can create client users and enable client sign-in only for companies where they currently have an assigned project. It shows company contacts only where the staff member currently has a project membership, with company filtering and the staff member's assigned project names. Client users have company membership rather than individual project membership. Unassigned-company and unassigned-project direct links are denied. Existing user editing/deletion, staff account provisioning, project assignments, account administration and password resets remain Admin-only. Staff client creation and sign-in provisioning recheck membership on every GET/POST, and cannot be changed into staff/Admin provisioning through submitted fields.

### Responsive interface

Shared responsive.css styles apply to administrator, AITS staff, client and authentication pages. The phone navigation is a keyboard-accessible disclosure (Menu and Escape); forms and project teams stack, and tables become labelled cards below 560px. Without JavaScript, navigation remains visible and tables remain horizontally scrollable. Mobile fields use 16px text and controls have larger touch targets. Desktop, 768px tablet, 390px phone and 320px narrow-phone checks covered the main role-specific pages using an isolated database. Preview images are kept under ignored App_Data/responsive-desktop.png and responsive-mobile.png.

### Admin email settings screen

Administrator → Email settings configures the sending account, sender name/address, SMTP hostname and STARTTLS port, optional public helpdesk URL, and notification enable/disable switch. Enter an app password to replace the encrypted credential; leave it blank to retain the current one. Passwords are never returned to the browser. SMTP settings are saved in ignored App_Data/email.local.json and the password in smtp-password.protected. Configuration reloads after saving without restarting. Environment/command-line overrides continue to take precedence. Staff and clients cannot read or submit this screen; saving never sends a test email.


The AddAccountActivity migration adds IsActive to both account tables, preserves existing staff inactivity, and defaults existing client accounts to active. Account activation is Admin-only, synchronizes staff directory activity, and revokes sessions on either change. Reactivation requires a fresh sign-in. Password resets and staff approval do not reactivate an account. Administrator accounts cannot be deactivated through this control.

### Combined user and account management

Administrator → Sign-in accounts is the combined directory for clients and internal users, including users without sign-in accounts. Both tabs offer Add client user and Add AITS staff. Each directory row offers Edit/Delete, staff project assignments where applicable, and Enable sign-in or Disable sign-in; password reset/approval remain available for existing accounts. Administrator rows retain their account-specific controls. The top generic Enable user sign-in button and Created date column are removed. Old Users/ClientUsers and Users/AitsStaff links redirect to the appropriate account tab, retaining company filtering. Staff's scoped MyWork client-creation/provisioning permissions are unchanged.

### Client resolution progress and TAT

Severity is no longer accepted in ticket forms or shown in ticket screens/emails; its legacy database values are preserved. Priority colors are Low blue, Medium yellow, High orange and Urgent red, with text labels. Resolution targets are 72/24/8/4 calendar hours respectively, starting at creation. New deadlines are saved with tickets; changing priority recalculates the deadline from creation. Existing deadlines are backfilled by AddTicketResolutionProgress.

Client ticket details include deadline, time remaining/overdue, actual creation-to-resolution duration, assigned staff, current team, work start/finish times and public progress history. Staff owners can Start work and Mark resolved without editing ticket fields. Other staff and clients cannot perform these actions. Resolution stops the elapsed clock; reopening clears the finish time. Ownership, escalation and admin assignment/status/priority changes are recorded, and new comments show author names/roles. Historical start times are not guessed. Time since work started is elapsed calendar time, not measured labor hours. New progress history is recorded from this upgrade onward.


## Production data

New installations start with no sample companies, users, or tickets. Demo seeding has been removed. Legacy file import is disabled unless Data:ImportLegacy=true is explicitly configured. The scripts/remove-demo-data.ps1 maintenance command backs up the configured SQL Server database before removing clearly identified seeded demo records; renamed records are preserved. Database backups and local demo/import artifacts are excluded from publish output.


## Ticket TAT and overdue alerts

Tickets use calendar-hour resolution targets from creation: Low 72h, Medium 24h, High 8h, and Urgent 4h. Admin can set a ticket-specific target in the create/edit form. The queue and ticket details show deadline, time remaining, and overdue status; counters update every 30 seconds while the page is open. All deadlines display UTC.

Staff see an overdue alert across signed-in pages for unresolved tickets assigned to them in their accessible projects. Admin sees all overdue tickets, including unassigned tickets. The alert lists the five earliest overdue tickets and the total count. Resolved/closed tickets stop generating alerts.

When email sending is enabled, the existing background email worker checks overdue tickets even when staff have no page open. One overdue email is queued per ticket, assigned staff member, and deadline. Reassignment or a new deadline can generate a new alert. The worker checks active, approved sign-in, project access, current ownership, deadline and unresolved status before delivery. Existing retries and Admin delivery history apply; stale notifications are skipped. Configure the sender and public site URL under Email settings. SMTP requires the app to remain running. Existing overdue tickets become eligible after startup.

The AddTatOverdueNotifications migration runs on startup. Verify with `dotnet build "Help Desk/Help Desk.csproj" -c TatCheck -p:UseAppHost=false` and `python tests/tat_smoke.py`. The test uses a disposable database and local pickup email; it does not send external test messages.

## Administrator priority TAT settings

Open **TAT settings** in the administrator sidebar (`/TatSettings`). Set Low, Medium, High and Urgent resolution targets in whole calendar hours (1–8,760), then choose **Save TAT settings**. Defaults are initially 72, 24, 8 and 4 hours. Settings are stored in SQL Server and take effect immediately for new tickets. Saving policy settings preserves existing ticket deadlines. Changing a ticket's priority applies the latest priority target unless a ticket-specific override is supplied. A custom TAT field is blank for tickets using standard priority targets; clearing a saved override restores the current priority default. The clock always starts at ticket creation.

The AddPriorityTatSettings migration creates the settings table, preserves legacy deadlines and identifies existing custom targets. Restart the updated application once to apply this migration. Administrator authorization, anti-forgery protection, server validation, database constraints, persistence, all four priority targets, unchanged existing deadlines and custom override/reset behavior are covered by `tests/tat_smoke.py`.
