[CmdletBinding()]
param([string]$PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:DATABASE_URL)) {
    throw 'DATABASE_URL is missing from this PowerShell process. No database operation was attempted.'
}
if (-not (Test-Path -LiteralPath $PsqlPath -PathType Leaf)) {
    throw 'psql.exe was not found. Supply its full path through -PsqlPath.'
}
$sqlFile = (Resolve-Path (Join-Path $PSScriptRoot '../database/TaskManagementDB_Postgres.sql')).Path
$expectedHash = '26FC6105C8B97787D8C1034B5CF6CAEDD7FE3D0B88FBECC43850F3B6CCB410E9'
if ((Get-FileHash -LiteralPath $sqlFile -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The SQL file differs from the reviewed version. Review it again before importing.'
}
[Uri]$dbUri = $null
if (-not [Uri]::TryCreate($env:DATABASE_URL, [UriKind]::Absolute, [ref]$dbUri) -or
    $dbUri.Scheme -notin @('postgres', 'postgresql')) {
    throw 'DATABASE_URL is not a valid PostgreSQL URL. Its value has not been logged.'
}
$dbName = [Uri]::UnescapeDataString($dbUri.AbsolutePath.TrimStart('/'))
if ($dbName -cne 'tasktrack_ass1') { throw 'URL database name is not tasktrack_ass1. Import stopped.' }
$credentials = ($dbUri.UserInfo -split ':', 2)
if ($credentials.Count -ne 2) { throw 'DATABASE_URL must contain username and password.' }
$sslMode = 'require'
if ($dbUri.Query) {
    foreach ($part in $dbUri.Query.TrimStart('?').Split('&')) {
        $pair = ($part -split '=', 2)
        if ($pair.Count -ne 2 -or $pair[0] -ne 'sslmode' -or
            $pair[1] -notin @('require', 'verify-ca', 'verify-full')) {
            throw 'Unsupported URL query options. Review connection settings without printing the URL.'
        }
        $sslMode = $pair[1]
    }
}
# Credentials are passed only in the child process environment, never as arguments or files.
$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $PsqlPath
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($key in @($info.EnvironmentVariables.Keys)) {
    if ($key -like 'PG*' -or $key -eq 'DATABASE_URL') { $info.EnvironmentVariables.Remove($key) }
}
$info.EnvironmentVariables['PGHOST'] = $dbUri.DnsSafeHost
$info.EnvironmentVariables['PGPORT'] = if ($dbUri.Port -gt 0) { [string]$dbUri.Port } else { '5432' }
$info.EnvironmentVariables['PGDATABASE'] = $dbName
$info.EnvironmentVariables['PGUSER'] = [Uri]::UnescapeDataString($credentials[0])
$info.EnvironmentVariables['PGPASSWORD'] = [Uri]::UnescapeDataString($credentials[1])
$info.EnvironmentVariables['PGSSLMODE'] = $sslMode
$info.EnvironmentVariables['PGCONNECT_TIMEOUT'] = '15'
$info.EnvironmentVariables['PGCLIENTENCODING'] = 'UTF8'
$preflight = Join-Path $PSScriptRoot 'preflight.sql'
$verify = Join-Path $PSScriptRoot 'verify-import.sql'
$info.Arguments = '-X --no-password --set=ON_ERROR_STOP=1 --set=VERBOSITY=sqlstate --single-transaction -f "{0}" -f "{1}" -f "{2}"' -f $preflight, $sqlFile, $verify
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $info
try {
    if (-not $process.Start()) { throw 'Unable to start psql.' }
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $outTask.GetAwaiter().GetResult()
    $stderr = $errTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        # Do not emit raw connection errors: they can contain connection metadata.
        $reason = switch -Regex ($stderr) {
            'P1001' { 'Connected database is not tasktrack_ass1.'; break }
            'P1002' { 'Assignment tables/objects already exist. Import was stopped before the original SQL.'; break }
            'P1003' { 'Seed counts did not match; transaction rolled back.'; break }
            'P1004' { 'Foreign keys did not match; transaction rolled back.'; break }
            'password authentication failed' { 'Database authentication failed.'; break }
            'could not translate host name|Name or service not known|No such host' { 'DNS resolution failed.'; break }
            'timeout expired|timed out' { 'Connection timed out. Check Render access rules and network.'; break }
            'Connection refused|could not connect|Network is unreachable' { 'Network connection failed.'; break }
            'SSL|certificate' { 'TLS/SSL connection failed.'; break }
            default { 'psql failed; no raw diagnostic was printed to protect connection details. If the connection dropped during commit, verify database state before retrying.' }
        }
        throw ('Import unsuccessful (psql exit {0}). {1}' -f $process.ExitCode, $reason)
    }
    Write-Output $stdout
    Write-Output 'Import committed successfully. Database, seed counts and four foreign keys verified.'
}
finally {
    $info.EnvironmentVariables.Remove('PGPASSWORD')
    $process.Dispose()
}
