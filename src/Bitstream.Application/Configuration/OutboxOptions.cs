namespace Bitstream.Application.Configuration;

/// <summary>
/// Governs <c>OutboxDispatcher</c> (TR-ARC-03, TR-INT-04, TR-INT-05).
/// <para>
/// Deliberately separate from any one adapter's options (e.g. CRM's own timeout and health
/// probe settings): the dispatcher claims and retries messages for every target system through
/// the same loop, so its retry budget is one setting, not one per adapter.
/// </para>
/// </summary>
public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Integration:OutboxDispatcher";

    /// <summary>Set false to stop the background loop entirely — a test host driving dispatch manually, for instance.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the dispatcher looks for due messages.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Messages claimed per cycle.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Attempts before a technically-failing message is dead-lettered (TR-INT-04, TR-INT-05).</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>
/// The one setting <see cref="Services.PostActivation.ActiveLineSyncService"/> needs from the BI
/// adapter's own configuration. Binds to the same "Integration:Bi" section
/// <c>Infrastructure.Integration.Bi.BiOptions</c> does — the Application layer cannot reference
/// that type directly (TR-ARC-01), so this is its own narrow view of the same JSON.
/// </summary>
public sealed class ActiveLineSyncOptions
{
    public const string SectionName = "Integration:Bi";

    public int PageSize { get; set; } = 1000;

    /// <summary>How often the scheduled sync runs (TR-PAS-03).</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Set false to stop the background loop — a test host driving sync manually, for instance.</summary>
    public bool ScheduledSyncEnabled { get; set; } = true;
}
