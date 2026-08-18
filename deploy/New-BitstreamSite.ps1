<#
.SYNOPSIS
    Creates or updates the IIS site, application pool, bindings and folder permissions for an
    environment.

.DESCRIPTION
    TR-ARC-08: environments are provisioned through repeatable, scripted deployment. Everything
    that has to be true of the site lives in deploy/environments/<name>.psd1 and is applied
    here, so the answer to "how is production configured" is a file, not somebody's memory of
    what they clicked in IIS Manager.

    Idempotent: re-running it converges the site onto the definition, which is also how a
    configuration drift is corrected.

    What it sets and why:
      * Application pool with No Managed Code — the ASP.NET Core Module hosts the runtime, so
        the .NET Framework CLR must not be loaded.
      * Idle timeout and periodic recycling disabled. The portal holds an in-memory outbox
        dispatcher and a scheduler; a recycle in the middle of a dispatch is survivable
        (messages are in the database) but pointless, and idle shutdown makes the first request
        after a quiet period slow enough to breach TR-NFR-01.
      * ACLs granting the pool identity read on the site and write on the log folder only. The
        application never needs to write to its own binaries.
      * HTTPS binding from the environment definition. TR-SEC-26 requires TLS 1.2 or higher.

    Run elevated on the web server.

.PARAMETER Environment
    Environment name; loads deploy/environments/<name>.psd1.

.EXAMPLE
    .\New-BitstreamSite.ps1 -Environment uat
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $Environment,
    [Parameter(Mandatory = $true)] [ValidateSet('Web', 'Api')] [string] $Component
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module WebAdministration -ErrorAction Stop

$definitionPath = Join-Path $PSScriptRoot "environments\$Environment.psd1"

if (-not (Test-Path $definitionPath)) {
    throw "No environment definition at $definitionPath. Available: $((Get-ChildItem (Join-Path $PSScriptRoot 'environments') -Filter *.psd1).BaseName -join ', ')"
}

$config = Import-PowerShellDataFile -Path $definitionPath
# Two sites per environment; -Component picks which one this run targets.
$site = $config.Sites[$Component]
if (-not $site) { throw "Environment '$Environment' has no '$Component' site defined." }

Write-Host "Provisioning $($config.Name): site $($site.Name)"

# --- Folders ---------------------------------------------------------------------------
foreach ($path in @($site.PhysicalPath, $site.LogPath)) {
    if (-not (Test-Path $path)) {
        if ($PSCmdlet.ShouldProcess($path, 'Create directory')) {
            New-Item -Path $path -ItemType Directory -Force | Out-Null
            Write-Host "  Created $path"
        }
    }
}

# --- Application pool ------------------------------------------------------------------
$appPoolPath = "IIS:\AppPools\$($site.AppPoolName)"

if (-not (Test-Path $appPoolPath)) {
    if ($PSCmdlet.ShouldProcess($site.AppPoolName, 'Create application pool')) {
        New-WebAppPool -Name $site.AppPoolName | Out-Null
        Write-Host "  Created application pool $($site.AppPoolName)"
    }
}

if ($PSCmdlet.ShouldProcess($site.AppPoolName, 'Configure application pool')) {
    # No Managed Code: the ASP.NET Core Module hosts the runtime.
    Set-ItemProperty $appPoolPath -Name managedRuntimeVersion -Value ''
    Set-ItemProperty $appPoolPath -Name managedPipelineMode -Value 'Integrated'
    Set-ItemProperty $appPoolPath -Name startMode -Value 'AlwaysRunning'

    # No idle shutdown, no timed recycle: a cold start after an idle period would breach
    # TR-NFR-01, and a scheduled recycle interrupts background work for no benefit.
    Set-ItemProperty $appPoolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Set-ItemProperty $appPoolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
    Set-ItemProperty $appPoolPath -Name recycling.periodicRestart.schedule -Value @()

    Set-ItemProperty $appPoolPath -Name processModel.identityType -Value 'SpecificUser'
    Set-ItemProperty $appPoolPath -Name processModel.userName -Value $site.AppPoolIdentity

    # The password is not set here. It is supplied by Set-AppPoolSecrets.ps1 from the secret
    # store at deployment time; a service account password in a checked-in script is exactly
    # what TR-SEC-28 forbids.
    Write-Host "  Application pool configured for $($site.AppPoolIdentity)"
    Write-Warning '  Application pool password not set. Run Set-AppPoolSecrets.ps1 before starting the site.'
}

# --- Site ------------------------------------------------------------------------------
$sitePath = "IIS:\Sites\$($site.Name)"
$firstBinding = $site.Bindings | Where-Object { $_.Protocol -eq 'https' } | Select-Object -First 1

if (-not $firstBinding) {
    throw "Environment $Environment has no https binding. TLS is mandatory (TR-SEC-26)."
}

if (-not (Test-Path $sitePath)) {
    if ($PSCmdlet.ShouldProcess($site.Name, 'Create site')) {
        New-Website -Name $site.Name `
            -PhysicalPath $site.PhysicalPath `
            -ApplicationPool $site.AppPoolName `
            -HostHeader $firstBinding.HostHeader `
            -Port $firstBinding.Port `
            -Ssl `
            -Force | Out-Null

        Write-Host "  Created site $($site.Name)"
    }
}
else {
    if ($PSCmdlet.ShouldProcess($site.Name, 'Update site')) {
        Set-ItemProperty $sitePath -Name physicalPath -Value $site.PhysicalPath
        Set-ItemProperty $sitePath -Name applicationPool -Value $site.AppPoolName
        Write-Host "  Updated site $($site.Name)"
    }
}

# --- Bindings --------------------------------------------------------------------------
foreach ($binding in $site.Bindings) {
    $existing = Get-WebBinding -Name $site.Name -Protocol $binding.Protocol -ErrorAction SilentlyContinue |
        Where-Object { $_.bindingInformation -like "*:$($binding.Port):$($binding.HostHeader)" }

    if (-not $existing) {
        if ($PSCmdlet.ShouldProcess("$($binding.Protocol)://$($binding.HostHeader):$($binding.Port)", 'Add binding')) {
            New-WebBinding -Name $site.Name `
                -Protocol $binding.Protocol `
                -Port $binding.Port `
                -HostHeader $binding.HostHeader `
                -SslFlags $(if ($binding.Protocol -eq 'https') { 1 } else { 0 })

            Write-Host "  Added $($binding.Protocol) binding for $($binding.HostHeader):$($binding.Port)"
        }
    }

    if ($binding.Protocol -eq 'https' -and $binding.ContainsKey('CertificateThumbprint')) {
        if ($binding.CertificateThumbprint -like 'REPLACE_*') {
            Write-Warning "  Certificate thumbprint is still a placeholder for $($binding.HostHeader). Set it in the environment definition."
            continue
        }

        $certificate = Get-ChildItem -Path Cert:\LocalMachine\My |
            Where-Object { $_.Thumbprint -eq $binding.CertificateThumbprint }

        if (-not $certificate) {
            throw "Certificate $($binding.CertificateThumbprint) is not in LocalMachine\My on this server."
        }

        if ($PSCmdlet.ShouldProcess($binding.HostHeader, 'Bind certificate')) {
            $sslBinding = "IIS:\SslBindings\!$($binding.Port)!$($binding.HostHeader)"

            if (Test-Path $sslBinding) {
                Remove-Item $sslBinding -Force
            }

            New-Item $sslBinding -Value $certificate -SSLFlags 1 | Out-Null
            Write-Host "  Bound certificate $($binding.CertificateThumbprint) to $($binding.HostHeader)"
        }
    }
}

# --- Permissions -----------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess($site.PhysicalPath, 'Set ACLs')) {
    # Read and execute on the site: the application never writes to its own binaries, and a
    # process that cannot overwrite its own code cannot be made to.
    $acl = Get-Acl $site.PhysicalPath
    $readRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $site.AppPoolIdentity, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.SetAccessRule($readRule)
    Set-Acl -Path $site.PhysicalPath -AclObject $acl

    # Write on the log folder only.
    $logAcl = Get-Acl $site.LogPath
    $writeRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $site.AppPoolIdentity, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $logAcl.SetAccessRule($writeRule)
    Set-Acl -Path $site.LogPath -AclObject $logAcl

    Write-Host "  ACLs set for $($site.AppPoolIdentity)"
}

Write-Host ''
Write-Host "Site $($site.Name) provisioned." -ForegroundColor Green
Write-Host 'Next: Set-AppPoolSecrets.ps1, then db\Deploy-Database.ps1, then Deploy-Application.ps1.'
