"""Capture ticket notifications locally; never send test emails to external recipients."""
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
from email.utils import getaddresses
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
    with tempfile.TemporaryDirectory(prefix='aits-email-') as folder:
        content = Path(folder)
        (content / 'App_Data').mkdir()
        settings_path = content / 'App_Data/email.local.json'
        settings = {'Enabled': True, 'Transport': 'Pickup', 'FromAddress': 'notifications@notify.example', 'FromName': 'AITS Helpdesk', 'UserName': 'notifications@notify.example', 'Host': '127.0.0.1', 'Port': 2525, 'PublicBaseUrl': 'https://helpdesk.example', 'PollSeconds': 1, 'MaxAttempts': 1}
        settings_path.write_text(json.dumps({'Email': settings}))
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        command = ['dotnet', str(DLL), '--contentRoot', folder]
        log_path = content / 'server.log'
        with log_path.open('w', encoding='utf-8') as log:
            def rows(sql, args=()):
                return query(connection, sql, args)

            def stop():
                if process is not None and process.poll() is None:
                    process.terminate()
                    process.wait(timeout=15)

            def start():
                nonlocal process
                process = subprocess.Popen(command + ['--urls', base], env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
                browser = Browser(base)
                for _ in range(100):
                    assert process.poll() is None
                    try:
                        if browser.request('/Account/AitsLogin')[0] == 200:
                            return
                    except OSError:
                        time.sleep(0.2)
                raise AssertionError('Server startup timed out')

            def wait_status(ticket_id, status, count):
                for _ in range(30):
                    if rows('SELECT COUNT(*) FROM TicketEmailNotifications WHERE TicketId=? AND Status=?', (ticket_id, status))[0][0] == count:
                        return
                    time.sleep(0.3)
                raise AssertionError('Email delivery did not reach the expected state: ' + str(rows('SELECT Status,Attempts,LastError FROM TicketEmailNotifications WHERE TicketId=?', (ticket_id,))))

            def captured():
                return list((content / 'App_Data/email-pickup').glob('*.eml'))

            try:
                seeded = subprocess.run(command + ['--DemoData:Seed=true'], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90, creationflags=subprocess.CREATE_NO_WINDOW)
                assert seeded.returncode == 0
                start()
                anonymous, admin, creator, staff = [Browser(base) for _ in range(4)]
                password = secrets.token_urlsafe(6)
                admin.form('/Account/Setup', {'Name': 'Email Admin', 'Email': 'admin@notify.example', 'Password': password, 'ConfirmPassword': password}, '/Account/Setup')
                admin.form('/Account/Login', {'Email': 'admin@notify.example', 'Password': password, 'Area': 1}, '/Account/AitsLogin')
                secrets_by_email = dict(re.findall(r'^\| ([^|]+) \| ([^|]+) \| (?:AITS sign in|Client sign in) \|$', (content / 'App_Data/demo-credentials.md').read_text(), re.MULTILINE))
                creator.form('/Account/Login', {'Email': '1@gmail.com', 'Password': secrets_by_email['1@gmail.com'], 'Area': 0}, '/Account/Login')
                staff.form('/Account/Login', {'Email': 'demo.arun@aitsind.com', 'Password': secrets_by_email['demo.arun@aitsind.com'], 'Area': 1}, '/Account/AitsLogin')
                assert rows('SELECT COUNT(*) FROM TicketEmailNotifications')[0][0] == 0
                creator_id, company_id = rows("SELECT Id,ClientId FROM ClientUsers WHERE Email='1@gmail.com'")[0]
                other_company = rows("SELECT Id FROM Companies WHERE Name='Demo Beacon Retail'")[0][0]
                for company in [company_id, other_company]:
                    admin.form('/Clients/AddProject', {'clientId': company, 'name': 'EWS'}, f'/Clients/Details/{company}')
                project_id = rows("SELECT Id FROM Projects WHERE ClientId=? AND Name='EWS'", (company_id,))[0][0]
                other_project_id = rows("SELECT Id FROM Projects WHERE ClientId=? AND Name='EWS'", (other_company,))[0][0]
                other_same_company_project = rows("SELECT Id FROM Projects WHERE ClientId=? AND Name='Vendor Portal'", (company_id,))[0][0]
                staff_ids = dict(rows('SELECT Email,Id FROM AitsStaff'))
                for email in ['demo.arun@aitsind.com', 'demo.priya@aitsind.com', 'demo.rahul@aitsind.com', 'support@example.com']:
                    staff_id = staff_ids[email]
                    admin.form(f'/Users/Projects/{staff_id}', {'SelectedProjectIds': project_id}, f'/Users/Projects/{staff_id}')
                admin.form(f'/Users/Projects/{staff_ids["demo.neha@aitsind.com"]}', {'SelectedProjectIds': other_project_id}, f'/Users/Projects/{staff_ids["demo.neha@aitsind.com"]}')
                admin.form('/Users/SetActive', {'id': staff_ids['demo.rahul@aitsind.com'], 'isActive': False}, '/Users/AitsStaff')
                anonymous.form('/Account/AitsSignup', {'Name': 'Pending notifications staff', 'Email': 'pending@notify.example', 'Department': 'Support', 'Password': password, 'ConfirmPassword': password}, '/Account/AitsSignup')
                pending_id = rows("SELECT Id FROM AitsStaff WHERE Email='pending@notify.example'")[0][0]
                admin.form(f'/Users/Projects/{pending_id}', {'SelectedProjectIds': project_id}, f'/Users/Projects/{pending_id}')
                fields = {'Id': 0, 'Title': 'New EWS <b>support</b> ticket', 'Description': '<script>sample text</script>\nInvoice approval failed.', 'Priority': 3, 'ProjectId': project_id, 'ClientId': other_company}
                saved = creator.form('/Tickets/Save', fields, '/Tickets/Create')
                assert saved[0] == 200 and 'notifications queued for 2' in saved[1]
                ticket_id = rows('SELECT Id FROM Tickets WHERE Title=?', (fields['Title'],))[0][0]
                wait_status(ticket_id, 2, 2)
                expected_recipients = {'demo.arun@aitsind.com', 'demo.priya@aitsind.com'}
                assert {row[0] for row in rows('SELECT RecipientEmail FROM TicketEmailNotifications WHERE TicketId=?', (ticket_id,))} == expected_recipients
                assert len(captured()) == 2
                recipients = set()
                for path in captured():
                    message = BytesParser(policy=policy.default).parsebytes(path.read_bytes())
                    addresses = getaddresses(message.get_all('To', []))
                    assert len(addresses) == 1
                    recipients.add(addresses[0][1])
                    assert 'Demo Atlas Industries / EWS' in str(message['Subject'])
                    plain = '\n'.join(part.get_content() for part in message.walk() if part.get_content_type() == 'text/plain')
                    html = '\n'.join(part.get_content() for part in message.walk() if part.get_content_type() == 'text/html')
                    assert f'Ticket: #{ticket_id:04d}' in plain and 'Priority: Urgent' in plain
                    assert 'Invoice approval failed.' in plain and 'Project: EWS' in plain
                    assert f'https://helpdesk.example/Tickets/Details/{ticket_id}' in plain
                    assert '<script>sample text</script>' not in html and '&lt;script&gt;' in html
                    assert '<b>support</b>' not in html
                assert recipients == expected_recipients
                assert 'Ticket email notifications' in admin.request(f'/Tickets/Details/{ticket_id}')[1]
                creator.form('/Tickets/Save', {**fields, 'Id': ticket_id, 'Title': 'Edited EWS ticket'}, f'/Tickets/Edit/{ticket_id}')
                assert rows('SELECT Title FROM Tickets WHERE Id=?', (ticket_id,))[0][0] == 'Edited EWS ticket'
                assert rows('SELECT COUNT(*) FROM TicketEmailNotifications WHERE TicketId=?', (ticket_id,))[0][0] == 2
                sent_id = rows('SELECT TOP(1) Id FROM TicketEmailNotifications WHERE TicketId=?', (ticket_id,))[0][0]
                for browser in [creator, staff]:
                    assert browser.form('/Tickets/RetryEmail', {'id': sent_id}, '/Tickets/Create')[0] == 403
                admin.form('/Tickets/RetryEmail', {'id': sent_id}, f'/Tickets/Details/{ticket_id}')
                assert rows('SELECT Status,Attempts FROM TicketEmailNotifications WHERE Id=?', (sent_id,))[0] == [2, 1]

                # Missing sender credentials fail delivery, but never roll back ticket creation.
                stop()
                settings['Transport'] = 'Smtp'
                settings_path.write_text(json.dumps({'Email': settings}))
                start()
                result = creator.form('/Tickets/Save', {**fields, 'Title': 'EWS mail retry test'}, '/Tickets/Create')
                assert result[0] == 200
                retry_ticket = rows("SELECT Id FROM Tickets WHERE Title='EWS mail retry test'")[0][0]
                wait_status(retry_ticket, 4, 2)
                assert len(captured()) == 2
                assert 'Retry email' in admin.request(f'/Tickets/Details/{retry_ticket}')[1]
                stop()
                settings['Transport'] = 'Pickup'
                settings_path.write_text(json.dumps({'Email': settings}))
                start()
                for notification_id, in rows('SELECT Id FROM TicketEmailNotifications WHERE TicketId=?', (retry_ticket,)):
                    assert admin.form('/Tickets/RetryEmail', {'id': notification_id}, f'/Tickets/Details/{retry_ticket}')[0] == 200
                wait_status(retry_ticket, 2, 2)
                assert len(captured()) == 4

                def deferred_notification(staff_id, recipient):
                    new_ticket = rows("INSERT Tickets(ClientId,ProjectId,Title,Description,Status,Priority,RequesterId,CreatedAt,UpdatedAt) OUTPUT INSERTED.Id VALUES (?,?,'Deferred delivery','Check access at delivery',0,1,?,GETUTCDATE(),GETUTCDATE())", (company_id, project_id, creator_id))[0][0]
                    rows('INSERT TicketEmailNotifications(TicketId,StaffId,CompanyId,ProjectId,RecipientEmail,Status,Attempts,CreatedAt,NextAttemptAt) VALUES (?,?,?,?,?,0,0,GETUTCDATE(),DATEADD(minute,10,GETUTCDATE()))', (new_ticket, staff_id, company_id, project_id, recipient))
                    return new_ticket

                # Changes made while queued are respected before the message is sent.
                priya_id = staff_ids['demo.priya@aitsind.com']
                revoked = deferred_notification(priya_id, 'demo.priya@aitsind.com')
                admin.form(f'/Users/Projects/{priya_id}', {}, f'/Users/Projects/{priya_id}')
                rows('UPDATE TicketEmailNotifications SET NextAttemptAt=GETUTCDATE() WHERE TicketId=?', (revoked,))
                wait_status(revoked, 5, 1)
                assert len(captured()) == 4
                admin.form(f'/Users/Projects/{priya_id}', {'SelectedProjectIds': project_id}, f'/Users/Projects/{priya_id}')
                moved = deferred_notification(priya_id, 'demo.priya@aitsind.com')
                rows('UPDATE Tickets SET ProjectId=? WHERE Id=?', (other_same_company_project, moved))
                rows('UPDATE TicketEmailNotifications SET NextAttemptAt=GETUTCDATE() WHERE TicketId=?', (moved,))
                wait_status(moved, 5, 1)
                assert len(captured()) == 4
                changed_address = deferred_notification(priya_id, 'demo.priya@aitsind.com')
                admin.form('/Users/Save', {'Id': priya_id, 'Name': 'Demo Priya', 'Email': 'changed@notify.example', 'Department': 'Support', 'UserType': 1}, f'/Users/Edit/{priya_id}')
                rows('UPDATE TicketEmailNotifications SET NextAttemptAt=GETUTCDATE() WHERE TicketId=?', (changed_address,))
                wait_status(changed_address, 5, 1)
                assert len(captured()) == 4
                assert not rows('DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS')
                print('PASS: project/company recipient matching, active/approved/account eligibility, private per-recipient messages, ticket details and links, HTML escaping, creation-only delivery, persistent failures and Admin retries, no re-send of sent messages, and revoked/moved/address-changed access suppression. All emails were captured locally.')
            except Exception:
                log.flush()
                print(log_path.read_text(encoding='utf-8')[-6500:])
                raise
            finally:
                stop()
                assert re.fullmatch(r'AitsHelpDeskSmoke_[0-9a-f]{32}', database)
                query(master, f"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END")


if __name__ == '__main__':
    check()
