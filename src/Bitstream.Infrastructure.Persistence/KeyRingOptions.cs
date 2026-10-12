namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// <c>DataProtection</c>: the ASP.NET Core Data Protection key ring, kept in the shared database
/// (ops.DataProtectionKeys) so every host and machine reads what another encrypted.
/// </summary>
public sealed class KeyRingOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>The same on every host that shares the database; keys are isolated per application name.</summary>
    public string ApplicationName { get; set; } = "Bitstream";

    /// <summary>
    /// Optional thumbprint of a certificate in LocalMachine\My (with its private key readable by
    /// the app pool identity). When set, the keys are also encrypted at rest in the database.
    /// </summary>
    public string? CertificateThumbprint { get; set; }
}
