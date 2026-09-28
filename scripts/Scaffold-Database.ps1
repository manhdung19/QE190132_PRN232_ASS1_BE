[CmdletBinding()]
param([string]$SecretFile)
$ErrorActionPreference = 'Stop'
$connectionUrl = $env:DATABASE_URL
if ($SecretFile) {
    $content = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $SecretFile).Path)
    $found = [regex]::Matches($content, 'postgres(?:ql)?://[^\s"''`]+')
    if ($found.Count -ne 1) { throw 'Expected one PostgreSQL URL in the secret file.' }
    $connectionUrl = $found[0].Value
}
if ([string]::IsNullOrWhiteSpace($connectionUrl)) { throw 'Supply DATABASE_URL through the environment or -SecretFile.' }
$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = 'dotnet'
$info.WorkingDirectory = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.EnvironmentVariables['DATABASE_URL'] = $connectionUrl
$info.Arguments = 'ef dbcontext scaffold Name=ConnectionStrings:TaskTrack Npgsql.EntityFrameworkCore.PostgreSQL --project TaskTrack.Repo --startup-project TaskTrack.API --output-dir Models --context-dir Data --context TaskManagementDbContext --namespace TaskTrack.Repo.Models --context-namespace TaskTrack.Repo.Data --table public.Department --table public.Project --table public.Task --table public.Tag --table public.TaskTag --no-onconfiguring'
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $info
try {
    if (-not $process.Start()) { throw 'Unable to start dotnet ef.' }
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $output = $outTask.GetAwaiter().GetResult() + $errTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        [Uri]$uri = $connectionUrl
        $parts = $uri.UserInfo -split ':', 2
        $redactions = @($connectionUrl, [Uri]::UnescapeDataString($connectionUrl), $uri.Host)
        foreach ($part in $parts) {
            $decoded = [Uri]::UnescapeDataString($part)
            $redactions += @($part, $decoded, $decoded.Replace('"', '""'), $decoded.Replace("'", "''"))
        }
        $safeOutput = $output
        foreach ($item in ($redactions | Sort-Object Length -Descending)) {
            if ($item) { $safeOutput = $safeOutput.Replace($item, '[REDACTED]') }
        }
        Write-Output $safeOutput
        $reason = if ($output -match 'Build failed') { 'Build failed; run dotnet build separately.' }
            elseif ($output -match 'already exist') { 'Generated files already exist; nothing will be overwritten automatically.' }
            else { 'EF command failed. Raw output withheld to protect connection details.' }
        throw ('Scaffold exit {0}: {1}' -f $process.ExitCode, $reason)
    }
    # Resolve the SQL Task entity name against System.Threading.Tasks.Task without changing mappings.
    $contextPath = Join-Path $info.WorkingDirectory 'TaskTrack.Repo/Data/TaskManagementDbContext.cs'
    $contextCode = [System.IO.File]::ReadAllText($contextPath).Replace('using TaskTrack.Repo.Models;', "using TaskTrack.Repo.Models;`nusing Task = TaskTrack.Repo.Models.Task;")
    [System.IO.File]::WriteAllText($contextPath, $contextCode)
    Write-Output 'EF Core Database First scaffold succeeded; connection details were not logged.'
} finally {
    $info.EnvironmentVariables.Remove('DATABASE_URL')
    $process.Dispose()
}
