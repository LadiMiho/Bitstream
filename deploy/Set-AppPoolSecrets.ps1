<#
.SYNOPSIS
    Sets the application pool's service account password and the application's secrets as
    application-pool environment variables.

.DESCRIPTION
    TR-SEC-28: credentials, tokens and integration secrets are held in a secret store and never
    in source code or configuration files in plain text. This script is the bridge between the
    operator's secret store and the running application pool.

    Secrets are read interactively or from the pipeline as SecureString, set on the pool as
    environment variables named BITSTREAM_Secrets__<Name>, and never written to disk. The
    application reads them through ISecretResolver, which refuses any secret that turns out to
    have come from a JSON file.

    The pool's environment variables are readable by an administrator on the server, which is
    the accepted boundary: the server's administrators are already trusted with the machine.
    What this prevents is a credential in source control, in a build artifact, or in a
    configuration file that gets copied between environments.

    Run elevated on the web server.

.PARAMETER Environment
    Environment name; loads deploy/environments/<name>.psd1 for the pool name and the list of
    required secrets.

.PARAMETER AppPoolPassword
    Password of the application pool service account.

.EXAMPLE
    .\Set-AppPoolSecrets.ps1 -Environment uat
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $Environment,
    [SecureString] $AppPoolPassword
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module WebAdministration -ErrorAction Stop

$config = Import-PowerShellDataFile -Path (Join-Path $PSScriptRoot "environments\$Environment.psd1")
$site = $config.Site
$appPoolPath = "IIS:\AppPools\$($site.AppPoolName)"

if (-not (Test-Path $appPoolPath)) {
    throw "Application pool $($site.AppPoolName) does not exist. Run New-BitstreamSite.ps1 first."
}

function ConvertFrom-SecureStringPlain {
    param([SecureString] $Secure)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        # Zero the unmanaged copy rather than leaving it for the garbage collector.
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

# --- Service account password ----------------------------------------------------------
if (-not $AppPoolPassword) {
    $AppPoolPassword = Read-Host -AsSecureString "Password for $($site.AppPoolIdentity)"
}

if ($PSCmdlet.ShouldProcess($site.AppPoolName, 'Set application pool identity password')) {
    Set-ItemProperty $appPoolPath -Name processModel.password -Value (ConvertFrom-SecureStringPlain $AppPoolPassword)
    Write-Host "Application pool identity password set for $($site.AppPoolIdentity)."
}

# --- Application secrets ----------------------------------------------------------------
$environmentVariables = @{}

foreach ($secretName in $config.Application.RequiredSecrets) {
    $secure = Read-Host -AsSecureString "Value for secret '$secretName' (blank to skip)"
    $value = ConvertFrom-SecureStringPlain $secure

    if ([string]::IsNullOrWhiteSpace($value)) {
        Write-Warning "  $secretName skipped. The feature that needs it will fail at the point of use."
        continue
    }

    # Double underscore is the configuration provider's section separator, so this lands at
    # Secrets:<Name> in the application's configuration.
    $environmentVariables["BITSTREAM_Secrets__$secretName"] = $value
}

$environmentVariables['ASPNETCORE_ENVIRONMENT'] = $config.Application.Environment

if ($PSCmdlet.ShouldProcess($site.AppPoolName, "Set $($environmentVariables.Count) environment variables")) {
    $collection = Get-ItemProperty $appPoolPath -Name environmentVariables

    foreach ($entry in $environmentVariables.GetEnumerator()) {
        $existing = $collection.Collection | Where-Object { $_.name -eq $entry.Key }

        if ($existing) {
            Set-WebConfigurationProperty `
                -PSPath 'MACHINE/WEBROOT/APPHOST' `
                -Filter "system.applicationHost/applicationPools/add[@name='$($site.AppPoolName)']/environmentVariables/add[@name='$($entry.Key)']" `
                -Name 'value' `
                -Value $entry.Value
        }
        else {
            Add-WebConfigurationProperty `
                -PSPath 'MACHINE/WEBROOT/APPHOST' `
                -Filter "system.applicationHost/applicationPools/add[@name='$($site.AppPoolName)']/environmentVariables" `
                -Name '.' `
                -Value @{ name = $entry.Key; value = $entry.Value }
        }

        # The name, never the value.
        Write-Host "  Set $($entry.Key)"
    }
}

Write-Host ''
Write-Host 'Secrets applied. Recycle the pool for them to take effect:' -ForegroundColor Green
Write-Host "  Restart-WebAppPool -Name $($site.AppPoolName)"
