namespace Bitstream.Application.Services.PostActivation;

/// <summary>Permission codes this module's endpoints check, seeded in <c>db/mssql/0007_seed_roles_permissions.sql</c>.</summary>
public static class PostActivationPermissionCodes
{
    public const string TicketCreate = "ticket.create";

    /// <summary>Read complaint tickets of the caller's own ISP — not needed to read their own; ownership, not permission.</summary>
    public const string TicketReadOwn = "ticket.read.own";

    public const string TicketReadAll = "ticket.read.all";

    public const string TicketCommentCreate = "ticket.comment.create";

    public const string TicketClosureDecide = "ticket.closure.decide";

    /// <summary>See the internal routing history hidden from ISP users (TR-SEC-20-style visibility split).</summary>
    public const string TicketRoutingRead = "ticket.routing.read";

    public const string ServiceChangeCreate = "servicechange.create";

    public const string ServiceChangeReadOwn = "servicechange.read.own";

    public const string IntegrationSyncTrigger = "integration.sync.trigger";

    public const string IntegrationDeadLetterRead = "integration.deadletter.read";

    public const string IntegrationDeadLetterReplay = "integration.deadletter.replay";
}
