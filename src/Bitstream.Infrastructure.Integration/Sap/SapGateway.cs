using Bitstream.Application.Abstractions.Integration;

namespace Bitstream.Infrastructure.Integration.Sap;

/// <summary>Configuration of the SAP ERP adapter (TRD 7.5).</summary>
public sealed class SapOptions
{
    public const string SectionName = "Integration:Sap";

    public Uri? BaseAddress { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Disabled until the population point of the financial code is decided
    /// (TRD 11.4 open item 5). While disabled the field simply stays null, which the flow
    /// must tolerate (TR-INT-14).
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Adapter for SAP ERP (TRD 7.1 INT-SAP-01).
/// <para>
/// BLOCKED — TRD 11.4 open item 5. Neither the trigger nor the direction of this interface
/// is decided, so it is left unimplemented and disabled by configuration.
/// </para>
/// </summary>
public sealed class SapGateway : ISapGateway
{
    private const string PendingDecision =
        "SAP financial code population point is undecided (TRD 11.4 open item 5).";

    /// <summary>Typed client supplied by <c>AddHttpClient</c>; used once the decision is taken.</summary>
    private HttpClient Client { get; }

    public SapGateway(HttpClient httpClient) => Client = httpClient;

    public Task<IntegrationResult<FinancialCodeResult>> GetFinancialCodeAsync(
        FinancialCodeQuery query,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(PendingDecision);
}
