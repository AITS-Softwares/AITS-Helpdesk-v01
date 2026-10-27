"""SQL Server test queries using Windows PowerShell's built-in SqlClient."""
import base64
import json
import re
import subprocess


def query(connection, sql, args=()):
    connection = re.sub(r'(?i)Command Timeout=[^;]*;?', '', connection)
    for index in range(len(args)):
        sql = sql.replace('?', f'@p{index}', 1)
    payload = base64.b64encode(json.dumps({'connection': connection, 'sql': sql, 'args': args}).encode()).decode()
    script = r"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PAYLOAD__')) | ConvertFrom-Json
$connection = New-Object System.Data.SqlClient.SqlConnection($payload.connection)
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = $payload.sql
    $command.CommandTimeout = 30
    for ($i = 0; $i -lt $payload.args.Count; $i++) { $null = $command.Parameters.AddWithValue('@p' + $i, $payload.args[$i]) }
    $reader = $command.ExecuteReader()
    try {
        $result = New-Object 'System.Collections.Generic.List[object]'
        while ($reader.Read()) {
            $values = @()
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                if ($reader.IsDBNull($i)) { $values += $null } else { $values += $reader.GetValue($i) }
            }
            $result.Add($values)
        }
        ConvertTo-Json -InputObject $result.ToArray() -Compress -Depth 10
    } finally { $reader.Dispose() }
} finally { $connection.Dispose() }
""".replace('__PAYLOAD__', payload)
    encoded = base64.b64encode(script.encode('utf-16le')).decode()
    result = subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], capture_output=True, encoding='utf-8')
    if result.returncode: raise RuntimeError(result.stderr)
    return json.loads(result.stdout.strip())
