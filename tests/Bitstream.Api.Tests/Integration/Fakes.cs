using System.Collections.Concurrent;
using Bitstream.Application.Abstractions.Integration;

namespace Bitstream.Api.Tests.Integration;

/// <summary>
/// In-process stand-in for CRM (TRD 11.4 open item 1), mirroring the same idempotent-by-key
/// behaviour as <c>tools/CrmSimulator</c> — same shape, same reasoning, but living in the test
/// process instead of a second Kestrel host, so the end-to-end test does not depend on spinning
/// up and tearing down a separate server.
/// </summary>
public sealed class FakeCrmGateway : ICrmGateway
{
    private readonly ConcurrentDictionary<string, CreateCrmCustomerResult> _customers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CreateCrmTicketResult> _tickets = new(StringComparer.Ordinal);
    private int _customerSequence;
    private int _ticketSequence;

    /// <summary>When set, the next <see cref="CreateCustomerAsync"/> call returns this instead of succeeding — for testing the failure/retry/dead-letter path.</summary>
    public IntegrationResult<CreateCrmCustomerResult>? NextCreateCustomerResult { get; set; }

    public List<CreateCrmCustomerCommand> CreateCustomerCalls { get; } = [];

    public List<CreateActivationTicketCommand> CreateActivationTicketCalls { get; } = [];

    public Task<IntegrationResult<CreateCrmCustomerResult>> CreateCustomerAsync(
        CreateCrmCustomerCommand command,
        CancellationToken cancellationToken = default)
    {
        CreateCustomerCalls.Add(command);

        if (NextCreateCustomerResult is { } forced)
        {
            NextCreateCustomerResult = null;
            return Task.FromResult(forced);
        }

        // TR-INT-03/17: the same idempotency key always gets back the same result — a retried
        // message never creates a second customer.
        var result = _customers.GetOrAdd(command.Envelope.IdempotencyKey, _ =>
        {
            var sequence = Interlocked.Increment(ref _customerSequence);
            return new CreateCrmCustomerResult($"CRMCUST-{sequence:D6}", $"BP-{sequence:D6}");
        });

        return Task.FromResult(IntegrationResult<CreateCrmCustomerResult>.Success(result));
    }

    public Task<IntegrationResult<CreateCrmTicketResult>> CreateActivationTicketAsync(
        CreateActivationTicketCommand command,
        CancellationToken cancellationToken = default)
    {
        CreateActivationTicketCalls.Add(command);

        var result = _tickets.GetOrAdd(command.Envelope.IdempotencyKey, _ =>
        {
            var sequence = Interlocked.Increment(ref _ticketSequence);
            return new CreateCrmTicketResult($"CRMTKT-{sequence:D6}");
        });

        return Task.FromResult(IntegrationResult<CreateCrmTicketResult>.Success(result));
    }

    public Task<IntegrationResult<CreateCrmTicketResult>> CreateComplaintTicketAsync(
        CreateComplaintTicketCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    public Task<IntegrationResult<ReplicateCommentResult>> ReplicateCommentAsync(
        ReplicateCommentCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    public Task<IntegrationResult<ClosureDecisionResult>> SubmitClosureDecisionAsync(
        ClosureDecisionCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    public Task<IntegrationResult<ServiceChangeResult>> SubmitServiceChangeAsync(
        ServiceChangeCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    public Task<IntegrationResult<CreateCrmTicketResult>> FindTicketByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by these tests.");
}
