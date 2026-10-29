namespace Bitstream.Domain.Entities;

/// <summary>
/// A phone number BI publishes into portal.BiPhoneNumber (db/mssql/0024), e.g. <c>TRING024</c>
/// (or, in the older format, <c>TRING_024</c>). The letters before the number are the owning
/// ISP's <see cref="Isp.TicketCode"/>. Read-only for the portal.
/// </summary>
public sealed class BiPhoneNumber
{
    /// <summary>Separated the ticket code from the number in the older format (<c>TRING_024</c>).</summary>
    public const char PrefixSeparator = '_';

    public required string Phonenumber { get; set; }

    /// <summary>
    /// The ISP ticket code part of <paramref name="phonenumber"/>: the text before the first
    /// <c>_</c> when there is one (<c>TRING_024</c>), otherwise the letters before the first digit
    /// (<c>TRINGGPON024</c> → <c>TRINGGPON</c>). Null when there is no such prefix or nothing
    /// follows it.
    /// </summary>
    public static string? PrefixOf(string phonenumber)
    {
        ArgumentNullException.ThrowIfNull(phonenumber);

        var separator = phonenumber.IndexOf(PrefixSeparator, StringComparison.Ordinal);

        if (separator >= 0)
        {
            return separator > 0 ? phonenumber[..separator] : null;
        }

        var letters = 0;
        while (letters < phonenumber.Length && char.IsAsciiLetter(phonenumber[letters]))
        {
            letters++;
        }

        return letters > 0 && letters < phonenumber.Length && char.IsAsciiDigit(phonenumber[letters])
            ? phonenumber[..letters]
            : null;
    }
}
