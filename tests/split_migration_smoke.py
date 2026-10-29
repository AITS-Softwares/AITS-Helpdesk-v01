"""Verify populated shared tables can migrate, roll back, and migrate again without loss."""
import base64
import hashlib
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
import uuid
from auth_smoke import Browser
from sqlserver import query

ROOT = Path(__file__).resolve().parents[1]
CONFIGURATION = 'Debug'
DLL = ROOT / 'Help Desk/bin/Debug/net8.0/Help Desk.dll'
OLD_MIGRATION = '20261027064547_AddStaffProjectAssignments'
USER_COLUMNS = 'Id,UserType,ClientId,Name,Email,Department'
ACCOUNT_COLUMNS = 'Id,Name,Email,PasswordHash,Role,IsApproved,DeskUserId,SecurityStamp,FailedAttempts,LockoutUntil,CreatedAt'


def check():
    configured = json.loads((ROOT / 'Help Desk/appsettings.json').read_text())['ConnectionStrings']['HelpDesk']
    database = 'AitsHelpDeskSmoke_' + uuid.uuid4().hex
    connection = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=' + database, configured)
    master = re.sub(r'(?i)(Initial Catalog|Database)=[^;]*', 'Initial Catalog=master', configured)
    env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ConnectionStrings__HelpDesk': connection}
    password = secrets.token_urlsafe(24)
    salt = secrets.token_bytes(16)
    password_hash = base64.b64encode(b'\x01' + struct.pack('>III', 2, 100000, 16) + salt + hashlib.pbkdf2_hmac('sha512', password.encode(), salt, 100000, 32)).decode()
    process = None
    with tempfile.TemporaryDirectory(prefix='aits-split-') as folder:
        log_path = Path(folder) / 'migration.log'
        with log_path.open('w', encoding='utf-8') as log:
            def migrate(target):
                result = subprocess.run(['dotnet', 'ef', 'database', 'update', target, '--project', str(ROOT / 'Help Desk/Help Desk.csproj'), '--context', 'HelpDeskDbContext', '--configuration', CONFIGURATION, '--no-build', '--connection', connection], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)
                assert result.returncode == 0

            def snapshot(split):
                user_sql = f'SELECT {USER_COLUMNS} FROM ClientUsers UNION ALL SELECT {USER_COLUMNS} FROM AitsStaff ORDER BY Id' if split else f'SELECT {USER_COLUMNS} FROM Users ORDER BY Id'
                account_sql = f'SELECT {ACCOUNT_COLUMNS} FROM InternalAccounts UNION ALL SELECT {ACCOUNT_COLUMNS} FROM ClientAccounts ORDER BY Id' if split else f'SELECT {ACCOUNT_COLUMNS} FROM Accounts ORDER BY Id'
                return [query(connection, user_sql), query(connection, account_sql)] + [query(connection, f'SELECT * FROM {table} ORDER BY {order}') for table, order in [('Companies', 'Id'), ('Projects', 'Id'), ('StaffProjects', 'StaffId,ProjectId'), ('Tickets', 'Id'), ('Comments', 'Id')]]

            try:
                migrate(OLD_MIGRATION)
                query(connection, """
                    INSERT Companies(Name,Email) VALUES ('Migration Company','company@migration.example');
                    INSERT Users(UserType,ClientId,Name,Email,Department) VALUES
                        (0,1,'Migration Client','client@migration.example','Finance'),
                        (1,NULL,'Migration Staff','staff@migration.example','IT');
                    INSERT Accounts(Name,Email,PasswordHash,Role,IsApproved,DeskUserId,SecurityStamp,FailedAttempts,CreatedAt) VALUES
                        ('Migration Admin','admin@migration.example',?,0,1,NULL,NEWID(),2,GETUTCDATE()),
                        ('Migration Staff','staff@migration.example',?,1,1,2,NEWID(),0,GETUTCDATE()),
                        ('Migration Client','client@migration.example',?,2,1,1,NEWID(),0,GETUTCDATE());
                    INSERT Projects(ClientId,Name) VALUES (1,'Migration Project');
                    INSERT StaffProjects(StaffId,ProjectId) VALUES (2,1);
                    INSERT Tickets(ClientId,ProjectId,Title,Description,Status,Priority,RequesterId,AssigneeId,CreatedAt,UpdatedAt)
                        VALUES (1,1,'Preserved migration ticket','Existing ticket and comment',1,2,1,2,GETUTCDATE(),GETUTCDATE());
                    INSERT Comments(TicketId,Body,CreatedAt) VALUES (1,'Preserved comment',GETUTCDATE());
                    INSERT DatabaseStates(InitializedAt) VALUES(GETUTCDATE());
                    """, (password_hash, password_hash, password_hash))
                original = snapshot(False)
                migrate('SeparateInternalAndClientStorage')
                assert snapshot(True) == original
                assert query(connection, "SELECT COUNT(*) FROM sys.tables WHERE name IN ('Users','Accounts')")[0][0] == 0
                assert query(connection, "SELECT COUNT(*) FROM sys.tables WHERE name IN ('AitsStaff','ClientUsers','InternalAccounts','ClientAccounts')")[0][0] == 4
                for sql in [
                    "INSERT AitsStaff(UserType,Name,Email,Department) VALUES (1,'Invalid','client@migration.example','IT')",
                    "INSERT InternalAccounts(Name,Email,PasswordHash,Role,IsApproved,SecurityStamp,FailedAttempts,CreatedAt) VALUES ('Invalid','client@migration.example','invalid',0,1,NEWID(),0,GETUTCDATE())",
                    "UPDATE Tickets SET RequesterId=2 WHERE Id=1"
                ]:
                    try:
                        query(connection, sql)
                    except RuntimeError:
                        pass
                    else:
                        raise AssertionError('Cross-group integrity check was bypassed')
                query(connection, "INSERT AitsStaff(UserType,Name,Email,Department) VALUES (1,'New Staff','new.staff@migration.example','IT'); INSERT ClientUsers(UserType,ClientId,Name,Email,Department) VALUES (0,1,'New Client','new.client@migration.example','Finance');")
                ids = query(connection, "SELECT Id FROM AitsStaff WHERE Email='new.staff@migration.example' UNION ALL SELECT Id FROM ClientUsers WHERE Email='new.client@migration.example'")
                assert len({row[0] for row in ids}) == 2 and all(row[0] > 2 for row in ids)
                before_rollback = snapshot(True)
                migrate(OLD_MIGRATION)
                assert snapshot(False) == before_rollback
                migrate('SeparateInternalAndClientStorage')
                assert snapshot(True) == before_rollback
                assert not query(connection, 'DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS')
                with socket.socket() as reservation:
                    reservation.bind(('127.0.0.1', 0))
                    port = reservation.getsockname()[1]
                base = f'http://127.0.0.1:{port}'
                process = subprocess.Popen(['dotnet', str(DLL), '--contentRoot', folder, '--urls', base], env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
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
                for email, area, role in [('admin@migration.example', 1, 'Admin'), ('staff@migration.example', 1, 'AitsUser'), ('client@migration.example', 0, 'ClientUser')]:
                    browser = Browser(base)
                    browser.form('/Account/Login', {'Email': email, 'Password': password, 'Area': area}, '/Account/AitsLogin' if area else '/Account/Login')
                    assert json.loads(browser.request('/Account/Me')[1])['role'] == role
                    assert 'Preserved migration ticket' in browser.request('/Tickets')[1]
                    assert 'Preserved comment' in browser.request('/Tickets/Details/1')[1]
                    if role == 'Admin':
                        assert 'client@migration.example' not in browser.request('/Account')[1]
                        assert 'admin@migration.example' not in browser.request('/Account?clients=true')[1]
                        assert 'client@migration.example' in browser.request('/Account?clients=true')[1]
                print('PASS: populated-table upgrade, exact ID/hash/stamp/ticket/assignment preservation, cross-group constraints, new IDs, lossless rollback/re-upgrade, existing logins, and separate account pages.')
            except Exception:
                log.flush()
                print(log_path.read_text(encoding='utf-8')[-6000:])
                raise
            finally:
                if process is not None and process.poll() is None:
                    process.terminate()
                    process.wait(timeout=15)
                assert re.fullmatch(r'AitsHelpDeskSmoke_[0-9a-f]{32}', database)
                query(master, f"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END")


if __name__ == '__main__':
    check()
