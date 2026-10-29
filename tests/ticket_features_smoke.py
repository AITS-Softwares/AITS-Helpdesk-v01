"""Disposable-database checks for optional photos and Admin-assisted password recovery."""
import json
import os
from pathlib import Path
import re
import secrets
import socket
import struct
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zlib
from auth_smoke import Browser
from sqlserver import query

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'Help Desk/bin/Debug/net8.0/Help Desk.dll'


def png_pixel(red, green, blue):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 1, 1, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(bytes([0, red, green, blue]))) + chunk(b'IEND', b'')


def raw_request(browser, path, data=None, headers=None):
    try:
        response = browser.client.open(urllib.request.Request(browser.base + path, data, headers or {}), timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read(), response.headers


def upload(browser, fields, source, data, filename='proof.png', content_type='image/png'):
    status, page, _ = browser.request(source)
    assert status == 200
    token = re.search(r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', page)[1]
    fields = {**fields, '__RequestVerificationToken': token}
    boundary = 'AitsPhoto' + uuid.uuid4().hex
    parts = []
    for key, value in fields.items():
        parts += [f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n'.encode()]
    parts += [f'--{boundary}\r\nContent-Disposition: form-data; name="photo"; filename="{filename}"\r\nContent-Type: {content_type}\r\n\r\n'.encode(), data, f'\r\n--{boundary}--\r\n'.encode()]
    return raw_request(browser, '/Tickets/Save', b''.join(parts), {'Content-Type': 'multipart/form-data; boundary=' + boundary})


def check():
    configured = json.loads((ROOT / 'Help Desk/appsettings.json').read_text())['ConnectionStrings']['HelpDesk']
    database = 'AitsHelpDeskSmoke_' + uuid.uuid4().hex
    connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=' + database, configured)
    master = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=master', configured)
    env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ConnectionStrings__HelpDesk': connection}
    process = None
    with tempfile.TemporaryDirectory(prefix='aits-features-') as folder:
        log_path = Path(folder) / 'server.log'
        with log_path.open('w', encoding='utf-8') as log:
            def rows(sql, args=()):
                return query(connection, sql, args)

            try:
                command = ['dotnet', str(DLL), '--contentRoot', folder]
                seeded = subprocess.run(command + ['--DemoData:Seed=true'], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90, creationflags=subprocess.CREATE_NO_WINDOW)
                assert seeded.returncode == 0
                credentials = {email: password for email, password in re.findall(r'^\| ([^|]+) \| ([^|]+) \| (?:AITS sign in|Client sign in) \|$', (Path(folder) / 'App_Data/demo-credentials.md').read_text(), re.MULTILINE)}
                with socket.socket() as reservation:
                    reservation.bind(('127.0.0.1', 0))
                    port = reservation.getsockname()[1]
                base = f'http://127.0.0.1:{port}'
                process = subprocess.Popen(command + ['--urls', base], env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
                anonymous, admin, alice, bob, staff, other_staff = [Browser(base) for _ in range(6)]
                for _ in range(100):
                    assert process.poll() is None
                    try:
                        if anonymous.request('/Account/Setup')[0] == 200:
                            break
                    except OSError:
                        time.sleep(0.2)
                else:
                    raise AssertionError('Server startup timed out')
                password = secrets.token_urlsafe(6)
                admin.form('/Account/Setup', {'Name': 'Feature Admin', 'Email': 'admin@feature.example', 'Password': password, 'ConfirmPassword': password}, '/Account/Setup')

                def login(browser, email, secret, internal=False):
                    browser.form('/Account/Login', {'Email': email, 'Password': secret, 'Area': 1 if internal else 0}, '/Account/AitsLogin' if internal else '/Account/Login')
                    return browser.request('/Account/Me')[0]

                assert login(admin, 'admin@feature.example', password, True) == 200
                assert login(alice, '1@gmail.com', credentials['1@gmail.com']) == 200
                assert login(bob, 'demo.client2.user1@example.com', credentials['demo.client2.user1@example.com']) == 200
                assert login(staff, 'demo.arun@aitsind.com', credentials['demo.arun@aitsind.com'], True) == 200
                assert login(other_staff, 'demo.priya@aitsind.com', credentials['demo.priya@aitsind.com'], True) == 200
                client_id, company_id = rows("SELECT Id,ClientId FROM ClientUsers WHERE Email='1@gmail.com'")[0]
                project_id = rows("SELECT Id FROM Projects WHERE ClientId=? AND Name='Vendor Portal'", (company_id,))[0][0]
                fields = {'Id': 0, 'Title': 'Feature photo ticket', 'Description': 'Optional screenshot', 'ProjectId': project_id, 'Priority': 3}
                form = alice.request('/Tickets/Create')[1]
                file_input = re.search(r'<input[^>]*id="photo"[^>]*>', form)[0]
                assert 'required' not in file_input and 'multipart/form-data' in form
                photo = png_pixel(98, 85, 217)
                result = upload(alice, fields, '/Tickets/Create', photo, content_type='text/plain')
                assert result[0] == 200
                ticket_id = rows("SELECT Id FROM Tickets WHERE Title='Feature photo ticket'")[0][0]
                assert rows('SELECT ClientId,RequesterId,Priority FROM Tickets WHERE Id=?', (ticket_id,))[0] == [company_id, client_id, 3]
                assert rows('SELECT ContentType,DATALENGTH(Data) FROM TicketPhotos WHERE TicketId=?', (ticket_id,))[0] == ['image/png', len(photo)]
                for browser in [alice, staff, admin]:
                    code, body, headers = raw_request(browser, f'/Tickets/Photo/{ticket_id}')
                    assert code == 200 and body == photo
                    assert headers['Content-Type'] == 'image/png' and headers['X-Content-Type-Options'] == 'nosniff'
                for browser in [bob, other_staff]:
                    assert raw_request(browser, f'/Tickets/Photo/{ticket_id}')[0] == 404
                assert anonymous.request(f'/Tickets/Photo/{ticket_id}', token='invalid')[0] == 401
                assert 'Attached photo' in alice.request(f'/Tickets/Details/{ticket_id}')[1]
                assert 'priority Urgent' in alice.request('/Tickets')[1]
                alice.form('/Tickets/Save', {**fields, 'Title': 'Feature no photo'}, '/Tickets/Create')
                no_photo_id = rows("SELECT Id FROM Tickets WHERE Title='Feature no photo'")[0][0]
                assert not rows('SELECT TicketId FROM TicketPhotos WHERE TicketId=?', (no_photo_id,))
                assert upload(alice, {**fields, 'Title': 'Feature empty photo field'}, '/Tickets/Create', b'', filename='')[0] == 200
                empty_photo_id = rows("SELECT Id FROM Tickets WHERE Title='Feature empty photo field'")[0][0]
                assert not rows('SELECT TicketId FROM TicketPhotos WHERE TicketId=?', (empty_photo_id,))
                invalid = upload(alice, {**fields, 'Title': 'Feature invalid photo'}, '/Tickets/Create', b'<svg onload="alert(1)"></svg>')
                assert invalid[0] == 200 and b'Upload a PNG, JPG, or WebP photo.' in invalid[1]
                assert not rows("SELECT Id FROM Tickets WHERE Title='Feature invalid photo'")
                oversized = upload(alice, {**fields, 'Title': 'Feature too large'}, '/Tickets/Create', photo + b'0' * (5 * 1024 * 1024))
                assert oversized[0] == 200 and b'5 MB or smaller' in oversized[1]
                assert not rows("SELECT Id FROM Tickets WHERE Title='Feature too large'")
                edit = {**fields, 'Id': ticket_id}
                alice.form('/Tickets/Save', edit, f'/Tickets/Edit/{ticket_id}')
                assert raw_request(alice, f'/Tickets/Photo/{ticket_id}')[1] == photo
                replacement = png_pixel(25, 120, 80)
                assert upload(alice, edit, f'/Tickets/Edit/{ticket_id}', replacement)[0] == 200
                assert raw_request(alice, f'/Tickets/Photo/{ticket_id}')[1] == replacement
                alice.form('/Tickets/Save', {**edit, 'removePhoto': 'true'}, f'/Tickets/Edit/{ticket_id}')
                assert raw_request(alice, f'/Tickets/Photo/{ticket_id}')[0] == 404
                assert upload(alice, edit, f'/Tickets/Edit/{ticket_id}', photo)[0] == 200
                admin.form('/Tickets/Delete', {'id': ticket_id}, f'/Tickets/Delete/{ticket_id}')
                assert not rows('SELECT TicketId FROM TicketPhotos WHERE TicketId=?', (ticket_id,))

                # Requests never expose account existence or change a password before Admin acts.
                alice_token = alice.token()
                assert 'Forgot password?' in anonymous.request('/Account/Login')[1]
                assert 'Forgot password?' in anonymous.request('/Account/AitsLogin')[1]
                assert anonymous.request('/Account/ForgotPassword', {'Email': '1@gmail.com'})[0] == 400
                for email in ['1@gmail.com', '1@GMAIL.COM', 'missing@feature.example']:
                    response = anonymous.form('/Account/ForgotPassword', {'Email': email, 'LoginArea': 0}, '/Account/ForgotPassword')
                    assert response[0] == 200 and 'If an account matches this email' in response[1]
                assert rows('SELECT COUNT(*) FROM PasswordResetRequests WHERE CompletedAt IS NULL')[0][0] == 1
                assert anonymous.request('/Account/Me', token=alice_token)[0] == 200
                account_id = rows("SELECT Id FROM ClientAccounts WHERE Email='1@gmail.com'")[0][0]
                reset_path = f'/Account/ResetPassword/{account_id}'
                for browser in [alice, staff]:
                    assert browser.request(reset_path, token=browser.token())[0] == 403
                    assert browser.form(reset_path, {'Password': password, 'ConfirmPassword': password}, '/Tickets/Create')[0] == 403
                assert '1@gmail.com' in admin.request('/Account')[1]
                assert admin.request(reset_path)[0] == 200
                invalid = admin.form(reset_path, {'Password': password[:7], 'ConfirmPassword': password[:7]}, reset_path)
                assert 'validation-summary-errors' in invalid[1]
                assert anonymous.request('/Account/Me', token=alice_token)[0] == 200
                replacement_password = secrets.token_urlsafe(6)
                rows('UPDATE ClientAccounts SET FailedAttempts=4,LockoutUntil=DATEADD(minute,15,GETUTCDATE()) WHERE Id=?', (account_id,))
                assert admin.form(reset_path, {'Password': replacement_password, 'ConfirmPassword': replacement_password, 'Role': 0}, reset_path)[0] == 200
                assert rows('SELECT Role,FailedAttempts,LockoutUntil FROM ClientAccounts WHERE Id=?', (account_id,))[0] == [2, 0, None]
                assert rows('SELECT COUNT(*) FROM PasswordResetRequests WHERE CompletedAt IS NOT NULL AND CompletedByAccountId IS NOT NULL')[0][0] == 1
                assert anonymous.request('/Account/Me', token=alice_token)[0] == 401
                assert login(alice, '1@gmail.com', credentials['1@gmail.com']) == 401
                assert login(alice, '1@gmail.com', replacement_password) == 200

                # A password reset must not activate inactive staff or alter their approval/role.
                inactive_staff_id = rows("SELECT Id FROM AitsStaff WHERE Email='demo.priya@aitsind.com'")[0][0]
                staff_account_id = rows('SELECT Id FROM InternalAccounts WHERE DeskUserId=?', (inactive_staff_id,))[0][0]
                admin.form('/Users/SetActive', {'id': inactive_staff_id, 'isActive': False}, '/Users/AitsStaff')
                staff_reset = f'/Account/ResetPassword/{staff_account_id}'
                assert admin.form(staff_reset, {'Password': replacement_password, 'ConfirmPassword': replacement_password}, staff_reset)[0] == 200
                assert rows('SELECT IsActive FROM AitsStaff WHERE Id=?', (inactive_staff_id,))[0][0] is False
                assert login(other_staff, 'demo.priya@aitsind.com', replacement_password, True) == 401
                admin.form('/Users/SetActive', {'id': inactive_staff_id, 'isActive': True}, '/Users/AitsStaff')
                assert login(other_staff, 'demo.priya@aitsind.com', replacement_password, True) == 200
                assert not rows('DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS')
                print('PASS: optional photos, validated image type/size, private image access, replacement/removal/cascade, priority labels, generic/deduplicated recovery requests, Admin-only password resets, minimum length, session revocation, lockout clearing, and preserved staff activity.')
            except Exception:
                log.flush()
                print(log_path.read_text(encoding='utf-8')[-6500:])
                raise
            finally:
                if process is not None and process.poll() is None:
                    process.terminate()
                    process.wait(timeout=15)
                assert re.fullmatch(r'AitsHelpDeskSmoke_[0-9a-f]{32}', database)
                query(master, f"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END")


if __name__ == '__main__':
    check()
