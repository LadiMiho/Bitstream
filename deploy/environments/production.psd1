<#
    Environment definition — Production.

    Differs from uat.psd1 only in values. TR-ARC-08 forbids manual configuration of production,
    so anything that has to be true of the production site belongs in this file and is applied
    by New-BitstreamSite.ps1 — not typed into IIS Manager once and forgotten.
#>
@{
    Name = 'Production'

    Site = @{
        Name            = 'BitstreamPortal'
        PhysicalPath    = 'D:\Sites\BitstreamPortal'
        AppPoolName     = 'BitstreamPortal'
        AppPoolIdentity = 'CORP\svc_bitstream_prod'
        LogPath         = 'D:\Logs\BitstreamPortal'
    }

    Bindings = @(
        @{ Protocol = 'https'; HostHeader = 'bitstream.example.com'; Port = 443; CertificateThumbprint = 'REPLACE_WITH_PROD_CERT_THUMBPRINT' }
        @{ Protocol = 'http';  HostHeader = 'bitstream.example.com'; Port = 80 }
    )

    Application = @{
        Environment     = 'Production'
        RequiredSecrets = @('CrmClientSecret', 'SmtpPassword', 'TotpEncryptionKey')
    }

    Database = @{
        ServerInstance = 'SQLPROD01'
        Name           = 'BitstreamPortal'
        AppUser        = 'CORP\svc_bitstream_prod'
        SchemaVersion  = 2
    }
}
