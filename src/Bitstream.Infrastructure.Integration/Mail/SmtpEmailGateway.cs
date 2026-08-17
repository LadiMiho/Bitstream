using Bitstream.Application.Abstractions.Integration;

namespace Bitstream.Infrastructure.Integration.Mail;

/// <summary>Configuration of the SMTP adapter (TRD 8, TR-NTF-03 to TR-NTF-07).</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Integration:Smtp";

    public string? Host { get; set; }

    public int Port { get; set; } = 25;

    /// <summary>TLS is mandatory for all traffic (TR-SEC-26).</summary>
    public bool UseStartTls { get; set; } = true;

    public string? FromAddress { get; set; }

    public string? FromDisplayName { get; set; }

    /// <summary>Name of the secret-store entry holding the relay credential (TR-SEC-28).</summary>
    public string? CredentialSecretName { get; set; }

    /// <summary>Dispatch attempts before the notification is logged as failed (TR-NTF-04).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Non-production test mode: every message is redirected to
    /// <see cref="RedirectMailbox"/> regardless of its recipients (TR-NTF-07).
    /// </summary>
    public bool RedirectAllMail { get; set; }

    public string? RedirectMailbox { get; set; }

    /// <summary>
    /// Distribution groups resolved at dispatch time — Service Desk, FM, FM Contractor
    /// (TR-NTF-02). Membership is TRD 11.4 open item 6.
    /// </summary>
    public IDictionary<string, string[]> DistributionGroups { get; set; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Timeout for the health probe's TCP connect (TR-ARC-05).</summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// SMTP adapter (TRD 7.1 INT-MAIL-01). The only place in the solution that speaks SMTP.
/// <para>
/// Unimplemented at scaffold stage. The relay host is an operations input rather than an
/// open TRD item; the blocked parts are the recipient lists (open item 6) and the sales
/// order template sample (open item 7), both of which are configuration and content, not code.
/// </para>
/// </summary>
public sealed class SmtpEmailGateway : IEmailGateway
{
    public Task<IntegrationResult<bool>> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Scaffold stage: SMTP dispatch not implemented.");
}
