<#
    Environment definition — UAT.

    TR-ARC-07 requires Development, UAT and Production to have isolated data and credentials.
    That isolation is expressed here: one file per environment, differing only in values, so
    that "what is different about production" is a diff rather than a conversation.

    No secret appears in this file. Credentials are set on the application pool as environment
    variables by Set-AppPoolSecrets.ps1, which reads them from the operator's secret store at
    deployment time (TR-SEC-28).
#>
@{
    Name = 'UAT'

    # Two IIS sites: the portal people sign in to, and the integration host CRM posts to.
    # They are separate applications so they can be firewalled, scaled and restarted apart —
    # only the API host needs to be reachable from CRM, and only it runs the background jobs
    # that call CRM outbound (see AddBitstreamBackgroundJobs).
    Sites = @{
        Web = @{
            Name            = 'BitstreamPortal-UAT'
            PhysicalPath    = 'D:\Sites\BitstreamPortal-UAT'
            AppPoolName     = 'BitstreamPortal-UAT'
            AppPoolIdentity = 'CORP\svc_bitstream_uat'
            LogPath         = 'D:\Logs\BitstreamPortal-UAT'
            Bindings = @(
                @{ Protocol = 'https'; HostHeader = 'bitstream-uat.example.com'; Port = 443; CertificateThumbprint = 'REPLACE_WITH_UAT_CERT_THUMBPRINT' }
                # TR-SEC-26: plain HTTP exists only to redirect.
                @{ Protocol = 'http';  HostHeader = 'bitstream-uat.example.com'; Port = 80 }
            )
        }

        Api = @{
            Name            = 'BitstreamApi-UAT'
            PhysicalPath    = 'D:\Sites\BitstreamApi-UAT'
            AppPoolName     = 'BitstreamApi-UAT'
            AppPoolIdentity = 'CORP\svc_bitstream_uat'
            LogPath         = 'D:\Logs\BitstreamApi-UAT'
            Bindings = @(
                # No plain-HTTP binding: CRM is a machine caller and has no redirect to follow,
                # so an unencrypted listener here would only ever be a mistake (TR-SEC-26).
                @{ Protocol = 'https'; HostHeader = 'crm-bitstream-uat.example.com'; Port = 443; CertificateThumbprint = 'REPLACE_WITH_UAT_CERT_THUMBPRINT' }
            )
        }
    }

    Application = @{
        Environment = 'UAT'
        # Names of the secrets the application pool must carry. Values come from the secret
        # store, never from this file.
        RequiredSecrets = @('CrmClientSecret', 'SmtpPassword', 'TotpEncryptionKey')
    }

    Database = @{
        ServerInstance = 'SQLUAT01'
        Name           = 'BitstreamPortal'
        AppUser        = 'CORP\svc_bitstream_uat'
        SchemaVersion  = 4
    }
}
