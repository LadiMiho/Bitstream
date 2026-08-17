<#
.SYNOPSIS
    Applies the Bitstream Portal database scripts in order.

.DESCRIPTION
    TR-ARC-08 requires repeatable, scripted deployment; manual configuration of production
    is not permitted. This script applies every file in db/mssql in numeric order and
    records each one in ops.SchemaVersion. Scripts are idempotent, so a re-run is safe and
    is the normal way to bring an environment up to date.

    Requires the SqlServer PowerShell module (Install-Module SqlServer) on the deployment
    host. Runs on Windows Server against MSSQL; no container tooling is involved.

.PARAMETER ServerInstance
    SQL Server instance, e.g. SQLPROD01\BITSTREAM.

.PARAMETER Database
    Target database name, e.g. BitstreamPortal.

.PARAMETER AppUser
    Login granted application rights by 0008_permissions.sql, e.g. DOMAIN\svc_bitstream_app.

.PARAMETER SchemaVersion
    Version stamped into ops.SchemaVersion. Must match BitstreamDbContext.ExpectedSchemaVersion.

.PARAMETER WhatIf
    Lists the scripts that would be applied without executing them.

.EXAMPLE
    .\Deploy-Database.ps1 -ServerInstance SQLUAT01 -Database BitstreamPortal -AppUser 'CORP\svc_bitstream_uat'
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $ServerInstance,
    [Parameter(Mandatory = $true)] [string] $Database,
    [Parameter(Mandatory = $true)] [string] $AppUser,
    [int] $SchemaVersion = 1
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module SqlServer -ErrorAction Stop

$scriptRoot = Join-Path $PSScriptRoot 'mssql'
$scripts = Get-ChildItem -Path $scriptRoot -Filter '*.sql' | Sort-Object Name

if (-not $scripts) {
    throw "No scripts found in $scriptRoot."
}

Write-Host "Target : $ServerInstance / $Database"
Write-Host "Scripts: $($scripts.Count)"

foreach ($script in $scripts) {
    if (-not $PSCmdlet.ShouldProcess($script.Name, 'Apply')) {
        continue
    }

    Write-Host "Applying $($script.Name) ..."

    Invoke-Sqlcmd `
        -ServerInstance $ServerInstance `
        -Database $Database `
        -InputFile $script.FullName `
        -Variable @("AppUser=$AppUser") `
        -QueryTimeout 300 `
        -AbortOnError

    # Recorded after the script succeeds, so a failed run leaves no version claim behind.
    $ledger = @"
MERGE ops.SchemaVersion AS target
USING (VALUES (N'$($script.Name)', $SchemaVersion)) AS source (ScriptName, SchemaVersion)
    ON target.ScriptName = source.ScriptName
WHEN MATCHED THEN UPDATE SET SchemaVersion = source.SchemaVersion, AppliedAt = SYSDATETIMEOFFSET(), AppliedBy = SUSER_SNAME()
WHEN NOT MATCHED BY TARGET THEN INSERT (ScriptName, SchemaVersion) VALUES (source.ScriptName, source.SchemaVersion);
"@

    Invoke-Sqlcmd -ServerInstance $ServerInstance -Database $Database -Query $ledger -AbortOnError
}

Write-Host "Done. Schema version $SchemaVersion applied to $Database."
