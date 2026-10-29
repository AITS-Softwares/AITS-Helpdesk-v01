$ErrorActionPreference = 'Stop'
$projectRoot = Join-Path $PSScriptRoot '../Help Desk'
$settings = Get-Content (Join-Path $projectRoot 'appsettings.json') -Raw | ConvertFrom-Json
$connectionText = $settings.ConnectionStrings.HelpDesk -replace '(?i)Command Timeout=[^;]*;?', ''
$connection = New-Object System.Data.SqlClient.SqlConnection($connectionText)
try {
    $connection.Open()
    $backupPath = [IO.Path]::GetFullPath((Join-Path $projectRoot ('App_Data/pre-demo-cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak')))
    $backup = $connection.CreateCommand()
    $backup.CommandTimeout = 120
    $backup.CommandText = 'BACKUP DATABASE ' + ('[' + $connection.Database.Replace(']', ']]') + ']') + ' TO DISK = @path WITH COPY_ONLY, CHECKSUM'
    $null = $backup.Parameters.AddWithValue('@path', $backupPath)
    $null = $backup.ExecuteNonQuery()
    Write-Output ('Database backup: ' + $backupPath)
    $cleanup = $connection.CreateCommand()
    $cleanup.CommandText = @"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @tickets TABLE (Id int);
INSERT INTO @tickets SELECT Id FROM Tickets WHERE Title LIKE '[[]Demo]%' AND Description LIKE 'Sample support request for Demo %This is demo data for testing staff project visibility.';
DELETE FROM Tickets WHERE Id IN (SELECT Id FROM @tickets);
DECLARE @ticketCount int = @@ROWCOUNT;
DECLARE @users TABLE (Id int);
INSERT INTO @users SELECT Id FROM ClientUsers u WHERE Name LIKE 'Demo Client [1-3] User [1-2]' AND Email LIKE 'demo.client%.user%@example.com' AND NOT EXISTS (SELECT 1 FROM Tickets t WHERE t.RequesterId=u.Id);
DELETE FROM PasswordResetRequests WHERE Email IN (SELECT Email FROM ClientAccounts WHERE DeskUserId IN (SELECT Id FROM @users));
DELETE FROM ClientAccounts WHERE DeskUserId IN (SELECT Id FROM @users);
DELETE FROM ClientUsers WHERE Id IN (SELECT Id FROM @users);
DECLARE @userCount int = @@ROWCOUNT;
DECLARE @companies TABLE (Id int);
INSERT INTO @companies SELECT Id FROM Companies c WHERE
 ((Name='Demo Atlas Industries' AND Email='demo.contact1@example.com') OR (Name='Demo Beacon Retail' AND Email='demo.contact2@example.com') OR (Name='Demo Cedar Logistics' AND Email='demo.contact3@example.com'))
 AND NOT EXISTS (SELECT 1 FROM ClientUsers u WHERE u.ClientId=c.Id)
 AND NOT EXISTS (SELECT 1 FROM Tickets t WHERE t.ClientId=c.Id);
DELETE FROM Projects WHERE ClientId IN (SELECT Id FROM @companies);
DELETE FROM Companies WHERE Id IN (SELECT Id FROM @companies);
DECLARE @companyCount int = @@ROWCOUNT;
COMMIT;
SELECT @ticketCount, @userCount, @companyCount;
"@
    $reader = $cleanup.ExecuteReader()
    while ($reader.Read()) { Write-Output ('Removed: {0} demo tickets, {1} demo client users, {2} demo companies.' -f $reader.GetInt32(0), $reader.GetInt32(1), $reader.GetInt32(2)) }
    $reader.Dispose()
} finally { $connection.Dispose() }
