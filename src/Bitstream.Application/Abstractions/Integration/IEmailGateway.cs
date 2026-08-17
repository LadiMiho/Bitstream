namespace Bitstream.Application.Abstractions.Integration;

/// <summary>
/// Port for the SMTP relay (TRD 7.1 INT-MAIL-01).
/// Dispatch is asynchronous and must never roll back a business transaction (TR-NTF-03).
/// The adapter honours the non-production redirect mailbox (TR-NTF-07).
/// </summary>
public interface IEmailGateway
{
    Task<IntegrationResult<bool>> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default);
}
