"""Check ticket ownership, deletion permissions and combined company/project filtering."""
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
import uuid
from auth_smoke import Browser
from sqlserver import query

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'Help Desk/bin/EmailCheck/net8.0/Help Desk.dll'


def check():
    configured = json.loads((ROOT / 'Help Desk/appsettings.json').read_text())['ConnectionStrings']['HelpDesk']
    database = 'AitsHelpDeskSmoke_' + uuid.uuid4().hex
    connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=' + database, configured)
    master = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=master', configured)
    env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ConnectionStrings__HelpDesk': connection, 'Email__Enabled':'true', 'Email__Transport':'Pickup', 'Email__FromAddress':'test@example.com', 'Email__PollSeconds':'1'}
    process = None
    with tempfile.TemporaryDirectory(prefix='aits-demo-') as folder:
        log_path = Path(folder) / 'server.log'
        with log_path.open('w', encoding='utf-8') as log:
            try:
                command = ['dotnet', str(DLL), '--contentRoot', folder]
                seed = subprocess.run(command + ['--DemoData:Seed=true'], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90, creationflags=subprocess.CREATE_NO_WINDOW)
                assert seed.returncode == 0
                snapshot = query(connection, 'SELECT Email,PasswordHash FROM InternalAccounts UNION ALL SELECT Email,PasswordHash FROM ClientAccounts ORDER BY Email')
                credentials_path = Path(folder) / 'App_Data/demo-credentials.md'
                credentials = credentials_path.read_text()
                seed = subprocess.run(command + ['--DemoData:Seed=true'], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90, creationflags=subprocess.CREATE_NO_WINDOW)
                assert seed.returncode == 0
                assert query(connection, 'SELECT Email,PasswordHash FROM InternalAccounts UNION ALL SELECT Email,PasswordHash FROM ClientAccounts ORDER BY Email') == snapshot
                assert credentials_path.read_text() == credentials
                assert query(connection, "SELECT (SELECT COUNT(*) FROM Companies WHERE Name LIKE 'Demo %'), (SELECT COUNT(*) FROM Projects), (SELECT COUNT(*) FROM InternalAccounts) + (SELECT COUNT(*) FROM ClientAccounts), (SELECT COUNT(*) FROM StaffProjects), (SELECT COUNT(*) FROM Tickets)")[0] == [3, 9, 10, 10, 27]
                tickets = query(connection, 'SELECT Id,Title,ClientId,ProjectId,RequesterId FROM Tickets')
                memberships = query(connection, 'SELECT u.Email,s.ProjectId FROM StaffProjects s JOIN AitsStaff u ON u.Id=s.StaffId')
                users = dict(query(connection, 'SELECT Email,Id FROM AitsStaff UNION ALL SELECT Email,Id FROM ClientUsers'))
                logins = re.findall(r'^\| ([^|]+) \| ([^|]+) \| (AITS sign in|Client sign in) \|$', credentials, re.MULTILINE)
                assert len(logins) == 10
                assert any(email == "1@gmail.com" for email, _, _ in logins)
                assert "demo.client1.user1@example.com" not in credentials
                with socket.socket() as reservation:
                    reservation.bind(('127.0.0.1', 0))
                    port = reservation.getsockname()[1]
                base = f'http://127.0.0.1:{port}'
                process = subprocess.Popen(command + ['--urls', base], env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
                anonymous = Browser(base)
                for _ in range(100):
                    assert process.poll() is None
                    try:
                        if anonymous.request('/Account/Login')[0] == 200:
                            break
                    except OSError:
                        time.sleep(0.2)
                else:
                    raise AssertionError('Server startup timed out')
                # Full demo login coverage lives in demo_smoke.py; keep this suite within the auth rate limit.
                for email, password, area in logins[:2]:
                    browser = Browser(base)
                    is_staff = area == 'AITS sign in'
                    browser.form('/Account/Login', {'Email': email, 'Password': password, 'Area': 1 if is_staff else 0}, '/Account/AitsLogin' if is_staff else '/Account/Login')
                    assert browser.request('/Account/Me')[0] == 200
                    page = browser.request('/Tickets')[1]
                    assigned = {project_id for member_email, project_id in memberships if member_email == email}
                    expected = [t for t in tickets if (t[3] in assigned if is_staff else t[4] == users[email])]
                    assert expected
                    for ticket in tickets:
                        visible = ticket in expected
                        # Razor encodes punctuation such as apostrophes; these demo titles have none.
                        assert (ticket[1] in page) == visible, email
                    allowed, denied = expected[0], next(t for t in tickets if t not in expected)
                    assert browser.request(f'/Tickets/Details/{allowed[0]}')[0] == 200
                    assert browser.request(f'/Tickets/Details/{denied[0]}')[0] == 404
                from concurrent.futures import ThreadPoolExecutor
                staff_logins = [(e,p) for e,p,a in logins if a == 'AITS sign in']
                first_email, first_password = staff_logins[0]
                second_email, second_password = staff_logins[1]
                first_id, second_id = users[first_email], users[second_email]
                target = next(t for t in tickets if any(e == first_email and p == t[3] for e,p in memberships))
                ticket_id, _, company_id, project_id, requester_id = target
                query(connection, 'IF NOT EXISTS(SELECT 1 FROM StaffProjects WHERE StaffId=? AND ProjectId=?) INSERT INTO StaffProjects(StaffId,ProjectId) VALUES(?,?); UPDATE Tickets SET AssigneeId=NULL WHERE Id=?', [second_id,project_id,second_id,project_id,ticket_id])
                workers = []
                for email,password in [(first_email,first_password),(second_email,second_password)]:
                    worker=Browser(base)
                    worker.form('/Account/Login', {'Email':email,'Password':password,'Area':1}, '/Account/AitsLogin')
                    page=worker.request(f'/Tickets/Details/{ticket_id}')[1]
                    assert 'Take ownership' in page and 'Delete ticket' not in page
                    assert worker.request(f'/Tickets/Delete/{ticket_id}')[0] == 403
                    assert worker.form('/Tickets/Delete', {'id':ticket_id}, f'/Tickets/Details/{ticket_id}')[0] == 403
                    workers.append(worker)
                with ThreadPoolExecutor(max_workers=2) as pool:
                    results=list(pool.map(lambda w:w.form('/Tickets/Claim', {'id':ticket_id}, f'/Tickets/Details/{ticket_id}'),workers))
                assert all(r[0] == 200 for r in results)
                owner=query(connection,'SELECT AssigneeId FROM Tickets WHERE Id=?',[ticket_id])[0][0]
                assert owner in (first_id,second_id)
                for worker in workers:
                    assert 'Take ownership</button>' not in worker.request(f'/Tickets/Details/{ticket_id}')[1]
                    worker.form('/Tickets/Claim', {'id':ticket_id},f'/Tickets/Details/{ticket_id}')
                    assert query(connection,'SELECT AssigneeId FROM Tickets WHERE Id=?',[ticket_id])[0][0] == owner
                worker=workers[0]
                for route in ['/Clients','/Clients/Create','/Clients/Edit/1','/Users/ClientUsers','/Users/AitsStaff','/Users/Create','/Users/Projects/1','/Account','/Tickets/Create']:
                    assert worker.request(route)[0] == 403, route
                for route in ['/Clients/Save','/Clients/AddProject','/Clients/RemoveProject','/Users/Save','/Users/Projects','/Account/Approve']:
                    assert worker.form(route,{},f'/Tickets/Details/{ticket_id}')[0] == 403, route
                navigation=worker.request('/Tickets')[1]
                assert 'PEOPLE &amp; ACCESS' not in navigation and 'Manage people' not in navigation and '+ New ticket' not in navigation

                # Posting a different assignee through the edit endpoint cannot bypass claiming.
                result=worker.form('/Tickets/Save', {'Id':ticket_id,'Title':target[1],'Description':'Ownership test','Status':0,'Priority':1,'RequesterId':requester_id,'ProjectId':project_id,'AssigneeId':second_id if owner==first_id else first_id},f'/Tickets/Details/{ticket_id}')
                assert result[0] == 403
                assert worker.request(f'/Tickets/Edit/{ticket_id}')[0] == 403
                assert query(connection,'SELECT AssigneeId FROM Tickets WHERE Id=?',[ticket_id])[0][0] == owner
                for scope in [f'company:{company_id}',f'project:{project_id}']:
                    page=worker.request('/Tickets?scope='+scope)[1]
                    assert 'name="scope"' in page and 'name="ClientId"' not in page and 'name="ProjectId"' not in page
                    assert target[1] in page
                    assert f'value="{scope}" selected="selected"' in page
                client_email,client_password,_=next(row for row in logins if row[2]=='Client sign in')
                client=Browser(base)
                client.form('/Account/Login',{'Email':client_email,'Password':client_password,'Area':0},'/Account/Login')
                source_id=next(t[0] for t in tickets if t[4]==users[client_email])
                assert client.form('/Tickets/Claim',{'id':source_id},f'/Tickets/Details/{source_id}')[0] == 403
                admin=Browser(base)
                admin.form('/Account/Setup',{'Name':'Level Admin','Email':'admin@levels.example','Password':'Admin@1234','ConfirmPassword':'Admin@1234'},'/Account/Setup')
                admin.form('/Account/Login',{'Email':'admin@levels.example','Password':'Admin@1234','Area':1},'/Account/AitsLogin')
                assert admin.request(f'/Tickets/Edit/{ticket_id}')[0] == 200
                third_id=next(uid for email,uid in users.items() if email.endswith('@aitsind.com') and uid not in (first_id,second_id))
                fields={'id':project_id,'l1':[first_id],'l2':[second_id],'l3':[third_id]}
                assert admin.form('/Clients/AssignTeam',fields,f'/Clients/Project/{project_id}')[0] == 200
                assert query(connection,'SELECT StaffId,SupportLevel FROM StaffProjects WHERE ProjectId=? ORDER BY SupportLevel',[project_id]) == [[first_id,1],[second_id,2],[third_id,3]]
                invalid={**fields,'l2':[first_id]}
                assert 'only one level' in admin.form('/Clients/AssignTeam',invalid,f'/Clients/Project/{project_id}')[1]
                assert workers[0].form('/Clients/AssignTeam',fields,f'/Tickets/Details/{ticket_id}')[0] == 403
                assert workers[0].request(f'/MyWork/Project/{project_id}')[0] == 200
                other_project=next(p for e,p in memberships if p != project_id and not any(e2==first_email and p2==p for e2,p2 in memberships))
                assert workers[0].request(f'/MyWork/Project/{other_project}')[0] == 404
                assert workers[0].request(f'/MyWork/ClientUsers?projectId={other_project}')[0] == 404
                page=workers[0].request('/MyWork/ClientUsers')[1]
                expected_companies={r[0] for r in query(connection,'SELECT p.ClientId FROM StaffProjects m JOIN Projects p ON p.Id=m.ProjectId WHERE m.StaffId=?',[first_id])}
                for email,cid in query(connection,'SELECT Email,ClientId FROM ClientUsers'):
                    assert (email in page) == (cid in expected_companies), email
                company_page=workers[0].request(f'/MyWork/ClientUsers?projectId={project_id}')[1]
                for email,cid in query(connection,'SELECT Email,ClientId FROM ClientUsers'):
                    assert (email in company_page) == (cid == company_id), email

                page=workers[0].request(f'/MyWork/Project/{project_id}')[1]
                assert 'L1 team' in page and 'L2 team' in page and 'L3 team' in page
                third_email=next(e for e,i in users.items() if i==third_id)
                third_password=next(p for e,p,a in logins if e==third_email)
                third=Browser(base)
                third.form('/Account/Login',{'Email':third_email,'Password':third_password,'Area':1},'/Account/AitsLogin')
                query(connection,'UPDATE Tickets SET AssigneeId=NULL,SupportLevel=1,Priority=1 WHERE Id=?',[ticket_id])
                page=workers[1].request(f'/Tickets/Details/{ticket_id}')[1]
                assert 'Ticket assignment' in page and 'Assign / escalate to team' in page and 'Only the L1 team can take ownership' in page
                workers[1].form('/Tickets/Escalate',{'id':ticket_id,'level':2,'reason':'Assign unowned ticket directly to L2'},f'/Tickets/Details/{ticket_id}')
                assert query(connection,'SELECT SupportLevel,AssigneeId FROM Tickets WHERE Id=?',[ticket_id])[0] == [2,None]
                query(connection,'DELETE FROM TicketEscalations WHERE TicketId=?',[ticket_id])
                # A normal L1 ticket can only be claimed by its L1 team.
                query(connection,'UPDATE Tickets SET AssigneeId=NULL,SupportLevel=1,Priority=1 WHERE Id=?',[ticket_id])
                assert workers[1].form('/Tickets/Claim',{'id':ticket_id},f'/Tickets/Details/{ticket_id}')[0] == 403
                workers[0].form('/Tickets/Claim',{'id':ticket_id},f'/Tickets/Details/{ticket_id}')
                workers[0].form('/Tickets/Escalate',{'id':ticket_id,'level':2,'reason':'Needs specialist investigation'},f'/Tickets/Details/{ticket_id}')
                assert query(connection,'SELECT SupportLevel,AssigneeId FROM Tickets WHERE Id=?',[ticket_id])[0] == [2,None]
                assert query(connection,'SELECT FromLevel,ToLevel FROM TicketEscalations WHERE TicketId=?',[ticket_id]) == [[1,2]]
                assert workers[0].form('/Tickets/Claim',{'id':ticket_id},f'/Tickets/Details/{ticket_id}')[0] == 403
                workers[1].form('/Tickets/Claim',{'id':ticket_id},f'/Tickets/Details/{ticket_id}')
                workers[1].form('/Tickets/Escalate',{'id':ticket_id,'level':3,'reason':'L3 diagnosis needed'},f'/Tickets/Details/{ticket_id}')
                assert query(connection,'SELECT SupportLevel,AssigneeId FROM Tickets WHERE Id=?',[ticket_id])[0] == [3,None]
                third.form('/Tickets/Claim',{'id':ticket_id},f'/Tickets/Details/{ticket_id}')
                assert query(connection,'SELECT AssigneeId FROM Tickets WHERE Id=?',[ticket_id])[0][0] == third_id
                assert workers[0].form('/Tickets/WorkStatus',{'id':ticket_id,'status':1},f'/Tickets/Details/{ticket_id}')[0] == 403
                third.form('/Tickets/WorkStatus',{'id':ticket_id,'status':1},f'/Tickets/Details/{ticket_id}')
                assert query(connection,'SELECT Status,CASE WHEN WorkStartedAt IS NOT NULL THEN 1 ELSE 0 END FROM Tickets WHERE Id=?',[ticket_id])[0] == [1,1]
                third.form('/Tickets/Comment',{'id':ticket_id,'body':'Investigating the reported issue.'},f'/Tickets/Details/{ticket_id}')
                assert query(connection,'SELECT AuthorName FROM Comments WHERE TicketId=? AND Body=?',[ticket_id,'Investigating the reported issue.'])[0][0]
                third.form('/Tickets/WorkStatus',{'id':ticket_id,'status':2},f'/Tickets/Details/{ticket_id}')
                assert query(connection,'SELECT Status,CASE WHEN ClosedAt >= WorkStartedAt THEN 1 ELSE 0 END FROM Tickets WHERE Id=?',[ticket_id])[0] == [2,1]

                # Create an urgent client ticket: it must start at L1 and notify only L1.
                requester_email=query(connection,'SELECT Email FROM ClientUsers WHERE Id=?',[requester_id])[0][0]
                requester_password=next(p for e,p,a in logins if e==requester_email)
                requester=Browser(base)
                requester.form('/Account/Login',{'Email':requester_email,'Password':requester_password,'Area':0},'/Account/Login')
                result=requester.form('/Tickets/Save',{'Title':'Urgent routing proof','Description':'Project L3 only','ProjectId':project_id,'Priority':3,'Severity':3},'/Tickets/Create')
                assert result[0] == 200
                urgent_id,level,severity=query(connection,"SELECT Id,SupportLevel,Severity FROM Tickets WHERE Title='Urgent routing proof'")[0]
                assert (level,severity)==(1,1)
                admin.form('/Tickets/Save',{'Id':ticket_id,'Title':target[1],'Description':'Now urgent','RequesterId':requester_id,'ProjectId':project_id,'Priority':3,'Severity':2,'Status':0,'AssigneeId':first_id},f'/Tickets/Edit/{ticket_id}')
                assert query(connection,'SELECT SupportLevel,AssigneeId FROM Tickets WHERE Id=?',[ticket_id])[0] == [1,first_id]

                assert query(connection,'SELECT StaffId,TargetLevel FROM TicketEmailNotifications WHERE TicketId=?',[urgent_id]) == [[first_id,1]]
                for _ in range(60):
                    delivery=query(connection,'SELECT Status FROM TicketEmailNotifications WHERE TicketId=?',[urgent_id])
                    if delivery == [[2]]: break
                    time.sleep(.2)
                assert delivery == [[2]], delivery
                assert list((Path(folder)/'App_Data/email-pickup').glob('*.eml'))
                progress_page=requester.request(f'/Tickets/Details/{ticket_id}')[1]
                assert 'Started working on this ticket.' in progress_page and 'Resolved this ticket.' in progress_page and 'Investigating the reported issue.' in progress_page
                page=requester.request(f'/Tickets/Details/{urgent_id}')[1]
                assert 'Elapsed time since creation' in page and 'not staff hours worked' in page and 'escalationReason' not in page
                assert 'Resolution progress' in page and '4h' in page and '<label>Severity</label>' not in page
                assert query(connection,'SELECT DATEDIFF(hour,CreatedAt,ResolutionDueAt) FROM Tickets WHERE Id=?',[urgent_id])[0][0] == 4
                assert client.form('/Tickets/WorkStatus',{'id':source_id,'status':1},f'/Tickets/Details/{source_id}')[0] == 403
                assert client.request(f'/Tickets/Edit/{source_id}')[0] == 403
                assert client.form('/Tickets/Save',{'Id':source_id,'Title':'Tampered','Description':'Tampered'},f'/Tickets/Details/{source_id}')[0] == 403
                staff=workers[0]
                result=staff.form('/MyWork/CreateClient',{'Name':'Scoped client','Email':'scoped.client@example.com','Department':'Support','ClientId':company_id,'UserType':1,'Id':first_id},'/MyWork/CreateClient')
                assert result[0] == 200 and 'Enable client sign-in' in result[1]
                new_user=query(connection,"SELECT Id FROM ClientUsers WHERE Email='scoped.client@example.com'")[0][0]
                assert not query(connection,"SELECT Id FROM AitsStaff WHERE Email='scoped.client@example.com'")
                page=staff.request('/MyWork/ClientUsers')[1]
                assert 'Enable sign-in' in page and 'scoped.client@example.com' in page
                result=staff.form(f'/Account/Create?userId={new_user}',{'DeskUserId':first_id,'Password':'Admin@1234','ConfirmPassword':'Admin@1234','Staff':'true','Role':0},f'/Account/Create?userId={new_user}')
                assert result[0] == 200
                assert query(connection,'SELECT Role FROM ClientAccounts WHERE DeskUserId=?',[new_user]) == [[2]]
                assert staff.request(f'/Account/Create?userId={first_id}')[0] == 404
                assert staff.form(f'/Account/Create?userId={first_id}',{'DeskUserId':first_id,'Password':'Admin@1234','ConfirmPassword':'Admin@1234'},f'/Tickets/Details/{ticket_id}')[0] == 404
                query(connection,"INSERT Companies(Name,Email) VALUES('Unassigned Company Test','unassigned@example.com')")
                outside_id=query(connection,"SELECT Id FROM Companies WHERE Name='Unassigned Company Test'")[0][0]
                assert staff.request(f'/MyWork/CreateClient?companyId={outside_id}')[0] == 404
                assert staff.form('/MyWork/CreateClient',{'Name':'Forbidden','Email':'forbidden@example.com','Department':'Support','ClientId':outside_id},'/MyWork/CreateClient')[0] == 403
                assert not query(connection,"SELECT Id FROM ClientUsers WHERE Email='forbidden@example.com'")
                query(connection,"INSERT ClientUsers(Name,Email,Department,ClientId,UserType) VALUES('Outside client','outside.client@example.com','Support',?,0)",[outside_id])
                outside_user=query(connection,"SELECT Id FROM ClientUsers WHERE Email='outside.client@example.com'")[0][0]
                assert staff.request(f'/Account/Create?userId={outside_user}')[0] == 404
                assert staff.form(f'/Account/Create?userId={outside_user}',{'DeskUserId':outside_user,'Password':'Admin@1234','ConfirmPassword':'Admin@1234'},f'/Tickets/Details/{ticket_id}')[0] == 404
                assert 'outside.client@example.com' not in staff.request('/Account/Create?staff=true')[1]
                new_client=Browser(base)
                new_client.form('/Account/Login',{'Email':'scoped.client@example.com','Password':'Admin@1234','Area':0},'/Account/Login')
                assert new_client.request('/Account/Me')[0] == 200
                for account_page in ['/Account','/Account?clients=true']:
                    page=admin.request(account_page)[1]
                    assert '+ Enable user sign-in' not in page and '<th>Created (UTC)</th>' not in page
                    assert '+ Add client user' in page and '+ Add AITS staff' in page
                    assert 'Disable sign-in' in page and '>Edit</a>' in page and '>Delete</a>' in page
                client_page=admin.request('/Users/ClientUsers')[1]
                assert 'Sign-in account groups' in client_page and 'outside.client@example.com' in client_page
                assert f'/Account/Create?userId={outside_user}' in client_page
                assert f'/Users/Edit/{outside_user}' in client_page and f'/Users/Delete/{outside_user}' in client_page
                staff_directory=admin.request('/Users/AitsStaff')[1]
                assert '<th>Status</th>' not in staff_directory and '/Users/SetActive' not in staff_directory
                client_account_id=query(connection,'SELECT Id FROM ClientAccounts WHERE DeskUserId=?',[new_user])[0][0]
                staff_account_id=query(connection,'SELECT Id FROM InternalAccounts WHERE DeskUserId=?',[first_id])[0][0]
                for clients_page in ['/Account?clients=true','/Account']:
                    assert '/Account/SetActive' in admin.request(clients_page)[1]
                assert staff.form('/Account/SetActive',{'id':client_account_id,'isActive':'false'},'/MyWork/ClientUsers')[0] == 403
                assert admin.form('/Account/SetActive',{'id':client_account_id},'/Account?clients=true')[0] == 400
                assert admin.form('/Account/SetActive',{'id':client_account_id,'isActive':'false'},'/Account?clients=true')[0] == 200
                assert new_client.request('/Account/Me')[0] == 401
                blocked=Browser(base)
                blocked.form('/Account/Login',{'Email':'scoped.client@example.com','Password':'Admin@1234','Area':0},'/Account/Login')
                assert blocked.request('/Account/Me')[0] == 401
                admin.form('/Account/SetActive',{'id':client_account_id,'isActive':'true'},'/Account?clients=true')
                assert new_client.request('/Account/Me')[0] == 401
                blocked.form('/Account/Login',{'Email':'scoped.client@example.com','Password':'Admin@1234','Area':0},'/Account/Login')
                assert blocked.request('/Account/Me')[0] == 200
                new_client=blocked
                admin.form('/Account/SetActive',{'id':staff_account_id,'isActive':'false'},'/Account')
                assert staff.request('/Account/Me')[0] == 401
                assert query(connection,'SELECT a.IsActive,s.IsActive FROM InternalAccounts a JOIN AitsStaff s ON s.Id=a.DeskUserId WHERE a.Id=?',[staff_account_id])[0] == [False,False]
                admin.form('/Account/SetActive',{'id':staff_account_id,'isActive':'true'},'/Account')
                assert staff.request('/Account/Me')[0] == 401
                staff.form('/Account/Login',{'Email':first_email,'Password':first_password,'Area':1},'/Account/AitsLogin')
                assert staff.request('/Account/Me')[0] == 200
                admin_account_id=query(connection,'SELECT Id FROM InternalAccounts WHERE Role=0')[0][0]
                assert admin.form('/Account/SetActive',{'id':admin_account_id,'isActive':'false'},'/Account')[0] == 400
                query(connection,'DELETE m FROM StaffProjects m JOIN Projects p ON p.Id=m.ProjectId WHERE m.StaffId=? AND p.ClientId=?',[first_id,company_id])
                assert staff.request(f'/Account/Create?userId={new_user}')[0] == 404
                assert staff.form('/MyWork/CreateClient',{'Name':'Revoked','Email':'revoked@example.com','Department':'Support','ClientId':company_id},'/MyWork/CreateClient')[0] == 403
                settings={'Enabled':'true','UserName':'settings@example.com','FromAddress':'settings@example.com','FromName':'AITS settings test','Host':'smtp.gmail.com','Port':587,'PublicBaseUrl':'https://helpdesk.example.com','Password':'test-app-password-123'}
                assert staff.request('/EmailSettings')[0] == 403
                assert new_client.request('/EmailSettings')[0] == 403
                assert staff.form('/EmailSettings',settings,'/MyWork/ClientUsers')[0] == 403
                assert admin.request('/EmailSettings')[0] == 200
                missing={**settings,'Password':''}
                assert 'app password' in admin.form('/EmailSettings',missing,'/EmailSettings')[1]
                result=admin.form('/EmailSettings',settings,'/EmailSettings')
                assert result[0] == 200 and 'Email settings saved' in result[1]
                private=Path(folder)/'App_Data'
                protected=(private/'smtp-password.protected').read_text()
                saved=(private/'email.local.json').read_text()
                assert settings['Password'] not in protected and settings['Password'] not in saved and settings['Password'] not in result[1]
                assert json.loads(saved)['Email']['FromAddress']=='settings@example.com'
                result=admin.form('/EmailSettings',{**settings,'Password':''},'/EmailSettings')
                assert 'Email settings saved' in result[1] and (private/'smtp-password.protected').read_text()==protected
                result=admin.form('/EmailSettings',{**settings,'Host':'not a hostname','Password':'must-not-be-saved'},'/EmailSettings')
                assert 'SMTP hostname' in result[1] and 'must-not-be-saved' not in result[1]
                assert (private/'smtp-password.protected').read_text()==protected
                assert not query(connection, 'DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS')
                print('PASS: project-level L1/L2/L3 teams, staff scope, escalation handovers, level-specific claiming, urgent L1 routing, client elapsed time and admin-only management.')
            except Exception:
                log.flush()
                print(log_path.read_text(encoding='utf-8')[-5000:])
                raise
            finally:
                if process is not None and process.poll() is None:
                    process.terminate()
                    process.wait(timeout=15)
                assert re.fullmatch(r'AitsHelpDeskSmoke_[0-9a-f]{32}', database)
                query(master, f"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END")


if __name__ == '__main__':
    check()
