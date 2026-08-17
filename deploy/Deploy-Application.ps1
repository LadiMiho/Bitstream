<#
.SYNOPSIS
    Deploys a published build to an environment's IIS site.

.DESCRIPTION
    TR-NFR-19: deployment without data loss, with a documented rollback. This script does the
    application half; db\Deploy-Database.ps1 does the schema half, and the order is database
    first (see below).

    The sequence:
      1. Verify the schema is at the version this build expects (db\Get-SchemaStatus.ps1). The
         application refuses to start on a mismatch, so failing here is better than failing
         after the files are already in place.
      2. Drop app_offline.htm so IIS drains in-flight requests and stops the process cleanly —
         an outbox dispatch that is mid-flight finishes or is retried from the database.
      3. Back up the current site so a rollback is a file copy, not a rebuild.
      4. Copy the new build, preserving nothing but the log folder.
      5. Remove app_offline.htm and warm the site.

    Deliberately not xcopy-over-the-top: a stale assembly left behind by a rename is a class of
    failure that is hard to diagnose and trivial to prevent.

    Run elevated on the web server.

.PARAMETER Environment
    Environment name; loads deploy/environments/<name>.psd1.

.PARAMETER PackagePath
    Folder containing the dotnet publish output, or the extracted CI artifact.

.PARAMETER SkipSchemaCheck
    Deploy without verifying the schema version. For a rollback where the schema is knowingly
    ahead of the application.

.EXAMPLE
    .\Deploy-Application.ps1 -Environment uat -PackagePath C:\artifacts\publish
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $Environment,
    [Parameter(Mandatory = $true)] [string] $PackagePath,
    [switch] $SkipSchemaCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module WebAdministration -ErrorAction Stop

$config = Import-PowerShellDataFile -Path (Join-Path $PSScriptRoot "environments\$Environment.psd1")
$site = $config.Site

if (-not (Test-Path (Join-Path $PackagePath 'Bitstream.Api.dll'))) {
    throw "$PackagePath does not look like a publish output: Bitstream.Api.dll is missing."
}

# --- 1. Schema gate ---------------------------------------------------------------------
if (-not $SkipSchemaCheck) {
    Write-Host 'Checking the database schema version...'

    & (Join-Path $PSScriptRoot '..\db\Get-SchemaStatus.ps1') `
        -ServerInstance $config.Database.ServerInstance `
        -Database $config.Database.Name `
        -ExpectedVersion $config.Database.SchemaVersion

    if ($LASTEXITCODE -ne 0) {
        throw 'Schema is not at the version this build expects. Apply db\Deploy-Database.ps1 first, or use -SkipSchemaCheck for a deliberate rollback.'
    }
}

$appOffline = Join-Path $site.PhysicalPath 'app_offline.htm'
$backupPath = Join-Path (Split-Path $site.PhysicalPath -Parent) "$($site.Name)_backup_$(Get-Date -Format 'yyyyMMdd-HHmmss')"

try {
    # --- 2. Take the site offline -------------------------------------------------------
    if ($PSCmdlet.ShouldProcess($site.Name, 'Take offline')) {
        @'
<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><title>Bitstream Portal — maintenance</title></head>
<body style="font-family:Segoe UI,system-ui,sans-serif;margin:4rem auto;max-width:32rem">
<h1>Maintenance in progress</h1>
<p>The Bitstream Portal is briefly unavailable while an update is applied. Please try again in a few minutes.</p>
</body></html>
'@ | Set-Content -Path $appOffline -Encoding UTF8

        # ANCM notices the file and shuts the application down; give in-flight requests a moment.
        Start-Sleep -Seconds 5
        Write-Host 'Site offline.'
    }

    # --- 3. Back up ---------------------------------------------------------------------
    if ($PSCmdlet.ShouldProcess($site.PhysicalPath, "Back up to $backupPath")) {
        Copy-Item -Path $site.PhysicalPath -Destination $backupPath -Recurse -Force
        Write-Host "Backed up to $backupPath"
    }

    # --- 4. Replace the application -----------------------------------------------------
    if ($PSCmdlet.ShouldProcess($site.PhysicalPath, 'Replace application files')) {
        Get-ChildItem -Path $site.PhysicalPath -Force |
            Where-Object { $_.Name -ne 'app_offline.htm' -and $_.Name -ne 'logs' } |
            Remove-Item -Recurse -Force

        Copy-Item -Path (Join-Path $PackagePath '*') -Destination $site.PhysicalPath -Recurse -Force
        Write-Host 'Application files replaced.'
    }
}
finally {
    # --- 5. Back online -----------------------------------------------------------------
    # In finally: a failure part-way through must not leave the site offline with no notice.
    if (Test-Path $appOffline) {
        Remove-Item $appOffline -Force
        Write-Host 'Site online.'
    }
}

if ($PSCmdlet.ShouldProcess($site.AppPoolName, 'Recycle and warm')) {
    Restart-WebAppPool -Name $site.AppPoolName

    $binding = $config.Bindings | Where-Object { $_.Protocol -eq 'https' } | Select-Object -First 1
    $healthUrl = "https://$($binding.HostHeader)/health/ready"

    Write-Host "Warming $healthUrl ..."

    # The first request pays for JIT and start-up validation; do it here rather than making a
    # user wait for it (TR-NFR-01). A failing readiness probe is also the signal that the
    # deployment is wrong, and it is better to see that now.
    try {
        $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 60
        Write-Host "Readiness: $($response.StatusCode)" -ForegroundColor Green
    }
    catch {
        Write-Warning "Readiness probe did not return success: $($_.Exception.Message)"
        Write-Warning "Roll back with: Deploy-Application.ps1 -Environment $Environment -PackagePath $backupPath -SkipSchemaCheck"
    }
}

Write-Host ''
Write-Host "Deployed to $($config.Name)." -ForegroundColor Green
Write-Host "Rollback package: $backupPath"
