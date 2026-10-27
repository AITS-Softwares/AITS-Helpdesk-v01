"""SQL Server integration checks for authentication and role/data boundaries."""
import base64
import hashlib
import hmac
import http.cookiejar
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
        data = None if fields is None else urllib.parse.urlencode(fields).encode()
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


def check():
    configured = json.loads((ROOT / 'Help Desk/appsettings.json').read_text())['ConnectionStrings']['HelpDesk']
    database = 'AitsHelpDeskSmoke_' + uuid.uuid4().hex
    connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=' + database, configured)
    master = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=master', configured)
    password = secrets.token_urlsafe(24)
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
            assert rows('SELECT COUNT(*) FROM Accounts WHERE Role=0')[0][0] == 1
            assert rows('SELECT PasswordHash FROM Accounts WHERE Role=0')[0][0] != password
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
            person_id = rows("SELECT Id FROM Users WHERE Email='Alice@auth.example'")[0][0]
            other_id = rows("SELECT Id FROM Users WHERE Email='Bob@auth.example'")[0][0]
            staff_id = rows('SELECT TOP(1) Id FROM Users WHERE UserType=1')[0][0]
            assert admin.form('/Account/Create', {'DeskUserId': staff_id, 'Password': password, 'ConfirmPassword': password}, '/Account/Create')[0] == 200
            staff_email = rows('SELECT Email FROM Users WHERE Id=?', (staff_id,))[0][0]
            assert login(staff, staff_email, 1)['role'] == 'AitsUser'
            pending = Browser(base)
            result = pending.form('/Account/AitsSignup', {'Name': 'Self Registered Staff', 'Email': 'pending@auth.example', 'Department': 'IT',
                'Password': password, 'ConfirmPassword': password, 'Role': 0, 'IsApproved': True}, '/Account/AitsSignup')
            assert result[0] == 200 and 'admin must approve' in result[1]
            account = rows("SELECT Id,Role,IsApproved FROM Accounts WHERE Email='pending@auth.example'")[0]
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

            assert staff.request('/Clients/Create', token=staff.token())[0] == 403
            assert staff.form('/Clients/Save', {'Id': 0, 'Name': 'Forbidden', 'Email': 'forbidden@example.com'}, '/Tickets/Create')[0] == 403
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
            assert staff.form('/Tickets/Save', {'Id': ticket_id, 'Title': 'Alice private ticket', 'Description': 'Updated by staff',
                'Priority': 1, 'ClientId': alpha, 'RequesterId': person_id, 'AssigneeId': staff_id, 'Status': 1}, f'/Tickets/Edit/{ticket_id}')[0] == 200
            assert rows('SELECT Status,AssigneeId FROM Tickets WHERE Id=?', (ticket_id,))[0] == [1, staff_id]
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
            assert 'Unable to sign in' in blocked[1]
            assert rows("SELECT COUNT(*) FROM Accounts WHERE Email='Bob@auth.example' AND LockoutUntil > GETUTCDATE()")[0][0] == 1
            assert not rows('DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS')
            print('PASS: first-admin setup, AITS/client login separation, both signup pages, pending staff approval, client signup, password hashing, HttpOnly JWT, admin-only companies/accounts, staff ticket access, tenant/owner isolation, claim tampering, issuer/audience/expiry/signature checks, CSRF, logout and profile-change revocation.')
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
