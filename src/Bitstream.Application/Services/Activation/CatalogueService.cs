using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;

namespace Bitstream.Application.Services.Activation;

/// <summary>A package or contract duration change that breaks a rule; field-keyed for the drawer (TR-NFR-12).</summary>
public sealed class CatalogueValidationException : Exception
{
    public CatalogueValidationException(IReadOnlyDictionary<string, IReadOnlyList<string>> fieldErrors)
        : base(string.Join(" ", fieldErrors.Values.SelectMany(messages => messages))) =>
        FieldErrors = fieldErrors;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors { get; }
}

/// <summary>No package or contract duration with that key. The presentation layer maps this to 404.</summary>
public sealed class CatalogueNotFoundException : Exception
{
    public CatalogueNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>Implements <see cref="ICatalogueService"/> over <see cref="IActivationCatalogueRepository"/>.</summary>
public sealed partial class CatalogueService : ICatalogueService
{
    private const int MaxPackageNameLength = 200;
    private const int MaxDurationLabelLength = 50;
    private const int MinMonths = 1;
    private const int MaxMonths = 120;

    private readonly IActivationCatalogueRepository _catalogueRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;

    public CatalogueService(IActivationCatalogueRepository catalogueRepository, IUnitOfWork unitOfWork, IAuditWriter auditWriter)
    {
        _catalogueRepository = catalogueRepository;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
    }

    [GeneratedRegex("^[A-Z0-9_]{1,50}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageCodePattern();

    public async Task<CatalogueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var packages = await _catalogueRepository.GetPackagesAsync(cancellationToken).ConfigureAwait(false);
        var durations = await _catalogueRepository.GetContractDurationsAsync(cancellationToken).ConfigureAwait(false);

        return new CatalogueOverview(packages, durations);
    }

    // --- Packages -----------------------------------------------------------------------------

    public Task<Package?> GetPackageAsync(string code, CancellationToken cancellationToken = default) =>
        _catalogueRepository.FindPackageAsync(code, cancellationToken);

    public async Task<Package> CreatePackageAsync(string code, string name, int tier, CancellationToken cancellationToken = default)
    {
        var normalisedCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        var trimmedName = (name ?? string.Empty).Trim();
        var errors = new Dictionary<string, IReadOnlyList<string>>();

        if (normalisedCode.Length == 0)
        {
            errors["code"] = ["Code is required."];
        }
        else if (!PackageCodePattern().IsMatch(normalisedCode))
        {
            errors["code"] = ["Code may only contain letters, digits and underscores (max 50), e.g. BITSTREAM_STD."];
        }
        else if (await _catalogueRepository.FindPackageAsync(normalisedCode, cancellationToken).ConfigureAwait(false) is not null)
        {
            errors["code"] = [$"A package with code '{normalisedCode}' already exists."];
        }

        ValidatePackageFields(trimmedName, tier, errors);
        ThrowIfAny(errors);

        var package = new Package { Code = normalisedCode, Name = trimmedName, Tier = tier, IsActive = true };

        await _catalogueRepository.AddPackageAsync(package, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync("Package.Created", "Package", package.Code, null, Describe(package), cancellationToken).ConfigureAwait(false);

        return package;
    }

    public async Task<Package> UpdatePackageAsync(string code, string name, int tier, CancellationToken cancellationToken = default)
    {
        var package = await RequirePackageAsync(code, cancellationToken).ConfigureAwait(false);
        var trimmedName = (name ?? string.Empty).Trim();
        var errors = new Dictionary<string, IReadOnlyList<string>>();

        ValidatePackageFields(trimmedName, tier, errors);
        ThrowIfAny(errors);

        var previous = Describe(package);
        package.Name = trimmedName;
        package.Tier = tier;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync("Package.Updated", "Package", package.Code, previous, Describe(package), cancellationToken).ConfigureAwait(false);

        return package;
    }

    public async Task SetPackageActiveAsync(string code, bool isActive, CancellationToken cancellationToken = default)
    {
        var package = await RequirePackageAsync(code, cancellationToken).ConfigureAwait(false);

        if (package.IsActive == isActive)
        {
            return;
        }

        var previous = Describe(package);
        package.IsActive = isActive;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync(
            isActive ? "Package.Activated" : "Package.Deactivated", "Package", package.Code, previous, Describe(package),
            cancellationToken).ConfigureAwait(false);
    }

    // --- Contract durations -------------------------------------------------------------------

    public Task<ContractDuration?> GetContractDurationAsync(int months, CancellationToken cancellationToken = default) =>
        _catalogueRepository.FindContractDurationAsync(months, cancellationToken);

    public async Task<ContractDuration> CreateContractDurationAsync(int months, string? label, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, IReadOnlyList<string>>();

        if (months is < MinMonths or > MaxMonths)
        {
            errors["months"] = [$"Months must be between {MinMonths} and {MaxMonths}."];
        }
        else if (await _catalogueRepository.FindContractDurationAsync(months, cancellationToken).ConfigureAwait(false) is not null)
        {
            errors["months"] = [$"A {months}-month contract duration already exists."];
        }

        var resolvedLabel = ResolveLabel(months, label, errors);
        ThrowIfAny(errors);

        var duration = new ContractDuration { Months = months, Label = resolvedLabel, IsActive = true };

        await _catalogueRepository.AddContractDurationAsync(duration, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync(
            "ContractDuration.Created", "ContractDuration", Key(duration), null, Describe(duration), cancellationToken).ConfigureAwait(false);

        return duration;
    }

    public async Task<ContractDuration> UpdateContractDurationAsync(int months, string? label, CancellationToken cancellationToken = default)
    {
        var duration = await RequireDurationAsync(months, cancellationToken).ConfigureAwait(false);
        var errors = new Dictionary<string, IReadOnlyList<string>>();

        var resolvedLabel = ResolveLabel(months, label, errors);
        ThrowIfAny(errors);

        var previous = Describe(duration);
        duration.Label = resolvedLabel;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync(
            "ContractDuration.Updated", "ContractDuration", Key(duration), previous, Describe(duration), cancellationToken).ConfigureAwait(false);

        return duration;
    }

    public async Task SetContractDurationActiveAsync(int months, bool isActive, CancellationToken cancellationToken = default)
    {
        var duration = await RequireDurationAsync(months, cancellationToken).ConfigureAwait(false);

        if (duration.IsActive == isActive)
        {
            return;
        }

        var previous = Describe(duration);
        duration.IsActive = isActive;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync(
            isActive ? "ContractDuration.Activated" : "ContractDuration.Deactivated", "ContractDuration", Key(duration), previous,
            Describe(duration), cancellationToken).ConfigureAwait(false);
    }

    // --- Helpers ------------------------------------------------------------------------------

    private static void ValidatePackageFields(string name, int tier, Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (name.Length == 0)
        {
            errors["name"] = ["Name is required."];
        }
        else if (name.Length > MaxPackageNameLength)
        {
            errors["name"] = [$"Name must be at most {MaxPackageNameLength} characters."];
        }

        if (tier < 0)
        {
            errors["tier"] = ["Tier must be 0 or greater."];
        }
    }

    private static string ResolveLabel(int months, string? label, Dictionary<string, IReadOnlyList<string>> errors)
    {
        var trimmed = (label ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return $"{months.ToString(CultureInfo.InvariantCulture)} months";
        }

        if (trimmed.Length > MaxDurationLabelLength)
        {
            errors["label"] = [$"Label must be at most {MaxDurationLabelLength} characters."];
        }

        return trimmed;
    }

    private static void ThrowIfAny(Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (errors.Count > 0)
        {
            throw new CatalogueValidationException(errors);
        }
    }

    private async Task<Package> RequirePackageAsync(string code, CancellationToken cancellationToken) =>
        await _catalogueRepository.FindPackageAsync(code, cancellationToken).ConfigureAwait(false)
        ?? throw new CatalogueNotFoundException($"No package with code '{code}'.");

    private async Task<ContractDuration> RequireDurationAsync(int months, CancellationToken cancellationToken) =>
        await _catalogueRepository.FindContractDurationAsync(months, cancellationToken).ConfigureAwait(false)
        ?? throw new CatalogueNotFoundException($"No {months}-month contract duration.");

    private static string Key(ContractDuration duration) => duration.Months.ToString(CultureInfo.InvariantCulture);

    private static string Describe(Package package) =>
        $"{{\"name\":{JsonSerializer.Serialize(package.Name)},\"tier\":{package.Tier.ToString(CultureInfo.InvariantCulture)},\"isActive\":{(package.IsActive ? "true" : "false")}}}";

    private static string Describe(ContractDuration duration) =>
        $"{{\"label\":{JsonSerializer.Serialize(duration.Label)},\"isActive\":{(duration.IsActive ? "true" : "false")}}}";
}
