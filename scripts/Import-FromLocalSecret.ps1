[CmdletBinding()]
param([string]$SecretFile)
if (-not $SecretFile) { $SecretFile = Join-Path $PSScriptRoot '../../DATABASE_URL' }
$ErrorActionPreference = 'Stop'
$content = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $SecretFile).Path).Trim()
$matchesFound = [regex]::Matches($content, 'postgres(?:ql)?://[^\s"''`]+')
if ($matchesFound.Count -ne 1) { throw 'Expected exactly one PostgreSQL URL in the local secret file. Content was not logged.' }
$previous = $env:DATABASE_URL
try {
    $env:DATABASE_URL = $matchesFound[0].Value
    Write-Output 'Connection URL loaded from local file without displaying it.'
    & (Join-Path $PSScriptRoot 'Import-AssignmentDatabase.ps1')
} finally {
    $env:DATABASE_URL = $previous
    $content = $null
    $matchesFound = $null
}
