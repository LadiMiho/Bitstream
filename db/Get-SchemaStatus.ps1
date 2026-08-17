<#
.SYNOPSIS
    Reports which schema scripts are applied to a database and which are pending.

.DESCRIPTION
    The application refuses to start against a schema version it was not built for
    (SchemaVersionGuard), so before a deployment you want to know where the database actually
    is. This compares the files in db/mssql with the rows in ops.SchemaVersion and prints the
    difference.

    Exit codes make it usable as a CI or deployment gate:
      0  every script is applied and the version matches -ExpectedVersion
      1  scripts are pending, or the version does not match
      2  the database is unreachable or ops.SchemaVersion does not exist

.PARAMETER ServerInstance
    SQL Server instance, e.g. SQLPROD01\BITSTREAM.

.PARAMETER Database
    Target database name.

.PARAMETER ExpectedVersion
    Version the application build expects; compare against BitstreamDbContext.ExpectedSchemaVersion.

.EXAMPLE
    .\Get-SchemaStatus.ps1 -ServerInstance SQLUAT01 -Database BitstreamPortal -ExpectedVersion 1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ServerInstance,
    [Parameter(Mandatory = $true)] [string] $Database,
    [int] $ExpectedVersion = 1
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module SqlServer -ErrorAction Stop

$scriptRoot = Join-Path $PSScriptRoot 'mssql'
$onDisk = Get-ChildItem -Path $scriptRoot -Filter '*.sql' | Sort-Object Name

try {
    $applied = Invoke-Sqlcmd `
        -ServerInstance $ServerInstance `
        -Database $Database `
        -Query 'SELECT ScriptName, SchemaVersion, AppliedAt, AppliedBy FROM ops.SchemaVersion ORDER BY ScriptName;' `
        -AbortOnError
}
catch {
    Write-Error "Cannot read ops.SchemaVersion on $ServerInstance/$Database. Has 0001_schemas_and_version.sql been applied? $($_.Exception.Message)"
    exit 2
}

$appliedNames = @($applied | ForEach-Object { $_.ScriptName })
$pending = @($onDisk | Where-Object { $appliedNames -notcontains $_.Name })
$unknown = @($appliedNames | Where-Object { $onDisk.Name -notcontains $_ })

$currentVersion = 0
if ($applied) {
    $currentVersion = ($applied | Measure-Object -Property SchemaVersion -Maximum).Maximum
}

Write-Host ""
Write-Host "Database        : $ServerInstance/$Database"
Write-Host "Schema version  : $currentVersion (application expects $ExpectedVersion)"
Write-Host "Scripts on disk : $($onDisk.Count)"
Write-Host "Applied         : $($appliedNames.Count)"
Write-Host ""

if ($applied) {
    $applied | Format-Table ScriptName, SchemaVersion, AppliedAt, AppliedBy -AutoSize | Out-String | Write-Host
}

if ($pending.Count -gt 0) {
    Write-Warning "Pending scripts (run Deploy-Database.ps1):"
    $pending | ForEach-Object { Write-Warning "  $($_.Name)" }
}

if ($unknown.Count -gt 0) {
    # A row with no matching file usually means the database is ahead of this checkout —
    # someone deployed a later release here, and deploying this build would move it backwards.
    Write-Warning "Applied scripts that are not in this checkout (database may be ahead of this build):"
    $unknown | ForEach-Object { Write-Warning "  $_" }
}

if ($pending.Count -gt 0 -or $currentVersion -ne $ExpectedVersion) {
    Write-Host ""
    Write-Host "Result: NOT READY for this application build." -ForegroundColor Yellow
    exit 1
}

Write-Host "Result: schema matches the application build." -ForegroundColor Green
exit 0
