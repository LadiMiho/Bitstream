namespace Bitstream.Application.Abstractions.Integration;

/// <summary>
/// Port for SAP ERP (TRD 7.1 INT-SAP-01).
/// <para>
/// The population point of the financial code is undecided (TRD 11.4 open item 5), so the
/// direction of this interface — portal pulls from SAP, SAP pushes to the portal, or the
/// code is captured manually — is not yet fixed. The port is declared as a pull so that the
/// scheduler can be wired later; if the decision is "push", the inbound API gains an event
/// type and this port is deleted. Nothing outside the adapter changes either way
/// (TR-INT-11 to TR-INT-14).
/// </para>
/// </summary>
public interface ISapGateway
{
    /// <summary>INT-SAP-01. Retrieves the financial code for a request, when available.</summary>
    Task<IntegrationResult<FinancialCodeResult>> GetFinancialCodeAsync(
        FinancialCodeQuery query,
        CancellationToken cancellationToken = default);
}
