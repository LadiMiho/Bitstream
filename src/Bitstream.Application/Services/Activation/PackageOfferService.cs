using System.Globalization;
using System.Text.Json;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;

namespace Bitstream.Application.Services.Activation;

/// <summary>A package offer change that breaks a rule; field-keyed for the drawer (TR-NFR-12).</summary>
public sealed class PackageOfferValidationException : Exception
{
    public PackageOfferValidationException(IReadOnlyDictionary<string, IReadOnlyList<string>> fieldErrors)
        : base(string.Join(" ", fieldErrors.Values.SelectMany(messages => messages))) =>
        FieldErrors = fieldErrors;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors { get; }
}

/// <summary>No offer exists for the package + duration pair. The presentation layer maps this to 404.</summary>
public sealed class PackageOfferNotFoundException : Exception
{
    public PackageOfferNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>Implements <see cref="IPackageOfferService"/> over <see cref="IActivationCatalogueRepository"/>.</summary>
public sealed class PackageOfferService : IPackageOfferService
{
    private const int MaxOfferCodeLength = 50;

    private readonly IActivationCatalogueRepository _catalogueRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;

    public PackageOfferService(IActivationCatalogueRepository catalogueRepository, IUnitOfWork unitOfWork, IAuditWriter auditWriter)
    {
        _catalogueRepository = catalogueRepository;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
    }

    public async Task<PackageOfferOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var packages = await _catalogueRepository.GetPackagesAsync(cancellationToken).ConfigureAwait(false);
        var durations = await _catalogueRepository.GetContractDurationsAsync(cancellationToken).ConfigureAwait(false);
        var offers = await _catalogueRepository.GetPackageOffersAsync(cancellationToken).ConfigureAwait(false);

        return new PackageOfferOverview(packages, durations, offers);
    }

    public Task<PackageOffer?> GetAsync(string packageCode, int contractDurationMonths, CancellationToken cancellationToken = default) =>
        _catalogueRepository.FindPackageOfferAsync(packageCode, contractDurationMonths, cancellationToken);

    public async Task<PackageOffer> CreateAsync(CreatePackageOfferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, IReadOnlyList<string>>();
        var offerCode = (request.OfferCode ?? string.Empty).Trim();

        var packages = await _catalogueRepository.GetPackagesAsync(cancellationToken).ConfigureAwait(false);
        if (!packages.Any(p => string.Equals(p.Code, request.PackageCode, StringComparison.Ordinal)))
        {
            errors["packageCode"] = ["Select a package."];
        }

        var durations = await _catalogueRepository.GetContractDurationsAsync(cancellationToken).ConfigureAwait(false);
        if (!durations.Any(d => d.Months == request.ContractDurationMonths))
        {
            errors["contractDurationMonths"] = ["Select a contract duration."];
        }

        if (errors.Count == 0
            && await _catalogueRepository.FindPackageOfferAsync(request.PackageCode, request.ContractDurationMonths, cancellationToken).ConfigureAwait(false) is not null)
        {
            errors["contractDurationMonths"] = ["This package already has a code for this contract duration. Edit that offer instead."];
        }

        await ValidateOfferCodeAsync(offerCode, null, errors, cancellationToken).ConfigureAwait(false);

        if (errors.Count > 0)
        {
            throw new PackageOfferValidationException(errors);
        }

        var offer = new PackageOffer
        {
            PackageCode = request.PackageCode,
            ContractDurationMonths = request.ContractDurationMonths,
            OfferCode = offerCode,
            IsActive = true
        };

        await _catalogueRepository.AddPackageOfferAsync(offer, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "PackageOffer.Created", "PackageOffer", Key(offer), null, Describe(offer), cancellationToken).ConfigureAwait(false);

        return offer;
    }

    public async Task<PackageOffer> UpdateAsync(
        string packageCode, int contractDurationMonths, UpdatePackageOfferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var offer = await RequireAsync(packageCode, contractDurationMonths, cancellationToken).ConfigureAwait(false);
        var offerCode = (request.OfferCode ?? string.Empty).Trim();

        var errors = new Dictionary<string, IReadOnlyList<string>>();
        await ValidateOfferCodeAsync(offerCode, offer, errors, cancellationToken).ConfigureAwait(false);

        if (errors.Count > 0)
        {
            throw new PackageOfferValidationException(errors);
        }

        var previous = Describe(offer);
        offer.OfferCode = offerCode;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "PackageOffer.Updated", "PackageOffer", Key(offer), previous, Describe(offer), cancellationToken).ConfigureAwait(false);

        return offer;
    }

    public async Task SetActiveAsync(string packageCode, int contractDurationMonths, bool isActive, CancellationToken cancellationToken = default)
    {
        var offer = await RequireAsync(packageCode, contractDurationMonths, cancellationToken).ConfigureAwait(false);

        if (offer.IsActive == isActive)
        {
            return;
        }

        var previous = Describe(offer);
        offer.IsActive = isActive;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            isActive ? "PackageOffer.Activated" : "PackageOffer.Deactivated", "PackageOffer", Key(offer), previous, Describe(offer),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateOfferCodeAsync(
        string offerCode, PackageOffer? current, Dictionary<string, IReadOnlyList<string>> errors, CancellationToken cancellationToken)
    {
        if (offerCode.Length == 0)
        {
            errors["offerCode"] = ["Code is required."];
            return;
        }

        if (offerCode.Length > MaxOfferCodeLength)
        {
            errors["offerCode"] = [$"Code must be at most {MaxOfferCodeLength} characters."];
            return;
        }

        var holder = await _catalogueRepository.FindPackageOfferByCodeAsync(offerCode, cancellationToken).ConfigureAwait(false);

        if (holder is not null && !ReferenceEquals(holder, current)
            && !(current is not null && holder.PackageCode == current.PackageCode && holder.ContractDurationMonths == current.ContractDurationMonths))
        {
            errors["offerCode"] = [$"Code '{offerCode}' is already used by {holder.PackageCode} with {holder.ContractDurationMonths} months."];
        }
    }

    private async Task<PackageOffer> RequireAsync(string packageCode, int contractDurationMonths, CancellationToken cancellationToken) =>
        await _catalogueRepository.FindPackageOfferAsync(packageCode, contractDurationMonths, cancellationToken).ConfigureAwait(false)
        ?? throw new PackageOfferNotFoundException($"No package offer for {packageCode} with {contractDurationMonths} months.");

    private static string Key(PackageOffer offer) =>
        $"{offer.PackageCode}/{offer.ContractDurationMonths.ToString(CultureInfo.InvariantCulture)}";

    private static string Describe(PackageOffer offer) =>
        $"{{\"offerCode\":{JsonSerializer.Serialize(offer.OfferCode)},\"isActive\":{(offer.IsActive ? "true" : "false")}}}";
}
