using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.Activation;

/// <summary>
/// Thrown for a validated business rule violation (unknown package, unparsable location, and so
/// on). The presentation layer maps this to 400/422; it is not an unexpected failure, so it
/// deliberately does not derive from an infrastructure exception type.
/// </summary>
public sealed class ActivationRequestValidationException : Exception
{
    public ActivationRequestValidationException(string message)
        : base(message)
    {
    }

    /// <param name="violations">One or more field-level messages (TR-NFR-12).</param>
    public ActivationRequestValidationException(IReadOnlyList<string> violations)
        : base(string.Join(" ", violations)) =>
        Violations = violations;

    public IReadOnlyList<string> Violations { get; } = [];
}

/// <summary>
/// Thrown when the requested change is not a permitted transition of the TRD 5.3 state machine
/// from the request's current status. The presentation layer maps this to 409 Conflict.
/// </summary>
public sealed class ActivationRequestConflictException : Exception
{
    public ActivationRequestConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when the referenced activation request does not exist. The presentation layer maps this to 404.</summary>
public sealed class ActivationRequestNotFoundException : Exception
{
    public ActivationRequestNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Implements <see cref="IActivationRequestService"/>: TRD 5, the activation request lifecycle
/// from submission to completion.
/// <para>
/// Every status change goes through <see cref="Transition"/>, which is the only place that
/// assigns <see cref="ActivationRequest.Status"/> — it always checks
/// <see cref="ActivationRequestTransitions.IsPermitted"/> first, so an invalid jump in the TRD
/// 5.3 table fails here rather than silently corrupting the record.
/// </para>
/// <para>
/// CRM is never called directly (TR-ARC-01, TR-ARC-03): <see cref="SubmitAsync"/> enqueues
/// INT-CRM-01 and INT-CRM-02 on <see cref="IIntegrationOutbox"/> and stops. Dispatching those
/// messages — and the transitions that follow from CRM's response (PendingCrmSync onward to
/// Completed) — is Phase 4's dispatcher and inbound event interpretation, not this service.
/// </para>
/// </summary>
public sealed partial class ActivationRequestService : IActivationRequestService
{
    private readonly IActivationRequestRepository _requestRepository;
    private readonly IIspRepository _ispRepository;
    private readonly IPublicIdentifierGenerator _identifierGenerator;
    private readonly IIntegrationOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;
    private readonly ICurrentUserContext _currentUser;
    private readonly IOptionsMonitor<CatalogueOptions> _catalogueOptions;

    public ActivationRequestService(
        IActivationRequestRepository requestRepository,
        IIspRepository ispRepository,
        IPublicIdentifierGenerator identifierGenerator,
        IIntegrationOutbox outbox,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        ICurrentUserContext currentUser,
        IOptionsMonitor<CatalogueOptions> catalogueOptions)
    {
        _requestRepository = requestRepository;
        _ispRepository = ispRepository;
        _identifierGenerator = identifierGenerator;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
        _currentUser = currentUser;
        _catalogueOptions = catalogueOptions;
    }

    public async Task<ActivationRequest> SubmitAsync(SubmitActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // An ISP user's IspId is fixed to their own; only an internal caller (no IspId of their
        // own, e.g. Administrator) may submit on another ISP's behalf.
        if (_currentUser.IspId is { } callerIspId && callerIspId != request.IspId)
        {
            throw new ActivationRequestValidationException("You may only submit activation requests for your own ISP.");
        }

        var catalogue = _catalogueOptions.CurrentValue;
        var violations = new List<string>();

        var isp = await _ispRepository.FindByIdAsync(request.IspId, cancellationToken).ConfigureAwait(false);

        if (isp is null)
        {
            violations.Add($"ISP {request.IspId} does not exist.");
        }
        else if (isp.Status != IspStatus.Active)
        {
            violations.Add("The ISP is locked and cannot submit activation requests.");
        }

        // TR-ACT-01: package code from the configured catalogue, active offers only.
        var package = catalogue.Packages.FirstOrDefault(p => string.Equals(p.Code, request.PackageCode, StringComparison.Ordinal));

        if (package is null)
        {
            violations.Add($"Package '{request.PackageCode}' is not in the configured catalogue.");
        }
        else if (!package.Active)
        {
            violations.Add($"Package '{request.PackageCode}' is no longer offered.");
        }

        // TR-ACT-02/03: location exactly as entered, parsed into normalised coordinates.
        var hasCoordinates = CoordinateParser.TryParse(request.LocationRaw, out var latitude, out var longitude);

        if (string.IsNullOrWhiteSpace(request.LocationRaw))
        {
            violations.Add("Location is required.");
        }
        else if (!hasCoordinates)
        {
            violations.Add("Location must be a map URL or a 'latitude,longitude' pair; it could not be parsed.");
        }

        // TR-ACT-04: classification from the configured list; defaults when not supplied.
        var classification = string.IsNullOrWhiteSpace(request.Classification)
            ? catalogue.DefaultClassification
            : request.Classification;

        if (catalogue.Classifications.Count > 0 && !catalogue.Classifications.Contains(classification, StringComparer.Ordinal))
        {
            violations.Add($"Classification '{classification}' is not in the configured list.");
        }

        // TRD 5.1: contract duration is one of the configured selectable values.
        if (catalogue.ContractDurationsMonths.Count > 0 && !catalogue.ContractDurationsMonths.Contains(request.ContractDurationMonths))
        {
            violations.Add(
                $"Contract duration {request.ContractDurationMonths} months is not offered. Configured durations: " +
                $"{string.Join(", ", catalogue.ContractDurationsMonths)}.");
        }

        // TR-ACT-05: free text, max 2000 characters, HTML stripped before it is stored or ever
        // reaches CRM.
        var comments = StripHtml(request.Comments);

        if (comments is { Length: > 2000 })
        {
            violations.Add("Comments must not exceed 2000 characters.");
        }

        if (violations.Count > 0)
        {
            throw new ActivationRequestValidationException(violations);
        }

        var now = _clock.UtcNow;

        // TR-DAT-01 / TR-ACT-06: the public identifier is issued and the record persisted with
        // status Submitted before any CRM call is even enqueued.
        var publicId = await _identifierGenerator.NextAsync(IdentifierSeries.ActivationRequest, cancellationToken).ConfigureAwait(false);

        var activationRequest = new ActivationRequest
        {
            PublicId = publicId,
            IspId = request.IspId,
            PackageCode = request.PackageCode,
            LocationRaw = request.LocationRaw,
            LocationLat = latitude,
            LocationLng = longitude,
            Classification = classification,
            ContractDurationMonths = request.ContractDurationMonths,
            Comments = comments,
            Status = ActivationRequestStatus.Submitted,
            CreatedAt = now,
            CreatedBy = _currentUser.UserId
        };

        await _requestRepository.AddAsync(activationRequest, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // TR-ARC-03: enqueued on the outbox, never called directly — a background dispatcher
        // takes it from here once Phase 4 wires the real CRM adapter (ICrmGateway, CrmHttpGateway).
        await EnqueueCrmSubmissionAsync(activationRequest, isp!, cancellationToken).ConfigureAwait(false);

        // Submitted -> PendingCrmSync (TRD 5.3): the messages are enqueued and the request is now
        // waiting on the dispatcher, not on anything the submitter still has to do.
        Transition(activationRequest, ActivationRequestStatus.PendingCrmSync, now);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "ActivationRequest.Submitted", "ActivationRequest", activationRequest.RequestId.ToString(CultureInfo.InvariantCulture),
            null,
            $"{{\"publicId\":{JsonSerializer.Serialize(publicId)},\"ispId\":{request.IspId},\"packageCode\":{JsonSerializer.Serialize(request.PackageCode)}}}",
            cancellationToken).ConfigureAwait(false);

        return activationRequest;
    }

    public async Task<ActivationRequest?> GetByPublicIdAsync(string publicId, CancellationToken cancellationToken = default)
    {
        var request = await _requestRepository.FindByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            return null;
        }

        // Same not-found-not-forbidden discipline as IAdministrationService (TR-SEC-18/19):
        // activation.read.all sees every ISP's requests; anyone else only their own, and a
        // mismatch is reported identically to "does not exist".
        if (!_currentUser.HasPermission(ActivationPermissionCodes.ActivationReadAll) && _currentUser.IspId != request.IspId)
        {
            await _auditWriter.WriteAsync(
                "Security.AccessDenied.CrossIsp", "ActivationRequest", request.RequestId.ToString(CultureInfo.InvariantCulture),
                null,
                $"{{\"callerIspId\":{(_currentUser.IspId is { } ispId ? ispId.ToString(CultureInfo.InvariantCulture) : "null")}}}",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        return request;
    }

    public async Task RecordGisOutcomeAsync(long requestId, bool lineAvailable, string? reason, CancellationToken cancellationToken = default)
    {
        var request = await _requestRepository.FindByIdAsync(requestId, cancellationToken).ConfigureAwait(false) ??
            throw new ActivationRequestNotFoundException($"Activation request {requestId} does not exist.");

        // TR-ACT-13: a "no line" outcome is recorded with a reason — an administrator closing
        // the loop must say why, since the ISP and the ISP's customer both need to be told.
        if (!lineAvailable && string.IsNullOrWhiteSpace(reason))
        {
            throw new ActivationRequestValidationException("A reason is required when no line is available.");
        }

        var target = lineAvailable ? ActivationRequestStatus.LineAvailable : ActivationRequestStatus.RejectedNoLine;
        var previousStatus = request.Status;

        if (!ActivationRequestTransitions.IsPermitted(previousStatus, target))
        {
            throw new ActivationRequestConflictException(
                $"Cannot record a GIS outcome for request {requestId}: it is in status '{previousStatus}', " +
                "not 'AwaitingGisVerification'.");
        }

        request.StatusReason = lineAvailable ? null : reason;
        var now = _clock.UtcNow;
        Transition(request, target, now);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "ActivationRequest.GisOutcomeRecorded", "ActivationRequest", requestId.ToString(CultureInfo.InvariantCulture),
            $"{{\"status\":\"{previousStatus}\"}}",
            $"{{\"status\":\"{target}\",\"lineAvailable\":{(lineAvailable ? "true" : "false")}}}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplySalesOrderAsync(string requestPublicId, string salesOrderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(salesOrderId);

        var request = await _requestRepository.FindByPublicIdAsync(requestPublicId, cancellationToken).ConfigureAwait(false) ??
            throw new ActivationRequestNotFoundException($"Activation request '{requestPublicId}' does not exist.");

        var previousStatus = request.Status;

        if (!ActivationRequestTransitions.IsPermitted(previousStatus, ActivationRequestStatus.SalesOrderOpened))
        {
            throw new ActivationRequestConflictException(
                $"Cannot apply a sales order to request '{requestPublicId}': it is in status '{previousStatus}', " +
                "not 'LineAvailable'.");
        }

        request.SalesOrderId = salesOrderId;
        Transition(request, ActivationRequestStatus.SalesOrderOpened, _clock.UtcNow);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "ActivationRequest.SalesOrderApplied", "ActivationRequest", request.RequestId.ToString(CultureInfo.InvariantCulture),
            $"{{\"status\":\"{previousStatus}\"}}",
            $"{{\"status\":\"SalesOrderOpened\",\"salesOrderId\":{JsonSerializer.Serialize(salesOrderId)}}}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The only place <see cref="ActivationRequest.Status"/> is assigned. Callers decide and
    /// validate the target status themselves (each has a different rule for what is being
    /// recorded alongside it — a reason, a sales order ID); this only stamps the timestamp that
    /// goes with every transition.
    /// </summary>
    private static void Transition(ActivationRequest request, ActivationRequestStatus to, DateTimeOffset now)
    {
        request.Status = to;
        request.LastUpdatedAt = now;
    }

    private async Task EnqueueCrmSubmissionAsync(ActivationRequest request, Isp isp, CancellationToken cancellationToken)
    {
        var envelope = new IntegrationEnvelope(Guid.NewGuid(), _currentUser.CorrelationId, request.PublicId, _clock.UtcNow);

        var customerCommand = new CreateCrmCustomerCommand(
            envelope, request.PublicId, isp.Name, isp.Nipt, isp.ContactPerson, isp.ContactEmail, isp.ContactMobile);

        await _outbox.EnqueueOutboundAsync(
            TargetSystem.Crm, "INT-CRM-01", "CREATE_CUSTOMER", request.PublicId,
            JsonSerializer.Serialize(customerCommand), _currentUser.CorrelationId, request.PublicId, cancellationToken)
            .ConfigureAwait(false);

        // BusinessPartner is not yet known: INT-CRM-01 has only just been enqueued, not
        // dispatched. Phase 4's dispatcher resolves it from the customer-creation response before
        // this message is sent to CRM; the port and its shape exist now so nothing else about
        // this method changes when that wiring lands.
        var ticketCommand = new CreateActivationTicketCommand(
            envelope, request.PublicId, string.Empty, request.Classification, request.PackageCode,
            request.ContractDurationMonths, request.LocationRaw, request.LocationLat, request.LocationLng, request.Comments);

        await _outbox.EnqueueOutboundAsync(
            TargetSystem.Crm, "INT-CRM-02", "CREATE_ACTIVATION_TICKET", request.PublicId,
            JsonSerializer.Serialize(ticketCommand), _currentUser.CorrelationId, request.PublicId, cancellationToken)
            .ConfigureAwait(false);
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagPattern();

    private static string? StripHtml(string? value) =>
        string.IsNullOrEmpty(value) ? value : HtmlTagPattern().Replace(value, string.Empty).Trim();
}
