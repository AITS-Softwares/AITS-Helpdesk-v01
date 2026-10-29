"""SQL Server integration checks for authentication and role/data boundaries."""
import base64
import hashlib
import hmac
import http.cookiejar
from html.parser import HTMLParser
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from sqlserver import query

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'Help Desk/bin/Debug/net8.0/Help Desk.dll'


class Browser:
    def __init__(self, base):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))

    def request(self, path, fields=None, token=None):
        headers = {'Authorization': 'Bearer ' + token} if token else {}
        data = None if fields is None else urllib.parse.urlencode(fields, doseq=True).encode()
        try:
            response = self.client.open(urllib.request.Request(self.base + path, data, headers), timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.read().decode(), response.headers

    def form(self, path, fields, source):
        status, body, _ = self.request(source)
        assert status == 200, (source, status)
        fields = {**fields, '__RequestVerificationToken': re.search(r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', body)[1]}
        return self.request(path, fields)

    def token(self):
        return next(cookie.value for cookie in self.jar if cookie.name == 'Aits.AccessToken')



class HiddenFormFields(HTMLParser):
    def __init__(self):
        super().__init__()
        self.fields = {}

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'input' and attrs.get('name'):
            self.fields[attrs['name']] = attrs.get('value', '')


def change_activity_from_rendered_form(browser, staff_id, active):
    status, page, _ = browser.request('/Account')
    assert status == 200
    for form in re.findall(r'<form\b[^>]*action="/Account/SetActive"[^>]*>(.*?)</form>', page, re.S):
        parser = HiddenFormFields()
        parser.feed(form)
        if parser.fields.get('staffId') != str(staff_id):
            continue
        assert parser.fields.get('isActive') == str(active).lower(), parser.fields.get('isActive')
        assert ('Enable sign-in' if active else 'Disable sign-in') in form
        return browser.request('/Account/SetActive', parser.fields)
    raise AssertionError('Staff status form was not rendered')


def check():
    configured = json.loads((ROOT / 'Help Desk/appsettings.json').read_text())['ConnectionStrings']['HelpDesk']
    database = 'AitsHelpDeskSmoke_' + uuid.uuid4().hex
    connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=' + database, configured)
    master = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=master', configured)
    # Exercise the minimum supported password across setup, signup, provisioning, and login.
    password = secrets.token_urlsafe(6)
    assert len(password) == 8
    with tempfile.TemporaryDirectory(prefix='aits-auth-') as folder:
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        log = open(Path(folder) / 'server.log', 'w', encoding='utf-8')
        env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ConnectionStrings__HelpDesk': connection}
        process = subprocess.Popen(['dotnet', str(DLL), '--urls', base, '--contentRoot', folder], env=env,
                                   stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        anonymous, admin, staff, alice, bob = [Browser(base) for _ in range(5)]

        def rows(sql, args=()):
            return query(connection, sql, args)

        def login(browser, email, portal):
            status, body, headers = browser.form('/Account/Login', {'Email': email, 'Password': password, 'Area': 0 if portal == 2 else 1, 'ReturnUrl': 'https://example.invalid'}, '/Account/Login')
            assert status == 200, status
            status, body, _ = browser.request('/Account/Me')
            assert status == 200, (email, body)
            return json.loads(body)

        try:
            for _ in range(120):
                if process.poll() is not None:
                    raise AssertionError('Server failed to start')
                try:
                    if anonymous.request('/Account/Login')[0] == 200:
                        break
                except (OSError, urllib.error.URLError):
                    time.sleep(.2)
            else:
                raise AssertionError('Server startup timed out')
            assert anonymous.request('/Account/Me')[0] == 401
            assert 'Sign in' in anonymous.request('/Tickets')[1]
            assert anonymous.request('/Clients/Save', {'Name': 'Unauthorized'})[0] == 401
            for route in ['/Account/Login', '/Account/AdminLogin', '/Account/AitsLogin', '/Account/Signup', '/Account/AitsSignup']:
                assert anonymous.request(route)[0] == 200
            admin.form('/Account/Setup', {'Name': 'Admin', 'Email': 'admin@auth.example', 'Password': password, 'ConfirmPassword': password}, '/Account/Setup')
            assert anonymous.request('/Account/Setup')[0] == 404
            assert login(admin, 'admin@auth.example', 0)['role'] == 'Admin'
            assert rows('SELECT COUNT(*) FROM InternalAccounts WHERE Role=0')[0][0] == 1
            assert rows('SELECT PasswordHash FROM InternalAccounts WHERE Role=0')[0][0] != password
            assert 'httponly' in {k.lower() for k in next(c for c in admin.jar if c.name == 'Aits.AccessToken')._rest}
            for name in ['Company Alpha', 'Company Beta']:
                assert admin.form('/Clients/Save', {'Id': 0, 'Name': name, 'Email': name.replace(' ', '') + '@auth.example'}, '/Clients/Create')[0] == 200
            alpha = rows("SELECT Id FROM Companies WHERE Name='Company Alpha'")[0][0]
            beta = rows("SELECT Id FROM Companies WHERE Name='Company Beta'")[0][0]
            for browser, name, company in [(alice, 'Alice', alpha), (bob, 'Bob', beta)]:
                result = browser.form('/Account/Signup', {'Name': name, 'Email': name + '@auth.example', 'Department': 'Finance',
                    'ClientId': company, 'Password': password, 'ConfirmPassword': password, 'Role': 0, 'UserType': 1}, '/Account/Signup')
                assert result[0] == 200
                assert login(browser, name + '@auth.example', 2)['role'] == 'ClientUser'
            person_id = rows("SELECT Id FROM ClientUsers WHERE Email='Alice@auth.example'")[0][0]
            other_id = rows("SELECT Id FROM ClientUsers WHERE Email='Bob@auth.example'")[0][0]
            staff_id = rows('SELECT TOP(1) Id FROM AitsStaff WHERE UserType=1')[0][0]
            # Enable sign-in fixes the selected user and derives the mandatory role from the database.
            staff_source = f'/Account/Create?userId={staff_id}&staff=false'
            staff_form = admin.request(staff_source)[1]
            assert 'value="AITS staff" readonly' in staff_form
            assert not re.search(r'<select[^>]*(?:name|id)="DeskUserId"', staff_form)
            assert 'minlength="8"' in staff_form and 'minlength="12"' not in staff_form
            assert admin.form(f'/Account/Create?userId={staff_id}', {'DeskUserId': other_id, 'userId': other_id, 'Staff': False, 'Role': 2, 'Password': password, 'ConfirmPassword': password}, staff_source)[0] == 200
            assert rows('SELECT Role,DeskUserId FROM InternalAccounts WHERE DeskUserId=?', (staff_id,))[0] == [1, staff_id]
            enabled_page = admin.request(staff_source)[1]
            assert 'Sign-in is already enabled' in enabled_page
            assert 'value="AITS staff" readonly' in enabled_page
            assert 'name="Password"' not in enabled_page
            client_id = rows('SELECT TOP(1) Id FROM ClientUsers u WHERE NOT EXISTS (SELECT 1 FROM ClientAccounts a WHERE a.DeskUserId=u.Id)')[0][0]
            client_source = f'/Account/Create?userId={client_id}&staff=true'
            client_form = admin.request(client_source)[1]
            assert 'value="Client user" readonly' in client_form
            assert 'id="selectedUserCompany"' in client_form
            assert not re.search(r'<select[^>]*(?:name|id)="DeskUserId"', client_form)
            invalid_password = admin.form(f'/Account/Create?userId={client_id}', {'DeskUserId': staff_id, 'Staff': True, 'Password': password[:7], 'ConfirmPassword': password[:7]}, client_source)
            assert 'validation-summary-errors' in invalid_password[1]
            assert 'value="Client user" readonly' in invalid_password[1]
            assert admin.form(f'/Account/Create?userId={client_id}', {'DeskUserId': staff_id, 'userId': staff_id, 'Staff': True, 'Role': 0, 'Password': password, 'ConfirmPassword': password}, client_source)[0] == 200
            assert rows('SELECT Role,DeskUserId FROM ClientAccounts WHERE DeskUserId=?', (client_id,))[0] == [2, client_id]
            assert admin.request('/Account/Create?userId=999999')[0] == 404
            staff_email = rows('SELECT Email FROM AitsStaff WHERE Id=?', (staff_id,))[0][0]
            assert login(staff, staff_email, 1)['role'] == 'AitsUser'
            pending = Browser(base)
            result = pending.form('/Account/AitsSignup', {'Name': 'Self Registered Staff', 'Email': 'pending@auth.example', 'Department': 'IT',
                'Password': password, 'ConfirmPassword': password, 'Role': 0, 'IsApproved': True}, '/Account/AitsSignup')
            assert result[0] == 200 and 'admin must approve' in result[1]
            account = rows("SELECT Id,Role,IsApproved FROM InternalAccounts WHERE Email='pending@auth.example'")[0]
            assert account[1:] == [1, False]
            result = pending.form('/Account/Login', {'Email': 'pending@auth.example', 'Password': password, 'Area': 1}, '/Account/AitsLogin')
            assert 'Unable to sign in' in result[1]
            assert pending.request('/Account/Me')[0] == 401
            assert alice.form('/Account/Approve', {'id': account[0]}, '/Tickets/Create')[0] == 403
            assert admin.form('/Account/Approve', {'id': account[0]}, '/Account')[0] == 200
            assert login(pending, 'pending@auth.example', 1)['role'] == 'AitsUser'
            for email, area in [('admin@auth.example', 0), ('Alice@auth.example', 1)]:
                result = anonymous.form('/Account/Login', {'Email': email, 'Password': password, 'Area': area}, '/Account/Login')
                assert 'Unable to sign in' in result[1]
                assert anonymous.request('/Account/Me')[0] == 401

            assert staff.request('/Clients/Create', token=staff.token())[0] == 200
            assert staff.request('/Users', token=staff.token())[0] == 200
            assert staff.request('/Account/Create', token=staff.token())[0] == 200
            assert staff.request('/Account', token=staff.token())[0] == 200
            assert staff.form('/Clients/Save', {'Id': 0, 'Name': 'Staff managed company', 'Email': 'staff-managed@example.com'}, '/Clients/Create')[0] == 200
            managed_company = rows("SELECT Id FROM Companies WHERE Name='Staff managed company'")[0][0]
            assert staff.form('/Clients/Delete', {'id': managed_company}, f'/Clients/Delete/{managed_company}')[0] == 200
            assert not rows('SELECT Id FROM Companies WHERE Id=?', (managed_company,))
            assert alice.form('/Clients/Save', {'Id': 0, 'Name': 'Forbidden', 'Email': 'forbidden@example.com'}, '/Tickets/Create')[0] == 403
            assert alice.request('/Account/Create', token=alice.token())[0] == 403
            assert alice.request('/Users', token=alice.token())[0] == 403
            assert alice.request('/Clients', token=alice.token())[0] == 403
            client_form = alice.request('/Tickets/Create?clientId=' + str(beta))[1]
            assert 'id="accountCompany"' in client_form and 'value="Company Alpha" readonly' in client_form
            assert 'id="accountRequester"' in client_form and 'value="Alice" readonly' in client_form
            assert 'name="ClientId"' not in client_form and 'name="RequesterId"' not in client_form
            internal_form = staff.request('/Tickets/Create')[1]
            assert 'name="ClientId"' in internal_form and 'name="RequesterId"' in internal_form
            invalid = alice.form('/Tickets/Save', {'Id': 0, 'Title': '', 'Description': 'Missing title', 'Priority': 1}, '/Tickets/Create')
            assert 'validation-summary-errors' in invalid[1] and 'value="Company Alpha" readonly' in invalid[1]
            automatic = alice.form('/Tickets/Save', {'Id': 0, 'Title': 'Automatic account ticket', 'Description': 'No company or requester submitted', 'Priority': 1}, '/Tickets/Create')
            assert automatic[0] == 200
            assert rows("SELECT ClientId,RequesterId FROM Tickets WHERE Title='Automatic account ticket'")[0] == [alpha, person_id]
            # Client-provided tenant, owner, assignee, and status are ignored.
            fields = {'Id': 0, 'Title': 'Alice private ticket', 'Description': 'Client request', 'Priority': 1,
                      'ClientId': beta, 'RequesterId': other_id, 'AssigneeId': staff_id, 'Status': 3}
            assert alice.form('/Tickets/Save', fields, '/Tickets/Create')[0] == 200
            ticket = rows("SELECT Id,ClientId,RequesterId,AssigneeId,Status FROM Tickets WHERE Title='Alice private ticket'")[0]
            ticket_id = ticket[0]
            assert ticket[1:] == [alpha, person_id, None, 0], ticket
            assert 'Alice private ticket' not in bob.request('/Tickets')[1]
            assert 'Alice private ticket' not in bob.request('/')[1]
            for route in [f'/Tickets/Details/{ticket_id}', f'/Tickets/Edit/{ticket_id}']:
                assert bob.request(route)[0] == 404
            assert bob.form('/Tickets/Comment', {'id': ticket_id, 'body': 'Forbidden'}, '/Tickets/Create')[0] == 404
            assert bob.form('/Tickets/Save', {**fields, 'Id': ticket_id}, '/Tickets/Create')[0] == 404
            assert alice.form('/Tickets/Delete', {'id': ticket_id}, '/Tickets/Create')[0] == 403
            assert alice.form('/Tickets/Comment', {'id': ticket_id, 'body': 'Own comment'}, f'/Tickets/Details/{ticket_id}')[0] == 200
            assert staff.request(f'/Tickets/Details/{ticket_id}')[0] == 404
            assert 'Alice private ticket' not in staff.request('/Tickets')[1]
            assert admin.form('/Tickets/Save', {'Id': ticket_id, 'Title': 'Alice private ticket', 'Description': 'Updated by admin',
                'Priority': 1, 'ClientId': alpha, 'RequesterId': person_id, 'AssigneeId': staff_id, 'Status': 1}, f'/Tickets/Edit/{ticket_id}')[0] == 200
            assert rows('SELECT Status,AssigneeId FROM Tickets WHERE Id=?', (ticket_id,))[0] == [1, staff_id]
            # Company projects: scoped choices, automatic single selection, validation and immutable company.
            # Both internal roles manage projects; client users cannot invoke either action.
            staff.form('/Clients/AddProject', {'clientId': alpha, 'name': 'Staff managed'}, f'/Clients/Details/{alpha}')
            staff_project = rows("SELECT Id FROM Projects WHERE ClientId=? AND Name='Staff managed'", (alpha,))[0][0]
            assert 'Remove project' in staff.request(f'/Clients/Details/{alpha}')[1]
            assert alice.form('/Clients/AddProject', {'clientId': alpha, 'name': 'Forbidden'}, '/Tickets/Create')[0] == 403
            assert alice.form('/Clients/RemoveProject', {'id': staff_project}, '/Tickets/Create')[0] == 403
            staff.form('/Clients/RemoveProject', {'id': staff_project}, f'/Clients/Details/{alpha}')
            assert not rows('SELECT Id FROM Projects WHERE Id=?', (staff_project,))
            admin.form('/Clients/AddProject', {'clientId': alpha, 'name': 'Vendor Portal'}, f'/Clients/Details/{alpha}')
            admin.form('/Clients/AddProject', {'clientId': beta, 'name': 'Expense Management'}, f'/Clients/Details/{beta}')
            project_id = rows('SELECT Id FROM Projects WHERE ClientId=?', (alpha,))[0][0]
            other_project = rows('SELECT Id FROM Projects WHERE ClientId=?', (beta,))[0][0]
            assert 'Vendor Portal' in alice.request('/Tickets/Create')[1]
            assert 'Expense Management' not in alice.request('/Tickets/Create')[1]
            alice.form('/Tickets/Save', {**fields, 'Title': 'Single project ticket'}, '/Tickets/Create')
            assert rows("SELECT ProjectId FROM Tickets WHERE Title='Single project ticket'")[0][0] == project_id
            alice.form('/Tickets/Save', {**fields, 'Title': 'Wrong project', 'ProjectId': other_project}, '/Tickets/Create')
            assert not rows("SELECT Id FROM Tickets WHERE Title='Wrong project'")
            admin.form('/Clients/AddProject', {'clientId': alpha, 'name': 'P2P'}, f'/Clients/Details/{alpha}')
            alice.form('/Tickets/Save', {**fields, 'Title': 'Missing project'}, '/Tickets/Create')
            assert not rows("SELECT Id FROM Tickets WHERE Title='Missing project'")
            assert 'P2P' in alice.request('/Tickets/Create')[1]
            # Admin maps staff to multiple company projects; another staff member has a different set.
            p2p = rows("SELECT Id FROM Projects WHERE ClientId=? AND Name='P2P'", (alpha,))[0][0]
            admin.form('/Tickets/Save', {**fields, 'Id': ticket_id, 'ClientId': alpha, 'RequesterId': person_id, 'ProjectId': project_id, 'Status': 1}, f'/Tickets/Edit/{ticket_id}')
            alice.form('/Tickets/Save', {**fields, 'Title': 'P2P only ticket', 'ProjectId': p2p}, '/Tickets/Create')
            p2p_ticket = rows("SELECT Id FROM Tickets WHERE Title='P2P only ticket'")[0][0]
            bob.form('/Tickets/Save', {'Id': 0, 'Title': 'Beta expense ticket', 'Description': 'Other company', 'Priority': 1}, '/Tickets/Create')
            expense_ticket = rows("SELECT Id FROM Tickets WHERE Title='Beta expense ticket'")[0][0]
            second_staff = rows("SELECT DeskUserId FROM InternalAccounts WHERE Email='pending@auth.example'")[0][0]
            assert staff.request(f'/Users/Projects/{staff_id}', token=staff.token())[0] == 403
            assert staff.form(f'/Users/Projects/{staff_id}', {'SelectedProjectIds': project_id}, '/Tickets/Create')[0] == 403
            assert alice.form(f'/Users/Projects/{staff_id}', {'SelectedProjectIds': project_id}, '/Tickets/Create')[0] == 403
            admin.form(f'/Users/Projects/{staff_id}', {'SelectedProjectIds': [project_id, other_project]}, f'/Users/Projects/{staff_id}')
            assert rows('SELECT COUNT(*) FROM StaffProjects WHERE StaffId=?', (staff_id,))[0][0] == 2
            admin.form(f'/Users/Projects/{second_staff}', {'SelectedProjectIds': p2p}, f'/Users/Projects/{second_staff}')
            for route in ['/Tickets', '/', f'/Clients/Details/{alpha}']:
                body = staff.request(route)[1]
                assert 'Alice private ticket' in body
                assert 'P2P only ticket' not in body
            assert 'Beta expense ticket' in staff.request('/Tickets')[1]
            assert 'Automatic account ticket' not in staff.request('/Tickets')[1]
            assert staff.request(f'/Tickets/Details/{expense_ticket}')[0] == 200
            assert pending.request(f'/Tickets/Details/{p2p_ticket}')[0] == 200
            assert pending.request(f'/Tickets/Details/{ticket_id}')[0] == 404
            for route in [f'/Tickets/Details/{p2p_ticket}', f'/Tickets/Edit/{p2p_ticket}', f'/Tickets/Delete/{p2p_ticket}']:
                assert staff.request(route)[0] == 404
            assert staff.form('/Tickets/Comment', {'id': p2p_ticket, 'body': 'Forbidden'}, '/Tickets/Create')[0] == 404
            assert staff.form('/Tickets/Delete', {'id': p2p_ticket}, '/Tickets/Create')[0] == 404
            assert staff.form('/Tickets/Save', {**fields, 'Id': p2p_ticket, 'ProjectId': p2p}, '/Tickets/Create')[0] == 404
            staff.form('/Tickets/Save', {**fields, 'Id': ticket_id, 'ClientId': alpha, 'RequesterId': person_id, 'ProjectId': p2p}, f'/Tickets/Edit/{ticket_id}')
            assert rows('SELECT ProjectId FROM Tickets WHERE Id=?', (ticket_id,))[0][0] == project_id
            staff.form('/Tickets/Save', {**fields, 'Title': 'Staff forged project', 'ClientId': alpha, 'RequesterId': person_id, 'ProjectId': p2p}, '/Tickets/Create')
            assert not rows("SELECT Id FROM Tickets WHERE Title='Staff forged project'")
            assert 'P2P' not in staff.request('/Tickets/Create')[1]
            admin.form(f'/Users/Projects/{staff_id}', {'SelectedProjectIds': other_project}, f'/Users/Projects/{staff_id}')
            assert staff.request(f'/Tickets/Details/{ticket_id}')[0] == 404
            assert staff.request(f'/Tickets/Details/{expense_ticket}')[0] == 200
            assert 'Company Alpha' not in staff.request('/Tickets/Create')[1]
            admin.form(f'/Users/Projects/{staff_id}', {}, f'/Users/Projects/{staff_id}')
            assert not rows('SELECT ProjectId FROM StaffProjects WHERE StaffId=?', (staff_id,))
            assert staff.request(f'/Tickets/Details/{expense_ticket}')[0] == 404
            assert 'Beta expense ticket' not in staff.request('/Tickets')[1]
            for id_to_check in [ticket_id, p2p_ticket, expense_ticket]:
                assert admin.request(f'/Tickets/Details/{id_to_check}')[0] == 200
            admin.form(f'/Users/Projects/{staff_id}', {'SelectedProjectIds': [project_id, other_project]}, f'/Users/Projects/{staff_id}')
            staff.form('/Tickets/Save', {**fields, 'Id': ticket_id, 'ClientId': beta, 'RequesterId': person_id, 'ProjectId': project_id}, f'/Tickets/Edit/{ticket_id}')
            assert rows('SELECT ClientId,ProjectId FROM Tickets WHERE Id=?', (ticket_id,))[0] == [alpha, project_id]
            admin.form('/Clients/RemoveProject', {'id': project_id}, f'/Clients/Details/{alpha}')
            assert rows('SELECT Id FROM Projects WHERE Id=?', (project_id,))
            # Only Admin manages staff activity; deactivation blocks fresh logins and existing JWTs.
            assert pending.form('/Users/SetActive', {'id': staff_id, 'isActive': False}, '/Tickets/Create')[0] == 403
            assert alice.form('/Users/SetActive', {'id': staff_id, 'isActive': False}, '/Tickets/Create')[0] == 403
            previous_staff_token = staff.token()
            deactivated = change_activity_from_rendered_form(admin, staff_id, False)
            assert deactivated[0] == 200 and 'Staff deactivated.' in deactivated[1]
            assert rows('SELECT IsActive FROM AitsStaff WHERE Id=?', (staff_id,))[0][0] is False
            assert anonymous.request('/Account/Me', token=previous_staff_token)[0] == 401
            assert anonymous.request('/Tickets', token=previous_staff_token)[0] == 401
            inactive_login = Browser(base)
            inactive_login.form('/Account/Login', {'Email': staff_email, 'Password': password, 'Area': 1}, '/Account/AitsLogin')
            assert inactive_login.request('/Account/Me')[0] == 401
            admin.form('/Tickets/Save', {'Id': 0, 'Title': 'Inactive assignee rejected', 'Description': 'Validation', 'ClientId': alpha, 'ProjectId': project_id, 'RequesterId': person_id, 'AssigneeId': staff_id, 'Priority': 1}, '/Tickets/Create')
            assert not rows("SELECT Id FROM Tickets WHERE Title='Inactive assignee rejected'")
            activated = change_activity_from_rendered_form(admin, staff_id, True)
            assert activated[0] == 200 and 'Staff activated.' in activated[1]
            assert admin.form('/Users/SetActive', {'id': staff_id}, '/Users/AitsStaff')[0] == 400
            assert rows('SELECT IsActive FROM AitsStaff WHERE Id=?', (staff_id,))[0][0] is True
            assert anonymous.request('/Account/Me', token=previous_staff_token)[0] == 401
            assert login(staff, staff_email, 1)['role'] == 'AitsUser'
            assert rows('SELECT COUNT(*) FROM StaffProjects WHERE StaffId=?', (staff_id,))[0][0] == 2
            assert 'Your assigned projects' in staff.request('/')[1]
            assert 'Beta expense ticket' not in staff.request(f'/Tickets?clientId={alpha}&projectId={project_id}')[1]
            assert 'AITS Support' in alice.request('/Tickets')[1]
            valid_token = alice.token()
            assert anonymous.request('/Account/Me', token=valid_token)[0] == 200
            # Legacy role/portal fields cannot change the authenticated role.
            spoofed = Browser(base)
            spoofed.form('/Account/Login', {'Email': 'Alice@auth.example', 'Password': password, 'Portal': 0, 'Role': 0}, '/Account/Login')
            assert json.loads(spoofed.request('/Account/Me')[1])['role'] == 'ClientUser'
            # Correctly signed but expired/wrong-issuer/wrong-audience JWTs must be rejected.
            key = (Path(folder) / 'App_Data/jwt-signing.key').read_bytes()
            header, encoded, _ = valid_token.split('.')
            payload = json.loads(base64.urlsafe_b64decode(encoded + '=' * (-len(encoded) % 4)))
            for changes in [{'exp': int(time.time()) - 120, 'nbf': int(time.time()) - 3600}, {'iss': 'wrong'}, {'aud': 'wrong'}]:
                altered = base64.urlsafe_b64encode(json.dumps({**payload, **changes}).encode()).decode().rstrip('=')
                value = header + '.' + altered
                signature = base64.urlsafe_b64encode(hmac.new(key, value.encode(), hashlib.sha256).digest()).decode().rstrip('=')
                assert anonymous.request('/Account/Me', token=value + '.' + signature)[0] == 401
            assert anonymous.request('/Account/Me', token=header + '.' + encoded + '.' + 'A' * 43)[0] == 401
            assert alice.request('/Account/Logout', {})[0] == 400
            alice.form('/Account/Logout', {}, '/')
            assert anonymous.request('/Account/Me', token=valid_token)[0] == 401
            # Editing a linked user's company/type invalidates previously issued claims.
            staff_token = staff.token()
            admin.form('/Users/Save', {'Id': staff_id, 'Name': 'Renamed staff', 'Email': staff_email, 'Department': 'Support', 'UserType': 1}, f'/Users/Edit/{staff_id}')
            assert anonymous.request('/Account/Me', token=staff_token)[0] == 401
            for _ in range(5):
                failed = anonymous.form('/Account/Login', {'Email': 'Bob@auth.example', 'Password': 'incorrect-password'}, '/Account/Login')
                assert 'Unable to sign in' in failed[1]
            blocked = anonymous.form('/Account/Login', {'Email': 'Bob@auth.example', 'Password': password}, '/Account/Login')
            # The shared IP limiter may reject this extra attempt after the five failures.
            assert blocked[0] == 429 or 'Unable to sign in' in blocked[1]
            assert rows("SELECT COUNT(*) FROM ClientAccounts WHERE Email='Bob@auth.example' AND LockoutUntil > GETUTCDATE()")[0][0] == 1
            assert not rows('DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS')
            print('PASS: first-admin setup, AITS/client login separation, both signup pages, pending staff approval, client signup, password hashing, HttpOnly JWT, equal Admin/AITS management permissions, multi-company project assignments, staff activity, staff ticket isolation and immediate revocation, tenant/owner isolation, claim tampering, issuer/audience/expiry/signature checks, CSRF, logout and profile-change revocation.')
        except Exception:
            log.flush()
            print((Path(folder) / 'server.log').read_text(encoding='utf-8')[-6000:])
            raise
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=15)
            log.close()
            assert re.fullmatch(r'AitsHelpDeskSmoke_[0-9a-f]{32}', database)
            query(master, f"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END")


if __name__ == '__main__':
    check()
