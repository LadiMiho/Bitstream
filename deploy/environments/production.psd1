<#
    Environment definition — Production.

    Differs from uat.psd1 only in values. TR-ARC-08 forbids manual configuration of production,
    so anything that has to be true of the production site belongs in this file and is applied
    by New-BitstreamSite.ps1 — not typed into IIS Manager once and forgotten.
#>
@{
    Name = 'Production'

    # Two IIS sites: the portal people sign in to, and the integration host CRM posts to.
    # They are separate applications so they can be firewalled, scaled and restarted apart —
    # only the API host needs to be reachable from CRM, and only it runs the background jobs
    # that call CRM outbound (see AddBitstreamBackgroundJobs).
    Sites = @{
        Web = @{
            Name            = 'BitstreamPortal-PROD'
            PhysicalPath    = 'D:\Sites\BitstreamPortal-PROD'
            AppPoolName     = 'BitstreamPortal-PROD'
            AppPoolIdentity = 'CORP\svc_bitstream_prod'
            LogPath         = 'D:\Logs\BitstreamPortal-PROD'
            Bindings = @(
                @{ Protocol = 'https'; HostHeader = 'bitstream.example.com'; Port = 443; CertificateThumbprint = 'REPLACE_WITH_PROD_CERT_THUMBPRINT' }
                # TR-SEC-26: plain HTTP exists only to redirect.
                @{ Protocol = 'http';  HostHeader = 'bitstream.example.com'; Port = 80 }
            )
        }

        Api = @{
            Name            = 'BitstreamApi-PROD'
            PhysicalPath    = 'D:\Sites\BitstreamApi-PROD'
            AppPoolName     = 'BitstreamApi-PROD'
            AppPoolIdentity = 'CORP\svc_bitstream_prod'
            LogPath         = 'D:\Logs\BitstreamApi-PROD'
            Bindings = @(
                # No plain-HTTP binding: CRM is a machine caller and has no redirect to follow,
                # so an unencrypted listener here would only ever be a mistake (TR-SEC-26).
                @{ Protocol = 'https'; HostHeader = 'crm-bitstream.example.com'; Port = 443; CertificateThumbprint = 'REPLACE_WITH_PROD_CERT_THUMBPRINT' }
            )
        }
    }

    Application = @{
        Environment     = 'Production'
        RequiredSecrets = @('CrmClientSecret', 'SmtpPassword', 'TotpEncryptionKey')
    }

    Database = @{
        ServerInstance = 'SQLPROD01'
        Name           = 'BitstreamPortal'
        AppUser        = 'CORP\svc_bitstream_prod'
        SchemaVersion  = 4
    }
}
