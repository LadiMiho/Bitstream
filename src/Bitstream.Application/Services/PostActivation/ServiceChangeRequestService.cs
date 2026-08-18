using System.Globalization;
using System.Text.Json;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>Thrown for a validated business rule violation. The presentation layer maps this to 400/422.</summary>
public sealed class ServiceChangeValidationException : Exception
{
    public ServiceChangeValidationException(string message)
        : base(message)
    {
    }

    public ServiceChangeValidationException(IReadOnlyList<string> violations)
        : base(string.Join(" ", violations)) =>
        Violations = violations;

    public IReadOnlyList<string> Violations { get; } = [];
}

/// <summary>
/// Implements <see cref="IServiceChangeRequestService"/>: TRD 6.8. Eligible upgrade/downgrade
/// targets are read from <see cref="CatalogueOptions.Packages"/>' <c>Tier</c> — an upgrade is any
/// active package ranked above the line's current one, a downgrade any ranked below (TR-PAS-35);
/// a termination takes neither a target package nor anything from the catalogue, only a date
/// (TR-PAS-36).
/// </summary>
public sealed class ServiceChangeRequestService : IServiceChangeRequestService
{
    private readonly IServiceChangeRequestRepository _requestRepository;
    private readonly IActiveLineRepository _lineRepository;
    private readonly IPublicIdentifierGenerator _identifierGenerator;
    private readonly IIntegrationOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;
    private readonly ICurrentUserContext _currentUser;
    private readonly IOptionsMonitor<CatalogueOptions> _catalogueOptions;

    public ServiceChangeRequestService(
        IServiceChangeRequestRepository requestRepository,
        IActiveLineRepository lineRepository,
        IPublicIdentifierGenerator identifierGenerator,
        IIntegrationOutbox outbox,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        ICurrentUserContext currentUser,
        IOptionsMonitor<CatalogueOptions> catalogueOptions)
    {
        _requestRepository = requestRepository;
        _lineRepository = lineRepository;
        _identifierGenerator = identifierGenerator;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
        _currentUser = currentUser;
        _catalogueOptions = catalogueOptions;
    }

    public async Task<IReadOnlyList<string>> GetEligibleTargetPackagesAsync(
        long lineId, ServiceChangeType changeType, CancellationToken cancellationToken = default)
    {
        if (changeType == ServiceChangeType.Termination)
        {
            return [];
        }

        var line = await _lineRepository.FindByIdAsync(lineId, cancellationToken).ConfigureAwait(false);

        if (line is null)
        {
            return [];
        }

        return GetEligibleTargetPackages(line.PackageCode, changeType);
    }

    private IReadOnlyList<string> GetEligibleTargetPackages(string currentPackageCode, ServiceChangeType changeType)
    {
        var catalogue = _catalogueOptions.CurrentValue;
        var current = catalogue.Packages.FirstOrDefault(p => string.Equals(p.Code, currentPackageCode, StringComparison.Ordinal));

        if (current is null)
        {
            return [];
        }

        // TR-PAS-35: excludes the current package by construction — strictly greater or lesser,
        // never equal.
        return changeType switch
        {
            ServiceChangeType.Upgrade => [.. catalogue.Packages
                .Where(p => p.Active && p.Tier > current.Tier)
                .OrderBy(p => p.Tier)
                .Select(p => p.Code)],
            ServiceChangeType.Downgrade => [.. catalogue.Packages
                .Where(p => p.Active && p.Tier < current.Tier)
                .OrderByDescending(p => p.Tier)
                .Select(p => p.Code)],
            _ => []
        };
    }

    public async Task<ServiceChangeRequest> SubmitAsync(
        long lineId,
        ServiceChangeType changeType,
        string? packageToBe,
        DateOnly? requestedTerminationDate,
        CancellationToken cancellationToken = default)
    {
        var violations = new List<string>();
        var line = await _lineRepository.FindByIdAsync(lineId, cancellationToken).ConfigureAwait(false);

        if (line is null)
        {
            violations.Add($"Line {lineId} does not exist.");
        }
        else if (_currentUser.IspId is { } callerIspId && callerIspId != line.IspId)
        {
            violations.Add("You may only request a service change for your own ISP's lines.");
        }

        if (changeType == ServiceChangeType.Termination)
        {
            // TR-PAS-36: a termination is a date, never a target package (TR-PAS-34's read-only
            // as-is/to-be pair does not apply to it).
            if (packageToBe is not null)
            {
                violations.Add("A termination request must not specify a target package.");
            }

            if (requestedTerminationDate is null)
            {
                violations.Add("A requested termination date is required.");
            }
            else if (requestedTerminationDate < DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime))
            {
                violations.Add("Requested termination date must not be in the past.");
            }
        }
        else
        {
            if (requestedTerminationDate is not null)
            {
                violations.Add($"A termination date must not be specified for a {changeType}.");
            }

            if (string.IsNullOrWhiteSpace(packageToBe))
            {
                violations.Add($"A target package is required for a {changeType}.");
            }
            else if (line is not null && !GetEligibleTargetPackages(line.PackageCode, changeType).Contains(packageToBe, StringComparer.Ordinal))
            {
                violations.Add($"'{packageToBe}' is not an eligible {changeType} target for this line's current package.");
            }
        }

        if (violations.Count > 0)
        {
            throw new ServiceChangeValidationException(violations);
        }

        var now = _clock.UtcNow;
        var publicId = await _identifierGenerator.NextAsync(IdentifierSeries.ServiceChangeRequest, cancellationToken).ConfigureAwait(false);

        var request = new ServiceChangeRequest
        {
            PublicId = publicId,
            LineId = lineId,
            Line = line!,
            ChangeType = changeType,
            PackageAsIs = line!.PackageCode,
            PackageToBe = packageToBe,
            RequestedTerminationDate = requestedTerminationDate,
            Status = "Requested",
            CreatedAt = now,
            CreatedBy = _currentUser.UserId
        };

        await _requestRepository.AddAsync(request, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var envelope = new IntegrationEnvelope(Guid.NewGuid(), _currentUser.CorrelationId, publicId, now);
        var command = new ServiceChangeCommand(
            envelope, publicId, line.ContractId, changeType.ToString(), line.PackageCode, packageToBe, requestedTerminationDate);

        await _outbox.EnqueueOutboundAsync(
            TargetSystem.Crm, "INT-CRM-09", "SERVICE_CHANGE", publicId,
            JsonSerializer.Serialize(command), _currentUser.CorrelationId, publicId, cancellationToken)
            .ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "ServiceChangeRequest.Submitted", "ServiceChangeRequest", request.ChangeId.ToString(CultureInfo.InvariantCulture),
            null, $"{{\"publicId\":{JsonSerializer.Serialize(publicId)},\"changeType\":\"{changeType}\",\"lineId\":{lineId}}}",
            cancellationToken).ConfigureAwait(false);

        return request;
    }
}
