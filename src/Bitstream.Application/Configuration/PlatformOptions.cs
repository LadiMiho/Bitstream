namespace Bitstream.Application.Configuration;

/// <summary>
/// Public identifier configuration, TRD 3.2.
/// <para>
/// TR-DAT-02a: the prefix is an environment configuration value, not hard-coded, and must be
/// identical across portal, CRM, BI and SAP for a given environment. TR-DAT-02e: non-production
/// must use a distinct prefix. The agreed values are TRD 11.4 open item 2.
/// </para>
/// </summary>
public sealed class IdentifierOptions
{
    public const string SectionName = "Identifiers";

    /// <summary>Prefix of the activation request series, e.g. ISP.</summary>
    public string ActivationRequestPrefix { get; set; } = string.Empty;

    /// <summary>Prefix of the complaint ticket series. Distinguishable from the above (TR-DAT-06).</summary>
    public string ComplaintTicketPrefix { get; set; } = string.Empty;

    /// <summary>Prefix of the service change series.</summary>
    public string ServiceChangeRequestPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Identifier format, validated on both sides before acceptance (TR-DAT-02d). Held as
    /// configuration so the two sides can be pointed at one agreed value rather than each
    /// carrying its own copy in code.
    /// </summary>
    public string Pattern { get; set; } = "^[A-Z]+_[0-9]+$";
}

/// <summary>
/// Reference lists that must be extensible without a code release: line technologies (TR-PAS-02)
/// and the ISP-notifiable status set (TR-PAS-16). Packages (TR-ACT-01), classifications
/// (TR-ACT-04) and contract durations (TRD 5.1) used to live here too — they are now DB-backed
/// (db/mssql/0017_activation_catalogues.sql, <c>IActivationCatalogueRepository</c>) so an
/// administrator can maintain them without either a release or restarting the process, which a
/// configuration list can never offer.
/// </summary>
public sealed class CatalogueOptions
{
    public const string SectionName = "Catalogues";

    /// <summary>Technology codes presented in the active-lines dropdown (TR-PAS-02).</summary>
    public IList<string> LineTechnologies { get; set; } = [];

    /// <summary>
    /// CRM statuses that generate an ISP notification. Everything else — internal forwards in
    /// particular — is recorded but not notified (TR-PAS-13, TR-PAS-16, TR-INT-28).
    /// Contents are TRD 11.4 open item 4. Technically Completed notifies regardless of this
    /// list — TRD 6.3 names it explicitly, so it does not wait on open item 4 either.
    /// </summary>
    public IList<string> IspNotifiableStatuses { get; set; } = [];

    /// <summary>
    /// Three-level defect category cascade (TR-PAS-08). The real catalogue and its CRM mapping
    /// are TRD 11.4 open item 8; these are the codes the complaint ticket form validates
    /// against until it arrives.
    /// </summary>
    public IList<ComplaintCategoryDefinition> ComplaintCategories { get; set; } = [];
}

/// <summary>One leaf of the three-level complaint category cascade (TR-PAS-08).</summary>
public sealed class ComplaintCategoryDefinition
{
    public string L1 { get; set; } = string.Empty;

    public string L2 { get; set; } = string.Empty;

    public string L3 { get; set; } = string.Empty;
}

/// <summary>
/// Unanswered-closure handling, TRD 6.5.
/// TR-PAS-21a and TR-PAS-21b require the period and the reminder points to be configurable.
/// The mechanism itself is pending approval as TRD 11.4 open item 9.
/// </summary>
public sealed class TicketClosureOptions
{
    public const string SectionName = "TicketClosure";

    /// <summary>Whether unanswered closures are auto-confirmed at all.</summary>
    public bool AutoConfirmationEnabled { get; set; } = true;

    /// <summary>Working days from clearing code to auto-confirmation (TR-PAS-21a).</summary>
    public int AutoConfirmAfterWorkingDays { get; set; } = 5;

    /// <summary>Working-day offsets at which reminders are sent (TR-PAS-21b).</summary>
    public IList<int> ReminderAfterWorkingDays { get; set; } = [];

    /// <summary>Calendar days during which a closed ticket may be challenged (TR-PAS-21f).</summary>
    public int ChallengeWindowCalendarDays { get; set; } = 10;

    /// <summary>How often the auto-confirmation sweep runs.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Working-day calendar backing the auto-confirmation schedule (TR-PAS-21a).
/// The holiday list is maintained here so that a public holiday never needs a release.
/// </summary>
public sealed class WorkingCalendarOptions
{
    public const string SectionName = "WorkingCalendar";

    /// <summary>Days counted as working days.</summary>
    public IList<DayOfWeek> WorkingDays { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>Public holidays, in ISO yyyy-MM-dd form.</summary>
    public IList<DateOnly> PublicHolidays { get; set; } = [];

    /// <summary>
    /// Time zone the working-day boundaries are evaluated in. Timestamps are stored in UTC
    /// (TR-DAT-08); "two working days" is a local-time concept and needs this.
    /// </summary>
    public string TimeZoneId { get; set; } = "Central European Standard Time";
}
