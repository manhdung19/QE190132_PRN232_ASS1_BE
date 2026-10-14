# Creates a disposable local PostgreSQL cluster and runs the selected auth stage.
param(
    [ValidateSet('register', 'login', 'authorization')][string]$Stage = 'register',
    [string]$PostgresBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55434
)

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $env:TEMP ('tasktrack-a2-auth-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$started = $false
try {
    & (Join-Path $PostgresBin 'initdb.exe') -D (Join-Path $testRoot 'data') --encoding=UTF8 --no-locale --auth=trust -U postgres
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
    & (Join-Path $PostgresBin 'pg_ctl.exe') -D (Join-Path $testRoot 'data') -l (Join-Path $testRoot 'postgres.log') -o "-h 127.0.0.1 -p $Port" start
    if ($LASTEXITCODE -ne 0) { throw 'pg_ctl start failed' }
    $started = $true

    $env:PGHOST = '127.0.0.1'; $env:PGPORT = [string]$Port; $env:PGUSER = 'postgres'
    & (Join-Path $PostgresBin 'psql.exe') -X -q -v ON_ERROR_STOP=1 -d postgres -c 'CREATE DATABASE tasktrack_a2_test'
    if ($LASTEXITCODE -ne 0) { throw 'create database failed' }
    & (Join-Path $PostgresBin 'psql.exe') -X -q -v ON_ERROR_STOP=1 -d tasktrack_a2_test -f 'database/TaskManagementDB_Postgres.sql'
    if ($LASTEXITCODE -ne 0) { throw 'Assignment 1 import failed' }
    & (Join-Path $PostgresBin 'psql.exe') -X -q -v ON_ERROR_STOP=1 -d tasktrack_a2_test -f 'database/Assignment2_Upgrade.sql'
    if ($LASTEXITCODE -ne 0) { throw 'Assignment 2 upgrade failed' }

    $env:A2_TEST_CONNECTION = "Host=127.0.0.1;Port=$Port;Database=tasktrack_a2_test;Username=postgres;Pooling=false"
    & dotnet run --project 'tools/TaskTrack.AuthCheck/TaskTrack.AuthCheck.csproj' --no-build -- $Stage
    if ($LASTEXITCODE -ne 0) { throw "AuthCheck stage $Stage failed" }
}
finally {
    if ($started) { & (Join-Path $PostgresBin 'pg_ctl.exe') -D (Join-Path $testRoot 'data') stop -m fast | Out-Null }
    $target = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
    if (-not ($target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -and
              [IO.Path]::GetFileName($target) -match '^tasktrack-a2-auth-[0-9a-f]{32}$')) {
        throw "Unsafe cleanup target: $target"
    }
    Remove-Item -LiteralPath $target -Recurse -Force
    Remove-Item Env:A2_TEST_CONNECTION, Env:PGHOST, Env:PGPORT, Env:PGUSER -ErrorAction SilentlyContinue
}
