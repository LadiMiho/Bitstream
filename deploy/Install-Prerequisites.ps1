<#
.SYNOPSIS
    Installs the Windows Server roles and runtime the Bitstream Portal needs.

.DESCRIPTION
    TR-ARC-08: environments are provisioned through repeatable, scripted deployment; manual
    configuration of production is not permitted. This is the first of the three scripts —
    prerequisites, then site, then application — and it is idempotent, so re-running it on a
    server that is already prepared changes nothing.

    Installs:
      * The IIS role and the features the portal actually uses. Not the default feature set:
        anything installed is attack surface, and WebDAV and directory browsing in particular
        are things a portal should not have (TR-SEC-27).
      * The .NET 10 Hosting Bundle, which provides the ASP.NET Core Module v2 and the shared
        framework. This is what makes containers unnecessary here.

    Run elevated on the web server.

.PARAMETER HostingBundleUrl
    Location of the .NET 10 Hosting Bundle installer. Defaults to the Microsoft download; on a
    network with no outbound access, point it at an internal file share.

.PARAMETER SkipHostingBundle
    Skip the runtime install, for a server where it is managed by another process.

.EXAMPLE
    .\Install-Prerequisites.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $HostingBundleUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/dotnet-hosting-win.exe',
    [switch] $SkipHostingBundle
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script elevated.'
}

# Deliberately explicit rather than -IncludeAllSubFeature: every feature here is one the portal
# uses, and nothing else is installed.
$features = @(
    'Web-Server'
    'Web-WebServer'
    'Web-Common-Http'
    'Web-Default-Doc'
    'Web-Static-Content'
    'Web-Http-Errors'
    'Web-Http-Logging'
    'Web-Request-Monitor'          # for failed-request tracing when something needs diagnosing
    'Web-Stat-Compression'
    'Web-Dyn-Compression'
    'Web-Filtering'                # request filtering, used by web.config limits
    'Web-Windows-Auth'             # not used by the portal; required for IIS Manager remote admin
    'Web-Mgmt-Console'
    'Web-Mgmt-Service'
)

Write-Host 'Installing IIS features...'

foreach ($feature in $features) {
    $state = Get-WindowsFeature -Name $feature

    if ($null -eq $state) {
        Write-Warning "Feature $feature is not available on this server; skipping."
        continue
    }

    if ($state.Installed) {
        Write-Host "  $feature already installed."
        continue
    }

    if ($PSCmdlet.ShouldProcess($feature, 'Install-WindowsFeature')) {
        Install-WindowsFeature -Name $feature | Out-Null
        Write-Host "  $feature installed."
    }
}

# Features the portal must NOT have. WebDAV in particular exposes write verbs on the site.
$unwanted = @('Web-DAV-Publishing', 'Web-Dir-Browsing')

foreach ($feature in $unwanted) {
    $state = Get-WindowsFeature -Name $feature

    if ($null -ne $state -and $state.Installed) {
        if ($PSCmdlet.ShouldProcess($feature, 'Uninstall-WindowsFeature')) {
            Uninstall-WindowsFeature -Name $feature | Out-Null
            Write-Warning "Removed $feature; it is not permitted on a portal server (TR-SEC-27)."
        }
    }
}

if ($SkipHostingBundle) {
    Write-Host 'Skipping the .NET Hosting Bundle as requested.'
    return
}

# Presence of the ASP.NET Core Module v2 is the honest test of whether the bundle is installed.
$ancmPath = Join-Path $env:SystemRoot 'System32\inetsrv\aspnetcorev2.dll'

if (Test-Path $ancmPath) {
    Write-Host 'ASP.NET Core Module v2 already present; Hosting Bundle installed.'
    return
}

if ($PSCmdlet.ShouldProcess('.NET Hosting Bundle', 'Install')) {
    $installer = Join-Path $env:TEMP 'dotnet-hosting-win.exe'

    Write-Host "Downloading the Hosting Bundle from $HostingBundleUrl ..."
    Invoke-WebRequest -Uri $HostingBundleUrl -OutFile $installer -UseBasicParsing

    Write-Host 'Installing...'
    $process = Start-Process -FilePath $installer -ArgumentList '/quiet', '/norestart' -Wait -PassThru

    # 3010 is "success, reboot required" and is not a failure.
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Hosting Bundle installer exited with $($process.ExitCode)."
    }

    Remove-Item $installer -Force

    # IIS must be restarted before it picks up the module.
    Write-Host 'Restarting IIS so the module is loaded...'
    net stop was /y | Out-Null
    net start w3svc | Out-Null

    Write-Host 'Hosting Bundle installed.'

    if ($process.ExitCode -eq 3010) {
        Write-Warning 'A reboot is required to complete the installation.'
    }
}
