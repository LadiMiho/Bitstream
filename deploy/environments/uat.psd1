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

    Site = @{
        Name          = 'BitstreamPortal-UAT'
        PhysicalPath  = 'D:\Sites\BitstreamPortal-UAT'
        AppPoolName   = 'BitstreamPortal-UAT'
        # Domain service account. Must match the AppUser granted rights by
        # db/mssql/0008_permissions.sql.
        AppPoolIdentity = 'CORP\svc_bitstream_uat'
        LogPath       = 'D:\Logs\BitstreamPortal-UAT'
    }

    Bindings = @(
        @{ Protocol = 'https'; HostHeader = 'bitstream-uat.example.com'; Port = 443; CertificateThumbprint = 'REPLACE_WITH_UAT_CERT_THUMBPRINT' }
        # TR-SEC-26: plain HTTP exists only to redirect; the application refuses it for API paths.
        @{ Protocol = 'http';  HostHeader = 'bitstream-uat.example.com'; Port = 80 }
    )

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
