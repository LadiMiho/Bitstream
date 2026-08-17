using Bitstream.Application.Abstractions.Integration;

namespace Bitstream.Infrastructure.Integration.Crm;

/// <summary>
/// Configuration of the CRM adapter. Endpoints and timeouts are externalised (TR-ARC-06);
/// credentials are resolved from the secret store and never from a settings file (TR-SEC-28).
/// </summary>
public sealed class CrmOptions
{
    public const string SectionName = "Integration:Crm";

    /// <summary>Base address of the CRM service. Value per environment (TR-ARC-07).</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Per-call timeout; a timeout must never leave a record indeterminate (TR-INT-08).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Retry budget for technical failures (TR-INT-04).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Total window over which the attempts are spread.</summary>
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Name of the secret-store entry holding the CRM credential.</summary>
    public string? CredentialSecretName { get; set; }

    /// <summary>Client certificate thumbprint, when the agreed method is mutual TLS.</summary>
    public string? ClientCertificateThumbprint { get; set; }

    /// <summary>
    /// Path probed by the health check, relative to <see cref="BaseAddress"/>. Configurable
    /// because the path is CRM's to choose and is not yet agreed (TR-ARC-05, TR-ARC-06).
    /// </summary>
    public string? HealthPath { get; set; }

    /// <summary>Timeout for the health probe. Short, so readiness stays responsive.</summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// HTTP adapter for CRM (TRD 7.1 INT-CRM-01, -02, -04, -06, -08, -09; TRD 7.3.1 Direction A).
/// <para>
/// BLOCKED — TRD 11.4 open item 1. The CRM-side endpoint, authentication method, field
/// mapping and error semantics have not been supplied, so no call can be implemented and no
/// mapping table can be written. The class exists so that the port is wired, the retry and
/// idempotency behaviour has a home, and a stub or contract test double can be substituted
/// in Development and UAT.
/// </para>
/// <para>
/// When the contract arrives: implement each method here, add the mapping table to
/// docs/integration/crm-direction-a-mapping.md and have both teams sign it off (TR-INT-21).
/// Nothing outside this class changes.
/// </para>
/// </summary>
public sealed class CrmHttpGateway : ICrmGateway
{
    private const string PendingContract =
        "CRM Direction A contract is not yet available (TRD 11.4 open item 1). " +
        "Configure a stub gateway in non-production environments.";

    /// <summary>Typed client supplied by <c>AddHttpClient</c>; used once the contract is available.</summary>
    private HttpClient Client { get; }

    public CrmHttpGateway(HttpClient httpClient) => Client = httpClient;

    public Task<IntegrationResult<CreateCrmCustomerResult>> CreateCustomerAsync(
        CreateCrmCustomerCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);

    public Task<IntegrationResult<CreateCrmTicketResult>> CreateActivationTicketAsync(
        CreateActivationTicketCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);

    public Task<IntegrationResult<CreateCrmTicketResult>> CreateComplaintTicketAsync(
        CreateComplaintTicketCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);

    public Task<IntegrationResult<ReplicateCommentResult>> ReplicateCommentAsync(
        ReplicateCommentCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);

    public Task<IntegrationResult<ClosureDecisionResult>> SubmitClosureDecisionAsync(
        ClosureDecisionCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);

    public Task<IntegrationResult<ServiceChangeResult>> SubmitServiceChangeAsync(
        ServiceChangeCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);

    public Task<IntegrationResult<CreateCrmTicketResult>> FindTicketByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingContract);
}
