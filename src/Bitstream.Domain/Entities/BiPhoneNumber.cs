namespace Bitstream.Domain.Entities;

/// <summary>
/// A phone number BI publishes into portal.BiPhoneNumber (db/mssql/0024), e.g. <c>TRING_123</c>.
/// The text before the first <c>_</c> is the owning ISP's <see cref="Isp.TicketCode"/>. Read-only
/// for the portal.
/// </summary>
public sealed class BiPhoneNumber
{
    public const char PrefixSeparator = '_';

    public required string Phonenumber { get; set; }

    /// <summary>The ISP ticket code part of <paramref name="phonenumber"/>, or null when it has no <c>_</c>.</summary>
    public static string? PrefixOf(string phonenumber)
    {
        ArgumentNullException.ThrowIfNull(phonenumber);

        var separator = phonenumber.IndexOf(PrefixSeparator, StringComparison.Ordinal);

        return separator > 0 ? phonenumber[..separator] : null;
    }
}
