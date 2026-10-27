"""Run after dotnet build. Tests use a disposable SQL Server database and copied legacy data."""
from contextlib import closing
import http.cookiejar
import json
import os
from pathlib import Path
import re
import shutil
import socket
import secrets
import sqlite3
import uuid
from sqlserver import query
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'Help Desk/bin/Debug/net8.0/Help Desk.dll'


def check():
    with tempfile.TemporaryDirectory(prefix='aits-ef-test-') as folder:
        content = Path(folder)
        (content / 'App_Data').mkdir()
        source = ROOT / ('__no_legacy_data__' if '--fresh' in sys.argv else 'Help Desk/App_Data/helpdesk.json')
        if source.exists():
            shutil.copy2(source, content / 'App_Data/helpdesk.json')
        sqlite_source = ROOT / 'Help Desk/App_Data/helpdesk.db'
        if '--fresh' not in sys.argv and sqlite_source.exists():
            with closing(sqlite3.connect('file:' + sqlite_source.as_posix() + '?mode=ro', uri=True)) as old:
                with closing(sqlite3.connect(content / 'App_Data/helpdesk.db')) as snapshot:
                    old.backup(snapshot)
        database_name = 'AitsHelpDeskSmoke_' + uuid.uuid4().hex
        configured = json.loads((ROOT / 'Help Desk/appsettings.json').read_text())['ConnectionStrings']['HelpDesk']
        connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=' + database_name, configured)
        master_connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=master', configured)
        with socket.socket() as port_socket:
            port_socket.bind(('127.0.0.1', 0))
            port = port_socket.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        log = open(content / 'server.log', 'w', encoding='utf-8')
        process = None
        bootstrapped = False
        admin_password = secrets.token_urlsafe(24)

        def get(path):
            return client.open(base + path, timeout=10).read().decode()

        def post(path, fields, form_path):
            form = get(form_path)
            token = re.search(r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', form)[1]
            fields = {**fields, '__RequestVerificationToken': token}
            return client.open(base + path, urllib.parse.urlencode(fields).encode(), timeout=10).read().decode()

        def rows(sql, args=()):
            return query(connection, sql, args)

        def start():
            nonlocal process, bootstrapped
            env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development',
                   'ConnectionStrings__HelpDesk': connection}
            process = subprocess.Popen(['dotnet', str(DLL), '--urls', base, '--contentRoot', str(content)],
                                       stdout=log, stderr=subprocess.STDOUT, env=env,
                                       creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            for _ in range(100):
                if process.poll() is not None:
                    raise AssertionError('Server failed to start')
                try:
                    get('/Account/Login')
                    if not bootstrapped:
                        post('/Account/Setup', {'Name': 'Test Admin', 'Email': 'admin@smoke.example', 'Password': admin_password, 'ConfirmPassword': admin_password}, '/Account/Setup')
                        bootstrapped = True
                    post('/Account/Login', {'Email': 'admin@smoke.example', 'Password': admin_password, 'Area': 1}, '/Account/Login')
                    assert json.loads(get('/Account/Me'))['role'] == 'Admin'
                    return
                except (OSError, urllib.error.URLError):
                    time.sleep(0.2)
            raise AssertionError('Server startup timed out')

        def stop():
            if process and process.poll() is None:
                process.terminate()
                process.wait(timeout=15)

        try:
            start()
            assert rows('SELECT COUNT(*) FROM __EFMigrationsHistory')[0][0] == 3
            if source.exists() and not (content / 'App_Data/helpdesk.db').exists():
                original = json.loads(source.read_text(encoding='utf-8-sig'))
                assert rows('SELECT COUNT(*) FROM Users')[0][0] == len(original['Users'])
                assert rows('SELECT COUNT(*) FROM Tickets')[0][0] == len(original['Tickets'])
                assert (content / 'App_Data/helpdesk.json').read_bytes() == source.read_bytes()
            if (content / 'App_Data/helpdesk.db').exists():
                with closing(sqlite3.connect(content / 'App_Data/helpdesk.db')) as old:
                    for table in ['Companies', 'Users', 'Tickets', 'Comments']:
                        assert rows('SELECT COUNT(*) FROM ' + table)[0][0] == old.execute('SELECT COUNT(*) FROM ' + table).fetchone()[0]
            for path in ['/Clients', '/Clients/Create', '/Users', '/Users/Create', '/Tickets', '/Tickets/Create']:
                assert '<!DOCTYPE html>' in get(path)
            assert 'id="ClientId"' in get('/Users/Create')
            post('/Clients/Save', {'Id': 0, 'Name': 'Smoke Company', 'Email': 'company@example.com'}, '/Clients/Create')
            company_id = rows("SELECT Id FROM Companies WHERE Name='Smoke Company'")[0][0]
            post('/Clients/Save', {'Id': company_id, 'Name': 'Smoke Company Updated', 'Email': 'updated@example.com'}, f'/Clients/Edit/{company_id}')
            assert 'Smoke Company Updated' in get(f'/Clients/Details/{company_id}')
            assert 'Smoke Company Updated' in get('/Users/Create')
            invalid = post('/Clients/Save', {'Id': 0, 'Name': 'smoke company updated', 'Email': 'duplicate@example.com'}, '/Clients/Create')
            assert 'already exists' in invalid
            invalid = post('/Users/Save', {'Id': 0, 'Name': 'Missing company', 'Email': 'missing@example.com', 'Department': 'IT', 'UserType': 0}, '/Users/Create')
            assert 'Select a company' in invalid
            for name in ['One', 'Two']:
                post('/Users/Save', {'Id': 0, 'Name': f'Smoke User {name}', 'Email': f'{name}@smoke.example', 'Department': 'IT', 'UserType': 0, 'ClientId': company_id}, '/Users/Create')
            users = rows('SELECT Id FROM Users WHERE ClientId=? ORDER BY Id', (company_id,))
            assert len(users) == 2
            user_id = users[0][0]
            assert 'Smoke User One' in get(f'/Clients/Details/{company_id}')
            assert 'Smoke User Two' in get(f'/Users?clientId={company_id}')
            invalid = post('/Users/Save', {'Id': 0, 'Name': 'Duplicate', 'Email': 'ONE@SMOKE.EXAMPLE', 'Department': 'IT', 'UserType': 0, 'ClientId': company_id}, '/Users/Create')
            assert 'already in use' in invalid
            post('/Users/Save', {'Id': user_id, 'Name': 'Smoke User Edited', 'Email': 'One@smoke.example', 'Department': 'Finance', 'UserType': 0, 'ClientId': company_id}, f'/Users/Edit/{user_id}')
            assert rows('SELECT Department FROM Users WHERE Id=?', (user_id,))[0][0] == 'Finance'
            staff_id = rows('SELECT TOP(1) Id FROM Users WHERE UserType=1')[0][0]
            ticket = {'Id': 0, 'Title': 'Smoke ticket', 'Description': 'EF persistence', 'ClientId': company_id, 'RequesterId': user_id, 'AssigneeId': staff_id, 'Status': 0, 'Priority': 3}
            post('/Tickets/Save', ticket, '/Tickets/Create')
            ticket_id = rows("SELECT Id FROM Tickets WHERE Title='Smoke ticket'")[0][0]
            assert 'Smoke ticket' in get(f'/Tickets?clientId={company_id}&search=SMOKE&priority=3&status=0')
            invalid = post('/Tickets/Save', {**ticket, 'ClientId': 1 if company_id != 1 else 999999}, '/Tickets/Create')
            assert 'Select a requester belonging' in invalid
            invalid = post('/Tickets/Save', {**ticket, 'AssigneeId': user_id}, '/Tickets/Create')
            assert 'Select an AITS' in invalid
            invalid = post('/Tickets/Save', {**ticket, 'Status': 99}, '/Tickets/Create')
            assert 'validation-summary-errors' in invalid
            post('/Tickets/Save', {**ticket, 'Id': ticket_id, 'Status': 2}, f'/Tickets/Edit/{ticket_id}')
            post('/Tickets/Comment', {'id': ticket_id, 'body': 'Persisted comment'}, f'/Tickets/Details/{ticket_id}')
            assert 'Persisted comment' in get(f'/Tickets/Details/{ticket_id}')
            assert 'has users or tickets' in post('/Clients/Delete', {'id': company_id}, f'/Clients/Delete/{company_id}')
            assert 'has tickets' in post('/Users/Delete', {'id': user_id}, f'/Users/Delete/{user_id}')
            try:
                client.open(base + '/Clients/Delete', urllib.parse.urlencode({'id': company_id}).encode())
                raise AssertionError('Missing anti-forgery token accepted')
            except urllib.error.HTTPError as error:
                assert error.code == 400
            stop()
            start()
            assert 'Persisted comment' in get(f'/Tickets/Details/{ticket_id}')
            assert rows('SELECT COUNT(*) FROM Users WHERE ClientId=?', (company_id,))[0][0] == 2
            post('/Tickets/Delete', {'id': ticket_id}, f'/Tickets/Delete/{ticket_id}')
            assert rows('SELECT COUNT(*) FROM Comments WHERE TicketId=?', (ticket_id,))[0][0] == 0
            for user in users:
                post('/Users/Delete', {'id': user[0]}, f'/Users/Delete/{user[0]}')
            post('/Clients/Delete', {'id': company_id}, f'/Clients/Delete/{company_id}')
            assert not rows('SELECT Id FROM Companies WHERE Id=?', (company_id,))
            stop()
            start()
            assert not rows('SELECT Id FROM Companies WHERE Id=?', (company_id,))
            assert not rows('DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS')
            print('PASS: migrations, JSON import, company/user/ticket CRUD, company dropdown, multiple users, duplicates, relationship validation, comments, deletion restrictions/cascade, CSRF, restart persistence.')
        except Exception:
            log.flush()
            print((content / 'server.log').read_text(encoding='utf-8')[-7000:])
            raise
        finally:
            stop()
            log.close()
            assert re.fullmatch(r'AitsHelpDeskSmoke_[0-9a-f]{32}', database_name)
            query(master_connection, f'IF DB_ID(N\'{database_name}\') IS NOT NULL BEGIN ALTER DATABASE [{database_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database_name}]; END')


if __name__ == '__main__':
    check()
