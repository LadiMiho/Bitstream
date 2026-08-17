using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Api.Tests.Activation;

/*
 * Hand-written test doubles for the activation request module's ports, matching the style
 * already used in tests/Bitstream.Api.Tests/Identity/Fakes.cs rather than adding a mocking
 * framework.
 */

public sealed class FakeActivationRequestRepository : IActivationRequestRepository
{
    public Dictionary<long, ActivationRequest> Requests { get; } = [];

    private long _nextId = 1;

    public Task<ActivationRequest?> FindByIdAsync(long requestId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Requests.GetValueOrDefault(requestId));

    public Task<ActivationRequest?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Requests.Values.FirstOrDefault(request => request.PublicId == publicId));

    public Task AddAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestId == 0)
        {
            request.RequestId = _nextId++;
        }

        Requests[request.RequestId] = request;
        return Task.CompletedTask;
    }
}

/// <summary>Deterministic, in-memory stand-in for <see cref="IPublicIdentifierGenerator"/> — no stored procedure, no database.</summary>
public sealed class FakePublicIdentifierGenerator : IPublicIdentifierGenerator
{
    private long _next = 1;

    public string Prefix { get; set; } = "ISP";

    public Task<string> NextAsync(IdentifierSeries series, CancellationToken cancellationToken = default) =>
        Task.FromResult($"{Prefix}_{_next++}");

    public bool IsValid(string identifier) =>
        System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[A-Z]+_[0-9]+$");
}

/// <summary>Records every enqueued message, for assertions, instead of persisting or dispatching anything.</summary>
public sealed class FakeIntegrationOutbox : IIntegrationOutbox
{
    public List<IntegrationMessage> Outbound { get; } = [];

    private long _nextId = 1;

    public Task<long> EnqueueOutboundAsync(
        TargetSystem targetSystem,
        string interfaceCode,
        string messageType,
        string idempotencyKey,
        string payload,
        string correlationId,
        string? relatedPublicId,
        CancellationToken cancellationToken = default)
    {
        var message = new IntegrationMessage
        {
            MessageId = _nextId++,
            Direction = IntegrationDirection.Outbound,
            TargetSystem = targetSystem,
            InterfaceCode = interfaceCode,
            MessageType = messageType,
            Payload = payload,
            IdempotencyKey = idempotencyKey,
            RelatedPublicId = relatedPublicId,
            CorrelationId = correlationId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Outbound.Add(message);
        return Task.FromResult(message.MessageId);
    }

    public Task<(IntegrationMessage Message, bool IsDuplicate)> RecordInboundAsync(
        TargetSystem sourceSystem,
        string interfaceCode,
        string messageType,
        string idempotencyKey,
        string rawPayload,
        string correlationId,
        string? relatedPublicId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by the activation request tests.");

    public Task<IReadOnlyList<IntegrationMessage>> ClaimDueOutboundAsync(int batchSize, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by the activation request tests.");

    public Task MarkSucceededAsync(long messageId, string? responsePayload, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by the activation request tests.");

    public Task MarkFailedAsync(long messageId, string error, bool retryable, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by the activation request tests.");

    public Task<IReadOnlyList<IntegrationMessage>> GetDeadLetteredAsync(TargetSystem? targetSystem, int skip, int take, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by the activation request tests.");

    public Task ReplayAsync(long messageId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by the activation request tests.");
}
