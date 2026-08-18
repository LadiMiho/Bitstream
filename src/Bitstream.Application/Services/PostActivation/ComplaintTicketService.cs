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

namespace Bitstream.Application.Services.PostActivation;

/// <summary>Thrown for a validated business rule violation. The presentation layer maps this to 400/422.</summary>
public sealed class ComplaintTicketValidationException : Exception
{
    public ComplaintTicketValidationException(string message)
        : base(message)
    {
    }

    public ComplaintTicketValidationException(IReadOnlyList<string> violations)
        : base(string.Join(" ", violations)) =>
        Violations = violations;

    public IReadOnlyList<string> Violations { get; } = [];
}

/// <summary>Thrown when the referenced ticket does not exist. The presentation layer maps this to 404.</summary>
public sealed class ComplaintTicketNotFoundException : Exception
{
    public ComplaintTicketNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Implements <see cref="IComplaintTicketService"/>: TRD 6.2 to 6.7 — creation, comments and the
/// dashboard search. The closure handshake and auto-confirmation live in
/// <see cref="TicketClosureService"/>.
/// </summary>
public sealed partial class ComplaintTicketService : IComplaintTicketService
{
    private readonly IComplaintTicketRepository _ticketRepository;
    private readonly IActiveLineRepository _lineRepository;
    private readonly IIspRepository _ispRepository;
    private readonly IPublicIdentifierGenerator _identifierGenerator;
    private readonly IIntegrationOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;
    private readonly ICurrentUserContext _currentUser;
    private readonly IOptionsMonitor<CatalogueOptions> _catalogueOptions;

    public ComplaintTicketService(
        IComplaintTicketRepository ticketRepository,
        IActiveLineRepository lineRepository,
        IIspRepository ispRepository,
        IPublicIdentifierGenerator identifierGenerator,
        IIntegrationOutbox outbox,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        ICurrentUserContext currentUser,
        IOptionsMonitor<CatalogueOptions> catalogueOptions)
    {
        _ticketRepository = ticketRepository;
        _lineRepository = lineRepository;
        _ispRepository = ispRepository;
        _identifierGenerator = identifierGenerator;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
        _currentUser = currentUser;
        _catalogueOptions = catalogueOptions;
    }

    public async Task<ComplaintTicket> CreateAsync(CreateComplaintTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var violations = new List<string>();

        var line = await _lineRepository.FindByIdAsync(ticket.LineId, cancellationToken).ConfigureAwait(false);

        if (line is null)
        {
            violations.Add($"Line {ticket.LineId} does not exist.");
        }
        else if (line.IspId != ticket.IspId)
        {
            violations.Add($"Line {ticket.LineId} does not belong to ISP {ticket.IspId}.");
        }

        // An ISP user may only raise a ticket for their own ISP; an internal caller may raise one
        // on behalf of any ISP (ServiceDesk taking a phone report, for instance).
        if (_currentUser.IspId is { } callerIspId && callerIspId != ticket.IspId)
        {
            violations.Add("You may only create complaint tickets for your own ISP.");
        }

        // TR-PAS-08/09: the three-level cascade, validated against the configured catalogue when
        // one is configured — TRD 11.4 open item 8 leaves the real catalogue unsupplied, so an
        // empty configuration is not itself a validation failure (see CatalogueOptionsValidator).
        var categories = _catalogueOptions.CurrentValue.ComplaintCategories;

        if (categories.Count > 0 &&
            !categories.Any(c =>
                string.Equals(c.L1, ticket.CategoryL1, StringComparison.Ordinal) &&
                string.Equals(c.L2, ticket.CategoryL2, StringComparison.Ordinal) &&
                string.Equals(c.L3, ticket.CategoryL3, StringComparison.Ordinal)))
        {
            violations.Add($"'{ticket.CategoryL1} / {ticket.CategoryL2} / {ticket.CategoryL3}' is not a category in the configured catalogue.");
        }

        var description = StripHtml(ticket.Description);

        if (string.IsNullOrWhiteSpace(description))
        {
            violations.Add("Description is required.");
        }
        else if (description.Length > 4000)
        {
            violations.Add("Description must not exceed 4000 characters.");
        }

        if (violations.Count > 0)
        {
            throw new ComplaintTicketValidationException(violations);
        }

        var now = _clock.UtcNow;
        var publicId = await _identifierGenerator.NextAsync(IdentifierSeries.ComplaintTicket, cancellationToken).ConfigureAwait(false);

        var complaintTicket = new ComplaintTicket
        {
            PublicId = publicId,
            IspId = ticket.IspId,
            LineId = ticket.LineId,
            Line = line!,
            CategoryL1 = ticket.CategoryL1,
            CategoryL2 = ticket.CategoryL2,
            CategoryL3 = ticket.CategoryL3,
            Description = description!,
            Status = "Open",
            OpenedAt = now,
            OpenedBy = _currentUser.UserId
        };

        await _ticketRepository.AddAsync(complaintTicket, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var isp = await _ispRepository.FindByIdAsync(ticket.IspId, cancellationToken).ConfigureAwait(false);

        if (isp is not null)
        {
            var envelope = new IntegrationEnvelope(Guid.NewGuid(), _currentUser.CorrelationId, publicId, now);
            var command = new CreateComplaintTicketCommand(
                envelope, publicId, isp.CrmBpReference, line!.ContractId, line.SubscriberReference,
                ticket.CategoryL1, ticket.CategoryL2, ticket.CategoryL3, description!);

            await _outbox.EnqueueOutboundAsync(
                TargetSystem.Crm, "INT-CRM-04", "CREATE_COMPLAINT_TICKET", publicId,
                JsonSerializer.Serialize(command), _currentUser.CorrelationId, publicId, cancellationToken)
                .ConfigureAwait(false);
        }

        await _auditWriter.WriteAsync(
            "ComplaintTicket.Created", "ComplaintTicket", complaintTicket.TicketId.ToString(CultureInfo.InvariantCulture),
            null, $"{{\"publicId\":{JsonSerializer.Serialize(publicId)},\"ispId\":{ticket.IspId},\"lineId\":{ticket.LineId}}}",
            cancellationToken).ConfigureAwait(false);

        return complaintTicket;
    }

    public async Task<TicketComment> AddCommentAsync(long ticketId, string body, CancellationToken cancellationToken = default)
    {
        var ticket = await _ticketRepository.FindByIdAsync(ticketId, cancellationToken).ConfigureAwait(false) ??
            throw new ComplaintTicketNotFoundException($"Ticket {ticketId} does not exist.");

        if (_currentUser.IspId is { } callerIspId && callerIspId != ticket.IspId)
        {
            // Same not-found discipline as everywhere else ownership is enforced (TR-SEC-19).
            throw new ComplaintTicketNotFoundException($"Ticket {ticketId} does not exist.");
        }

        if (string.Equals(ticket.Status, "Closed", StringComparison.Ordinal))
        {
            throw new ComplaintTicketValidationException("Comments cannot be added to a closed ticket.");
        }

        var strippedBody = StripHtml(body);

        if (string.IsNullOrWhiteSpace(strippedBody))
        {
            throw new ComplaintTicketValidationException("Comment body is required.");
        }

        if (strippedBody.Length > 4000)
        {
            throw new ComplaintTicketValidationException("Comment body must not exceed 4000 characters.");
        }

        var now = _clock.UtcNow;
        var comment = new TicketComment
        {
            TicketId = ticketId,
            Ticket = ticket,
            AuthorUserId = _currentUser.UserId,
            AuthorType = _currentUser.IspId is not null ? CommentAuthorType.Isp : CommentAuthorType.ServiceDesk,
            Body = strippedBody,
            CreatedAt = now,
            CrmSyncStatus = "Pending"
        };

        await _ticketRepository.AddCommentAsync(comment, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (ticket.CrmTicketId is { } crmTicketId)
        {
            // TR-PAS-28: idempotency key includes the comment's own id, distinct per comment —
            // the ticket's public id alone would collide across every comment on the same ticket.
            var idempotencyKey = $"{ticket.PublicId}#comment-{comment.CommentId}";
            var envelope = new IntegrationEnvelope(Guid.NewGuid(), _currentUser.CorrelationId, idempotencyKey, now);
            var command = new ReplicateCommentCommand(
                envelope, ticket.PublicId, crmTicketId, _currentUser.UserId?.ToString(CultureInfo.InvariantCulture) ?? "Unknown",
                comment.AuthorType.ToString(), strippedBody, now);

            await _outbox.EnqueueOutboundAsync(
                TargetSystem.Crm, "INT-CRM-06", "REPLICATE_COMMENT", idempotencyKey,
                JsonSerializer.Serialize(command), _currentUser.CorrelationId, ticket.PublicId, cancellationToken)
                .ConfigureAwait(false);
        }

        await _auditWriter.WriteAsync(
            "TicketComment.Added", "ComplaintTicket", ticketId.ToString(CultureInfo.InvariantCulture),
            null, $"{{\"commentId\":{comment.CommentId}}}", cancellationToken).ConfigureAwait(false);

        return comment;
    }

    public async Task<IReadOnlyList<ComplaintTicket>> SearchAsync(ComplaintTicketFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // TR-SEC-18 / TR-PAS-06: an ISP user's search is forced to their own ISP regardless of
        // what they asked for; only a caller holding ticket.read.all may see across ISPs.
        var effectiveIspId = _currentUser.HasPermission(PostActivationPermissionCodes.TicketReadAll)
            ? filter.IspId
            : _currentUser.IspId;

        return await _ticketRepository.SearchAsync(
            effectiveIspId, filter.Status, filter.CreatedFrom, filter.CreatedTo, filter.CategoryL1, filter.LineId,
            filter.Skip, filter.Take, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComplaintTicket?> GetByPublicIdAsync(string publicId, CancellationToken cancellationToken = default)
    {
        var ticket = await _ticketRepository.FindByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        if (ticket is null)
        {
            return null;
        }

        if (!_currentUser.HasPermission(PostActivationPermissionCodes.TicketReadAll) && _currentUser.IspId != ticket.IspId)
        {
            await _auditWriter.WriteAsync(
                "Security.AccessDenied.CrossIsp", "ComplaintTicket", ticket.TicketId.ToString(CultureInfo.InvariantCulture),
                null, $"{{\"callerIspId\":{(_currentUser.IspId is { } ispId ? ispId.ToString(CultureInfo.InvariantCulture) : "null")}}}",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        return ticket;
    }

    public async Task<IReadOnlyList<TicketComment>> GetCommentsAsync(long ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await _ticketRepository.FindByIdAsync(ticketId, cancellationToken).ConfigureAwait(false) ??
            throw new ComplaintTicketNotFoundException($"Ticket {ticketId} does not exist.");

        if (_currentUser.IspId is { } callerIspId && callerIspId != ticket.IspId && !_currentUser.HasPermission(PostActivationPermissionCodes.TicketReadAll))
        {
            throw new ComplaintTicketNotFoundException($"Ticket {ticketId} does not exist.");
        }

        return await _ticketRepository.GetCommentsAsync(ticketId, cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagPattern();

    private static string? StripHtml(string? value) =>
        string.IsNullOrEmpty(value) ? value : HtmlTagPattern().Replace(value, string.Empty).Trim();
}
