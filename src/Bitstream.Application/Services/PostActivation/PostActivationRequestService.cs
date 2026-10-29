using System.Globalization;
using System.Text.Json;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>A post-activation request that breaks a rule; field-keyed for the form (TR-NFR-12).</summary>
public sealed class PostActivationRequestValidationException : Exception
{
    public PostActivationRequestValidationException(IReadOnlyDictionary<string, IReadOnlyList<string>> fieldErrors)
        : base(string.Join(" ", fieldErrors.Values.SelectMany(messages => messages))) =>
        FieldErrors = fieldErrors;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors { get; }
}

/// <summary>What the New request form offers the current user.</summary>
/// <param name="PhoneNumbers">BI's numbers: all of them for an internal user, the own ISP's for an ISP user.</param>
/// <param name="Classifications">Active classifications only, pruned to branches that end in an active level-3 item.</param>
public sealed record PostActivationFormOptions(
    IReadOnlyList<string> PhoneNumbers,
    IReadOnlyList<SupportClassificationNode> Classifications);

/// <summary>One choice of a filter dropdown.</summary>
public sealed record FilterOption(string Value, string Label);

/// <summary>What the Post-Activation Filters popup offers.</summary>
/// <param name="Isps">Every ISP for <c>ticket.read.all</c>; null for anyone else (they only see their own ISP).</param>
/// <param name="RequestTypes">Level-1 classifications, inactive ones included and marked.</param>
/// <param name="Categories">Level-2 classifications, labelled "Type › Category".</param>
public sealed record PostActivationFilterOptions(
    IReadOnlyList<FilterOption>? Isps,
    IReadOnlyList<FilterOption> RequestTypes,
    IReadOnlyList<FilterOption> Categories);

/// <summary>A request with its CRM communication, for the View drawer.</summary>
public sealed record PostActivationRequestDetails(PostActivationRequest Request, IReadOnlyList<IntegrationMessage> Messages);

public interface IPostActivationRequestService
{
    Task<PostActivationFormOptions> GetFormOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates, saves the request as PendingCrmSync and queues INT-CRM-11.</summary>
    Task<PostActivationRequest> CreateAsync(string phonenumber, long classificationId, string? note, CancellationToken cancellationToken = default);

    /// <summary>
    /// The grid: <c>ticket.read.all</c> sees every ISP's requests; anyone else only their own ISP's,
    /// whatever ISP filter was asked for.
    /// </summary>
    Task<PagedResult<PostActivationRequest>> SearchAsync(PostActivationRequestSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Every request matching <paramref name="filter"/> (same ownership rule), for the Excel export, capped at <see cref="PostActivationRequestService.MaxExportRows"/>.</summary>
    Task<PagedResult<PostActivationRequest>> ExportAsync(PostActivationRequestSearchFilter filter, CancellationToken cancellationToken = default);

    /// <summary>The Filters popup's choices for the current user.</summary>
    Task<PostActivationFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Null when it does not exist or belongs to another ISP (not-found, never forbidden).</summary>
    Task<PostActivationRequestDetails?> GetAsync(string publicId, CancellationToken cancellationToken = default);

    /// <summary>Records CRM's answer (the outbox dispatcher calls this): Submitted, AlreadyExists or Failed.</summary>
    Task MarkCrmResultAsync(string publicId, string status, string? crmTicketId, string? message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements <see cref="IPostActivationRequestService"/>: a phone number from BI
/// (portal.BiPhoneNumber), a level-3 support classification and an optional note, sent to CRM
/// through CRM's <c>BITSTREAM_TO_CRM</c> workflow via the outbox (INT-CRM-11). A phone number belongs to
/// the ISP whose ticket code is its prefix (the text before the first <c>_</c>).
/// </summary>
public sealed class PostActivationRequestService : IPostActivationRequestService
{
    public const string InterfaceCode = "INT-CRM-11";
    public const string MessageType = "BITSTREAM_TO_CRM";
    public const int MaxNoteLength = 4000;

    /// <summary>The most rows one Excel export returns; the file says so when there were more.</summary>
    public const int MaxExportRows = 50_000;
    private const string EntityType = "PostActivationRequest";

    private readonly IPostActivationRequestRepository _repository;
    private readonly ISupportClassificationRepository _classifications;
    private readonly IIspRepository _isps;
    private readonly IPublicIdentifierGenerator _identifierGenerator;
    private readonly IIntegrationOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly ICurrentUserContext _currentUser;
    private readonly IClock _clock;

    public PostActivationRequestService(
        IPostActivationRequestRepository repository,
        ISupportClassificationRepository classifications,
        IIspRepository isps,
        IPublicIdentifierGenerator identifierGenerator,
        IIntegrationOutbox outbox,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        ICurrentUserContext currentUser,
        IClock clock)
    {
        _repository = repository;
        _classifications = classifications;
        _isps = isps;
        _identifierGenerator = identifierGenerator;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<PostActivationFormOptions> GetFormOptionsAsync(CancellationToken cancellationToken = default)
    {
        var phoneNumbers = await GetAllowedPhoneNumbersAsync(cancellationToken).ConfigureAwait(false);
        var all = await _classifications.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return new PostActivationFormOptions(phoneNumbers, ActiveTree(all));
    }

    public async Task<PostActivationRequest> CreateAsync(string phonenumber, long classificationId, string? note, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, IReadOnlyList<string>>();
        var cleanPhone = (phonenumber ?? string.Empty).Trim();
        Isp? isp = null;

        if (cleanPhone.Length == 0)
        {
            errors["phonenumber"] = ["Phone number is required."];
        }
        else if (!await _repository.PhoneNumberExistsAsync(cleanPhone, cancellationToken).ConfigureAwait(false))
        {
            errors["phonenumber"] = [$"Phone number {cleanPhone} is not in the list."];
        }
        else if (BiPhoneNumber.PrefixOf(cleanPhone) is not { } prefix)
        {
            errors["phonenumber"] = [$"Phone number {cleanPhone} has no ISP ticket code (the part before '_')."];
        }
        else
        {
            isp = await _isps.FindByTicketCodeAsync(prefix, cancellationToken).ConfigureAwait(false);

            if (_currentUser.IspId is { } ownIspId && isp?.IspId != ownIspId)
            {
                // Another ISP's number is reported exactly like one that does not exist.
                errors["phonenumber"] = [$"Phone number {cleanPhone} is not in the list."];
                isp = null;
            }
            else if (isp is null)
            {
                errors["phonenumber"] = [$"No ISP has ticket code '{prefix}'. Set it in ISP Administration first."];
            }
        }

        var chain = await ResolveChainAsync(classificationId, cancellationToken).ConfigureAwait(false);

        if (chain is null)
        {
            errors["classificationId"] = ["Select a classification at all three levels."];
        }
        else if (chain.Any(c => !c.IsActive))
        {
            errors["classificationId"] = ["This classification is no longer offered. Select another one."];
        }

        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        if (cleanNote is { Length: > MaxNoteLength })
        {
            errors["note"] = [$"Note must be at most {MaxNoteLength} characters."];
        }

        if (errors.Count > 0)
        {
            throw new PostActivationRequestValidationException(errors);
        }

        var now = _clock.UtcNow;
        var publicId = await _identifierGenerator.NextAsync(IdentifierSeries.PostActivationRequest, cancellationToken).ConfigureAwait(false);

        var request = new PostActivationRequest
        {
            PublicId = publicId,
            IspId = isp!.IspId,
            Phonenumber = cleanPhone,
            ClassificationId = chain![2].ClassificationId,
            Classification1Code = chain[0].Code,
            Classification1Name = chain[0].Name,
            Classification2Code = chain[1].Code,
            Classification2Name = chain[1].Name,
            Classification3Code = chain[2].Code,
            Classification3Name = chain[2].Name,
            Note = cleanNote,
            Status = PostActivationRequestStatus.PendingCrmSync,
            CreatedAt = now,
            CreatedBy = _currentUser.UserId
        };

        await _repository.AddAsync(request, cancellationToken).ConfigureAwait(false);

        var command = new CreatePhoneTicketCommand(
            new IntegrationEnvelope(Guid.NewGuid(), _currentUser.CorrelationId, publicId, now),
            publicId, cleanPhone, request.Classification3Code, cleanNote);

        await _outbox.EnqueueOutboundAsync(
            TargetSystem.Crm, InterfaceCode, MessageType, publicId,
            JsonSerializer.Serialize(command), _currentUser.CorrelationId, publicId, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "PostActivationRequest.Created", EntityType, publicId, null,
            $"{{\"publicId\":{JsonSerializer.Serialize(publicId)},\"ispId\":{request.IspId.ToString(CultureInfo.InvariantCulture)}," +
            $"\"phonenumber\":{JsonSerializer.Serialize(cleanPhone)},\"classification\":{JsonSerializer.Serialize(request.Classification3Code)}}}",
            cancellationToken).ConfigureAwait(false);

        return request;
    }

    public async Task<PagedResult<PostActivationRequest>> SearchAsync(
        PostActivationRequestSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (!CanReadAll)
        {
            if (_currentUser.IspId is not { } ownIspId)
            {
                return new PagedResult<PostActivationRequest>([], 0);
            }

            filter = filter with { IspId = ownIspId };
        }

        var (items, totalCount) = await _repository.SearchAsync(filter, skip, take, cancellationToken).ConfigureAwait(false);
        return new PagedResult<PostActivationRequest>(items, totalCount);
    }

    public async Task<PagedResult<PostActivationRequest>> ExportAsync(PostActivationRequestSearchFilter filter, CancellationToken cancellationToken = default)
    {
        var result = await SearchAsync(filter, 0, MaxExportRows, cancellationToken).ConfigureAwait(false);

        // TR-REP-08: every export is audited, with what was asked for and how much was handed out.
        await _auditWriter.WriteAsync(
            "PostActivationRequest.Exported", EntityType, null, null,
            $"{{\"filter\":{JsonSerializer.Serialize(filter)},\"rows\":{result.Items.Count.ToString(CultureInfo.InvariantCulture)}}}",
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<PostActivationFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FilterOption>? isps = null;

        if (CanReadAll)
        {
            var (all, _) = await _isps.SearchAsync(search: null, status: null, skip: 0, take: 1000, cancellationToken).ConfigureAwait(false);
            isps = [.. all.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(i => new FilterOption(i.IspId.ToString(CultureInfo.InvariantCulture), i.Name))];
        }

        var classifications = await _classifications.GetAllAsync(cancellationToken).ConfigureAwait(false);
        static string Label(SupportClassification c) => c.IsActive ? c.Name : $"{c.Name} (inactive)";
        static IOrderedEnumerable<SupportClassification> Ordered(IEnumerable<SupportClassification> items) =>
            items.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);

        var types = Ordered(classifications.Where(c => c.Level == 1)).ToList();
        var categories = types
            .SelectMany(type => Ordered(classifications.Where(c => c.Level == 2 && c.ParentId == type.ClassificationId))
                .Select(category => new FilterOption(category.Code, $"{type.Name} › {Label(category)}")))
            .ToList();

        return new PostActivationFilterOptions(
            isps,
            [.. types.Select(type => new FilterOption(type.Code, Label(type)))],
            categories);
    }

    public async Task<PostActivationRequestDetails?> GetAsync(string publicId, CancellationToken cancellationToken = default)
    {
        var request = await _repository.FindByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        if (request is null || (!CanReadAll && request.IspId != _currentUser.IspId))
        {
            return null;
        }

        var messages = await _repository.GetMessagesAsync(publicId, cancellationToken).ConfigureAwait(false);

        return new PostActivationRequestDetails(request, messages);
    }

    public async Task MarkCrmResultAsync(string publicId, string status, string? crmTicketId, string? message, CancellationToken cancellationToken = default)
    {
        var request = await _repository.FindByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            return;
        }

        var previous = request.Status;
        request.Status = status;
        request.CrmTicketId = crmTicketId ?? request.CrmTicketId;
        request.CrmMessage = message is { Length: > 1000 } ? message[..1000] : message;
        request.LastUpdatedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.WriteAsync(
            "PostActivationRequest.CrmResult", EntityType, publicId,
            $"{{\"status\":{JsonSerializer.Serialize(previous)}}}",
            $"{{\"status\":{JsonSerializer.Serialize(status)},\"crmTicketId\":{JsonSerializer.Serialize(request.CrmTicketId)}}}",
            cancellationToken).ConfigureAwait(false);
    }

    private bool CanReadAll => _currentUser.HasPermission(PostActivationPermissionCodes.TicketReadAll);

    /// <summary>All numbers for an internal user; for an ISP user only those prefixed with their ISP's ticket code (none when it has no code).</summary>
    private async Task<IReadOnlyList<string>> GetAllowedPhoneNumbersAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.IspId is not { } ispId)
        {
            return await _repository.GetPhoneNumbersAsync(null, cancellationToken).ConfigureAwait(false);
        }

        var isp = await _isps.FindByIdAsync(ispId, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(isp?.TicketCode)
            ? []
            : await _repository.GetPhoneNumbersAsync(isp.TicketCode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>[level 1, level 2, level 3] for a level-3 id; null when the id is not a level-3 item.</summary>
    private async Task<IReadOnlyList<SupportClassification>?> ResolveChainAsync(long classificationId, CancellationToken cancellationToken)
    {
        var all = await _classifications.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var byId = all.ToDictionary(c => c.ClassificationId);

        if (!byId.TryGetValue(classificationId, out var level3) || level3.Level != SupportClassification.MaxLevel
            || level3.ParentId is not { } level2Id || !byId.TryGetValue(level2Id, out var level2)
            || level2.ParentId is not { } level1Id || !byId.TryGetValue(level1Id, out var level1))
        {
            return null;
        }

        return [level1, level2, level3];
    }

    /// <summary>The active part of the tree: inactive items and everything under them are left out, and so is any branch with no active level-3 item.</summary>
    private static IReadOnlyList<SupportClassificationNode> ActiveTree(IReadOnlyList<SupportClassification> all)
    {
        var byParent = all.Where(c => c.IsActive).ToLookup(c => c.ParentId);

        IReadOnlyList<SupportClassificationNode> ChildrenOf(long? parentId) =>
        [
            .. byParent[parentId]
                .OrderBy(c => c.DisplayOrder)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new SupportClassificationNode(c, c.Level < SupportClassification.MaxLevel ? ChildrenOf(c.ClassificationId) : []))
                .Where(n => n.Classification.Level == SupportClassification.MaxLevel || n.Children.Count > 0)
        ];

        return ChildrenOf(null);
    }
}
