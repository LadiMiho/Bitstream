namespace Bitstream.Web.Presentation;

/// <summary>
/// The placeholder shown in a grid with no rows (Views/Shared/_EmptyState.cshtml).
/// </summary>
/// <param name="Icon">One of <see cref="EmptyStateIcons"/>.</param>
/// <param name="Title">Short headline, e.g. "No packages yet".</param>
/// <param name="Message">One or two sentences: why it is empty and what to do.</param>
/// <param name="ActionLabel">Optional button text.</param>
/// <param name="ActionHref">Button as a link to this address.</param>
/// <param name="ActionAttributes">Button as a script hook instead: raw attributes, e.g. <c>data-empty-clear</c>.</param>
/// <param name="Id">Optional element id, for grids whose script shows and hides the placeholder.</param>
/// <param name="Hidden">Rendered hidden, for script-driven grids.</param>
public sealed record EmptyStateModel(
    string Icon,
    string Title,
    string Message,
    string? ActionLabel = null,
    string? ActionHref = null,
    string? ActionAttributes = null,
    string? Id = null,
    bool Hidden = false);

/// <summary>Line icons (24×24, stroke) for <see cref="EmptyStateModel"/>.</summary>
public static class EmptyStateIcons
{
    public const string Inbox = "M3 13h4l2 3h6l2-3h4M5 5h14l2 8v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-5l2-8Z";
    public const string Search = "m21 21-4.3-4.3M10.5 17a6.5 6.5 0 1 1 0-13 6.5 6.5 0 0 1 0 13Z";
    public const string Package = "M21 8 12 3 3 8m18 0-9 5m9-5v8l-9 5m0-8L3 8m9 5v8M3 8v8l9 5";
    public const string Calendar = "M7 3v3m10-3v3M4 9h16M5 5h14a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1Z";
    public const string Users = "M16 19v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1m20 0v-1a4 4 0 0 0-3-3.9M15 3.1a4 4 0 0 1 0 7.8M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z";
    public const string Building = "M4 21V5a1 1 0 0 1 1-1h9a1 1 0 0 1 1 1v16m0-10h4a1 1 0 0 1 1 1v9M2 21h20M8 8h3m-3 4h3m-3 4h3";
    public const string Tag = "M3 12V4a1 1 0 0 1 1-1h8l9 9-9 9-9-9Zm4.5-4.5h.01";
}
