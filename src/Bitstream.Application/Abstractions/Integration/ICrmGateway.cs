namespace Bitstream.Application.Abstractions.Integration;

/// <summary>
/// System-agnostic port for every portal-initiated CRM interaction
/// (TRD 7.1 INT-CRM-01, -02, -04, -06, -08, -09).
/// <para>
/// TR-ARC-02: application services depend on this interface only. The concrete adapter
/// lives in Bitstream.Infrastructure.Integration and is the single place that knows the
/// CRM endpoint, authentication and field mapping — none of which are agreed yet
/// (TRD 11.4 open item 1).
/// </para>
/// <para>
/// Implementations must be idempotent on <see cref="IntegrationEnvelope.IdempotencyKey"/>
/// (TR-INT-03, TR-INT-17) and must never be called directly from an endpoint; calls are
/// dispatched from the outbox (TR-ARC-03, TR-INT-16).
/// </para>
/// </summary>
public interface ICrmGateway
{
    /// <summary>INT-CRM-01. Creates the customer mask in CRM and returns the Business Partner.</summary>
    Task<IntegrationResult<CreateCrmCustomerResult>> CreateCustomerAsync(
        CreateCrmCustomerCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>INT-CRM-02. Creates the activation ticket, carrying the BP and the portal identifier.</summary>
    Task<IntegrationResult<CreateCrmTicketResult>> CreateActivationTicketAsync(
        CreateActivationTicketCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>INT-CRM-04. Replicates a portal complaint ticket to CRM.</summary>
    Task<IntegrationResult<CreateCrmTicketResult>> CreateComplaintTicketAsync(
        CreateComplaintTicketCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>INT-CRM-06. Pushes a portal comment to the CRM ticket.</summary>
    Task<IntegrationResult<ReplicateCommentResult>> ReplicateCommentAsync(
        ReplicateCommentCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>INT-CRM-08. Transmits Confirm, No, or a system-initiated auto-confirmation.</summary>
    Task<IntegrationResult<ClosureDecisionResult>> SubmitClosureDecisionAsync(
        ClosureDecisionCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>INT-CRM-09. Transmits an upgrade, downgrade or termination.</summary>
    Task<IntegrationResult<ServiceChangeResult>> SubmitServiceChangeAsync(
        ServiceChangeCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Status query used after an ambiguous timeout, so that a retry is never a blind
    /// second create (TR-INT-20). Availability depends on the CRM contract (open item 1).
    /// </summary>
    Task<IntegrationResult<CreateCrmTicketResult>> FindTicketByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
