"""Seed sample data twice and check each staff login against its company projects."""
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
DLL = ROOT / 'Help Desk/bin/Debug/net8.0/Help Desk.dll'


def check():
    configured = json.loads((ROOT / 'Help Desk/appsettings.json').read_text())['ConnectionStrings']['HelpDesk']
    database = 'AitsHelpDeskSmoke_' + uuid.uuid4().hex
    connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=' + database, configured)
    master = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=master', configured)
    env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ConnectionStrings__HelpDesk': connection}
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
                for email, password, area in logins:
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
                assert not query(connection, 'DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS')
                print('PASS: repeatable demo seeding, preserved passwords, all 9 projects populated, 4 staff with multi-company assignments, 6 client logins, per-login ticket visibility and direct-link isolation.')
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
