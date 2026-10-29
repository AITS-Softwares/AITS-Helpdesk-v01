"""TAT integration checks in a disposable SQL database, using local email pickup only."""
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import tempfile
import time
import uuid
from email import policy
from email.parser import BytesParser
from auth_smoke import Browser
from sqlserver import query

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'Help Desk/bin/TatCheck/net8.0/Help Desk.dll'


def check():
    configured = json.loads((ROOT / 'Help Desk/appsettings.json').read_text())['ConnectionStrings']['HelpDesk']
    database = 'AitsTatTest_' + uuid.uuid4().hex
    connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=' + database, configured)
    master = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=master', configured)
    env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ConnectionStrings__HelpDesk': connection}
    process = None
    with tempfile.TemporaryDirectory(prefix='aits-tat-') as folder:
        content = Path(folder)
        (content / 'App_Data').mkdir()
        settings = {'Enabled': True, 'Transport': 'Pickup', 'FromAddress': 'alerts@notify.example', 'UserName': 'alerts@notify.example', 'PublicBaseUrl': 'https://helpdesk.example', 'PollSeconds': 1, 'MaxAttempts': 1}
        (content / 'App_Data/email.local.json').write_text(json.dumps({'Email': settings}))
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        log_path = content / 'server.log'
        with log_path.open('w', encoding='utf-8') as log:
            def rows(sql, args=()):
                return query(connection, sql, args)

            def wait_for(predicate):
                for _ in range(40):
                    if predicate():
                        return
                    time.sleep(0.25)
                raise AssertionError('Timed out waiting for expected state')

            try:
                process = subprocess.Popen(['dotnet', str(DLL), '--contentRoot', folder, '--urls', base], env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
                anonymous = Browser(base)
                for _ in range(150):
                    assert process.poll() is None, log_path.read_text()
                    try:
                        if anonymous.request('/Account/AitsLogin')[0] == 200:
                            break
                    except OSError:
                        pass
                    time.sleep(0.2)
                assert rows('SELECT COUNT(*) FROM Companies')[0][0] == 0
                password = secrets.token_urlsafe(12) + 'aA!7'
                admin = Browser(base)
                admin.form('/Account/Setup', {'Name': 'TAT Admin', 'Email': 'admin@notify.example', 'Password': password, 'ConfirmPassword': password}, '/Account/Setup')
                assert admin.form('/Account/Login', {'Email': 'admin@notify.example', 'Password': password, 'Area': 1}, '/Account/AitsLogin')[0] == 200
                staff = Browser(base)
                staff.form('/Account/AitsSignup', {'Name': 'TAT Staff', 'Email': 'staff@notify.example', 'Department': 'Support', 'Password': password, 'ConfirmPassword': password}, '/Account/AitsSignup')
                rows("UPDATE InternalAccounts SET IsApproved=1 WHERE Email='staff@notify.example'")
                assert staff.form('/Account/Login', {'Email': 'staff@notify.example', 'Password': password, 'Area': 1}, '/Account/AitsLogin')[0] == 200
                staff_id = rows("SELECT Id FROM AitsStaff WHERE Email='staff@notify.example'")[0][0]
                company = rows("INSERT Companies(Name,Email) OUTPUT INSERTED.Id VALUES('TAT Test Company','contact@notify.example')")[0][0]
                project = rows("INSERT Projects(ClientId,Name) OUTPUT INSERTED.Id VALUES(?,'TAT Test Project')", (company,))[0][0]
                requester = rows("INSERT ClientUsers(Id,Name,Email,Department,UserType,ClientId) VALUES(NEXT VALUE FOR UserIds,'Requester','requester@notify.example','Operations',0,?); SELECT Id FROM ClientUsers WHERE Email='requester@notify.example'", (company,))[0][0]
                rows('INSERT StaffProjects(StaffId,ProjectId,SupportLevel) VALUES(?,?,1)', (staff_id, project))

                def ticket(title, due_minutes, status=0, assignee=None, fallback=False):
                    return rows("INSERT Tickets(ClientId,ProjectId,Title,Description,Status,Priority,RequesterId,AssigneeId,CreatedAt,UpdatedAt,SupportLevel,Severity,ResolutionDueAt) OUTPUT INSERTED.Id VALUES(?,?,?,'TAT test',?,1,?,NULLIF(?,0),DATEADD(hour,-25,SYSUTCDATETIME()),SYSUTCDATETIME(),1,1,CASE WHEN ?=1 THEN NULL ELSE DATEADD(minute,?,SYSUTCDATETIME()) END)", (company, project, title, status, requester, assignee or 0, int(fallback), due_minutes))[0][0]

                overdue = ticket('Assigned overdue', -10, assignee=staff_id)
                future = ticket('Future target', 90, assignee=staff_id)
                resolved = ticket('Already resolved', -10, status=2, assignee=staff_id)
                unassigned = ticket('Unassigned overdue', -10)
                fallback = ticket('Priority fallback overdue', -10, assignee=staff_id, fallback=True)
                wait_for(lambda: rows('SELECT COUNT(*) FROM TicketEmailNotifications WHERE Kind=1 AND Status=2')[0][0] == 2)
                snapshot = json.loads(staff.request('/Tickets/TatAlerts')[1])
                assert snapshot['count'] == 2, snapshot
                assert {t['id'] for t in snapshot['tickets']} == {overdue, fallback}
                assert json.loads(admin.request('/Tickets/TatAlerts')[1])['count'] == 3
                page = staff.request('/Tickets')[1]
                assert 'Resolution TAT' in page and 'Assigned overdue' in page and 'TAT breached' in page
                assert anonymous.request('/Tickets/TatAlerts')[2].get('Content-Type', '').startswith('text/html')
                time.sleep(2)
                assert rows('SELECT COUNT(*) FROM TicketEmailNotifications WHERE Kind=1')[0][0] == 2
                messages = [BytesParser(policy=policy.default).parsebytes(path.read_bytes()) for path in (content / 'App_Data/email-pickup').glob('*.eml')]
                assert len(messages) == 2
                assert all('[TAT OVERDUE]' in str(m['Subject']) and str(m['To']) == 'staff@notify.example' for m in messages)
                assert all('Resolution deadline (UTC)' in m.get_body(preferencelist=('plain',)).get_content() for m in messages)
                # A different deadline can generate one new alert, without repeating the original one.
                rows('UPDATE Tickets SET ResolutionDueAt=DATEADD(minute,-20,SYSUTCDATETIME()) WHERE Id=?', (overdue,))
                wait_for(lambda: rows('SELECT COUNT(*) FROM TicketEmailNotifications WHERE Kind=1 AND Status=2')[0][0] == 3)
                rows('UPDATE Tickets SET Status=2, ClosedAt=SYSUTCDATETIME() WHERE Id IN (?,?)', (overdue, fallback))
                assert json.loads(staff.request('/Tickets/TatAlerts')[1])['count'] == 0
                # Admin can set ticket-specific TAT without changing priority defaults.
                result = admin.form('/Tickets/Save', {'Id': 0, 'Title': 'Custom TAT target', 'Description': 'Custom target test', 'ClientId': company, 'ProjectId': project, 'RequesterId': requester, 'Status': 0, 'Priority': 1, 'tatHours': 2}, '/Tickets/Create')
                assert result[0] == 200, result[:2]
                assert rows("SELECT DATEDIFF(minute,CreatedAt,ResolutionDueAt) FROM Tickets WHERE Title='Custom TAT target'")[0][0] == 120
                invalid = admin.form('/Tickets/Save', {'Id': 0, 'Title': 'Invalid target', 'Description': 'Invalid target test', 'ClientId': company, 'ProjectId': project, 'RequesterId': requester, 'Status': 0, 'Priority': 1, 'tatHours': 0}, '/Tickets/Create')
                assert 'TAT must be between' in invalid[1]
                assert rows("SELECT COUNT(*) FROM Tickets WHERE Title='Invalid target'")[0][0] == 0
                # A queued email is skipped if its ticket was resolved before delivery.
                rows("INSERT TicketEmailNotifications(TicketId,StaffId,CompanyId,ProjectId,RecipientEmail,Status,Attempts,CreatedAt,NextAttemptAt,Kind,AlertDueAt) SELECT Id,AssigneeId,ClientId,ProjectId,'staff@notify.example',0,0,SYSUTCDATETIME(),SYSUTCDATETIME(),1,ResolutionDueAt FROM Tickets WHERE Id=?", (resolved,))
                wait_for(lambda: rows('SELECT COUNT(*) FROM TicketEmailNotifications WHERE TicketId=? AND Kind=1 AND Status=5', (resolved,))[0][0] == 1)
                assert staff.request('/TatSettings', token=staff.token())[0] == 403
                # Deactivated staff must not get new alerts.
                rows('UPDATE AitsStaff SET IsActive=0 WHERE Id=?', (staff_id,))
                ticket('Inactive owner overdue', -10, assignee=staff_id)
                time.sleep(2)
                assert rows('SELECT COUNT(*) FROM TicketEmailNotifications WHERE Kind=1')[0][0] == 4
                # Administrator priority settings: authorization, validation, persistence and defaults.
                page = admin.request('/TatSettings')[1]
                assert 'Priority TAT settings' in page and 'LowHours' in page and 'UrgentHours' in page
                targets = {'LowHours': 96, 'MediumHours': 30, 'HighHours': 6, 'UrgentHours': 2}
                assert admin.request('/TatSettings', targets)[0] == 400
                before = rows('SELECT Id,CONVERT(varchar(33),ResolutionDueAt,126) FROM Tickets ORDER BY Id')
                saved = admin.form('/TatSettings', targets, '/TatSettings')
                assert saved[0] == 200 and 'Priority TAT settings saved' in saved[1]
                assert rows('SELECT LowHours,MediumHours,HighHours,UrgentHours FROM TatPolicies WHERE Id=1')[0] == [96,30,6,2]
                assert before == rows('SELECT Id,CONVERT(varchar(33),ResolutionDueAt,126) FROM Tickets ORDER BY Id')
                invalid_settings = admin.form('/TatSettings', {**targets, 'LowHours': 0}, '/TatSettings')
                assert 'between 1 and 8760' in invalid_settings[1]
                assert rows('SELECT LowHours FROM TatPolicies WHERE Id=1')[0][0] == 96
                for priority, hours in enumerate([96,30,6,2]):
                    fields = {'Id': 0, 'Title': f'Policy target {priority}', 'Description': 'Policy target test', 'ClientId': company, 'ProjectId': project, 'RequesterId': requester, 'Status': 0, 'Priority': priority}
                    assert admin.form('/Tickets/Save', fields, '/Tickets/Create')[0] == 200
                    assert rows('SELECT DATEDIFF(minute,CreatedAt,ResolutionDueAt),HasCustomTat FROM Tickets WHERE Title=?', (fields['Title'],))[0] == [hours * 60, False]
                changed_id = rows("SELECT Id FROM Tickets WHERE Title='Policy target 0'")[0][0]
                edit_fields = {'Id': changed_id, 'Title': 'Changed priority target', 'Description': 'Priority change test', 'ClientId': company, 'ProjectId': project, 'RequesterId': requester, 'Status': 0, 'Priority': 2}
                assert admin.form('/Tickets/Save', edit_fields, f'/Tickets/Edit/{changed_id}')[0] == 200
                assert rows('SELECT DATEDIFF(minute,CreatedAt,ResolutionDueAt) FROM Tickets WHERE Id=?', (changed_id,))[0][0] == 360
                custom_id = rows("SELECT Id FROM Tickets WHERE Title='Custom TAT target'")[0][0]
                custom_fields = {**edit_fields, 'Id': custom_id, 'Title': 'Custom TAT target', 'tatHours': 2}
                assert admin.form('/Tickets/Save', custom_fields, f'/Tickets/Edit/{custom_id}')[0] == 200
                assert rows('SELECT DATEDIFF(minute,CreatedAt,ResolutionDueAt),HasCustomTat FROM Tickets WHERE Id=?', (custom_id,))[0] == [120, True]
                del custom_fields['tatHours']
                assert admin.form('/Tickets/Save', custom_fields, f'/Tickets/Edit/{custom_id}')[0] == 200
                assert rows('SELECT DATEDIFF(minute,CreatedAt,ResolutionDueAt),HasCustomTat FROM Tickets WHERE Id=?', (custom_id,))[0] == [360, False]
                print('PASS: empty startup, TAT SQL translation, staff/admin visibility, overdue pickup email, deduplication, changed deadline, resolved/future/unassigned exclusion, inactive staff exclusion, custom TAT validation, stale queued email suppression, admin priority settings permissions/CSRF/validation/persistence, all four priority defaults, priority changes, existing deadline preservation, custom override/reset.')
            except Exception:
                print(log_path.read_text()[-14000:])
                raise
            finally:
                if process is not None and process.poll() is None:
                    process.terminate()
                    process.wait(timeout=20)
                query(master, 'IF DB_ID(?) IS NOT NULL BEGIN ALTER DATABASE [' + database + '] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [' + database + ']; END', (database,))


if __name__ == '__main__':
    check()





