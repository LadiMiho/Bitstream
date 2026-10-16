using System.Globalization;
using System.Text.Json;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>A classification change that breaks a rule; field-keyed for the drawer (TR-NFR-12).</summary>
public sealed class SupportClassificationValidationException : Exception
{
    public SupportClassificationValidationException(IReadOnlyDictionary<string, IReadOnlyList<string>> fieldErrors)
        : base(string.Join(" ", fieldErrors.Values.SelectMany(messages => messages))) =>
        FieldErrors = fieldErrors;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors { get; }
}

/// <summary>No classification with that id. The presentation layer maps this to 404.</summary>
public sealed class SupportClassificationNotFoundException : Exception
{
    public SupportClassificationNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>One node of the tree with its children, already ordered.</summary>
public sealed record SupportClassificationNode(SupportClassification Classification, IReadOnlyList<SupportClassificationNode> Children);

public interface ISupportClassificationService
{
    /// <summary>All level-1 nodes with their level-2 and level-3 descendants, ordered by display order then name.</summary>
    Task<IReadOnlyList<SupportClassificationNode>> GetTreeAsync(CancellationToken cancellationToken = default);

    Task<SupportClassification?> GetAsync(long classificationId, CancellationToken cancellationToken = default);

    /// <summary>Adds a node under <paramref name="parentId"/>, or a level-1 node when it is null. The level follows from the parent.</summary>
    Task<SupportClassification> CreateAsync(long? parentId, string code, string name, int displayOrder, CancellationToken cancellationToken = default);

    /// <summary>Changes code, name and order. The parent and level never change after creation.</summary>
    Task<SupportClassification> UpdateAsync(long classificationId, string code, string name, int displayOrder, CancellationToken cancellationToken = default);

    /// <summary>
    /// An inactive node, and everything under it, will not be offered on the request form. Its
    /// children keep their own flag, so reactivating the parent brings them back as they were.
    /// </summary>
    Task SetActiveAsync(long classificationId, bool isActive, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements <see cref="ISupportClassificationService"/>: the three-level classification of
/// post-activation requests (portal.SupportClassification). Nothing is ever deleted — requests
/// will reference these codes — so a node is deactivated instead.
/// </summary>
public sealed class SupportClassificationService : ISupportClassificationService
{
    private const int MaxCodeLength = 20;
    private const int MaxNameLength = 200;
    private const string EntityType = "SupportClassification";

    private readonly ISupportClassificationRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;

    public SupportClassificationService(ISupportClassificationRepository repository, IUnitOfWork unitOfWork, IAuditWriter auditWriter, IClock clock)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SupportClassificationNode>> GetTreeAsync(CancellationToken cancellationToken = default)
    {
        var all = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var byParent = all.ToLookup(c => c.ParentId);

        IReadOnlyList<SupportClassificationNode> ChildrenOf(long? parentId) =>
        [
            .. byParent[parentId]
                .OrderBy(c => c.DisplayOrder)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new SupportClassificationNode(c, ChildrenOf(c.ClassificationId)))
        ];

        return ChildrenOf(null);
    }

    public Task<SupportClassification?> GetAsync(long classificationId, CancellationToken cancellationToken = default) =>
        _repository.FindByIdAsync(classificationId, cancellationToken);

    public async Task<SupportClassification> CreateAsync(long? parentId, string code, string name, int displayOrder, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, IReadOnlyList<string>>();
        SupportClassification? parent = null;

        if (parentId is { } id)
        {
            parent = await RequireAsync(id, cancellationToken).ConfigureAwait(false);

            if (parent.Level >= SupportClassification.MaxLevel)
            {
                errors["request"] = [$"\"{parent.Name}\" is level {SupportClassification.MaxLevel}, the last level: it cannot have items under it."];
            }
        }

        var cleanCode = (code ?? string.Empty).Trim();
        var cleanName = (name ?? string.Empty).Trim();
        await ValidateAsync(cleanCode, cleanName, displayOrder, excludeId: null, errors, cancellationToken).ConfigureAwait(false);
        ThrowIfAny(errors);

        var classification = new SupportClassification
        {
            Code = cleanCode,
            Name = cleanName,
            Level = (byte)(parent is null ? 1 : parent.Level + 1),
            ParentId = parent?.ClassificationId,
            DisplayOrder = displayOrder,
            IsActive = true,
            CreatedAt = _clock.UtcNow
        };

        await _repository.AddAsync(classification, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync(
            "SupportClassification.Created", EntityType, Key(classification), null, Describe(classification), cancellationToken).ConfigureAwait(false);

        return classification;
    }

    public async Task<SupportClassification> UpdateAsync(long classificationId, string code, string name, int displayOrder, CancellationToken cancellationToken = default)
    {
        var classification = await RequireAsync(classificationId, cancellationToken).ConfigureAwait(false);

        var errors = new Dictionary<string, IReadOnlyList<string>>();
        var cleanCode = (code ?? string.Empty).Trim();
        var cleanName = (name ?? string.Empty).Trim();
        await ValidateAsync(cleanCode, cleanName, displayOrder, classificationId, errors, cancellationToken).ConfigureAwait(false);
        ThrowIfAny(errors);

        var previous = Describe(classification);
        classification.Code = cleanCode;
        classification.Name = cleanName;
        classification.DisplayOrder = displayOrder;
        classification.UpdatedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync(
            "SupportClassification.Updated", EntityType, Key(classification), previous, Describe(classification), cancellationToken).ConfigureAwait(false);

        return classification;
    }

    public async Task SetActiveAsync(long classificationId, bool isActive, CancellationToken cancellationToken = default)
    {
        var classification = await RequireAsync(classificationId, cancellationToken).ConfigureAwait(false);

        if (classification.IsActive == isActive)
        {
            return;
        }

        var previous = Describe(classification);
        classification.IsActive = isActive;
        classification.UpdatedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync(
            isActive ? "SupportClassification.Activated" : "SupportClassification.Deactivated", EntityType, Key(classification), previous,
            Describe(classification), cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateAsync(
        string code, string name, int displayOrder, long? excludeId, Dictionary<string, IReadOnlyList<string>> errors, CancellationToken cancellationToken)
    {
        if (code.Length == 0)
        {
            errors["code"] = ["Code is required."];
        }
        else if (code.Length > MaxCodeLength || !code.All(char.IsAsciiDigit))
        {
            errors["code"] = [$"Code must be digits only, at most {MaxCodeLength} (the CRM classification code)."];
        }
        else if (await _repository.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false) is { } existing && existing.ClassificationId != excludeId)
        {
            errors["code"] = [$"Code {code} is already used by \"{existing.Name}\"."];
        }

        if (name.Length == 0)
        {
            errors["name"] = ["Name is required."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = [$"Name must be at most {MaxNameLength} characters."];
        }

        if (displayOrder < 0)
        {
            errors["displayOrder"] = ["Display order must be 0 or greater."];
        }
    }

    private static void ThrowIfAny(Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (errors.Count > 0)
        {
            throw new SupportClassificationValidationException(errors);
        }
    }

    private async Task<SupportClassification> RequireAsync(long classificationId, CancellationToken cancellationToken) =>
        await _repository.FindByIdAsync(classificationId, cancellationToken).ConfigureAwait(false)
        ?? throw new SupportClassificationNotFoundException($"No classification with id {classificationId}.");

    private static string Key(SupportClassification classification) => classification.ClassificationId.ToString(CultureInfo.InvariantCulture);

    private static string Describe(SupportClassification classification) =>
        $"{{\"code\":{JsonSerializer.Serialize(classification.Code)},\"name\":{JsonSerializer.Serialize(classification.Name)},\"level\":{classification.Level.ToString(CultureInfo.InvariantCulture)}," +
        $"\"displayOrder\":{classification.DisplayOrder.ToString(CultureInfo.InvariantCulture)},\"isActive\":{(classification.IsActive ? "true" : "false")}}}";
}
