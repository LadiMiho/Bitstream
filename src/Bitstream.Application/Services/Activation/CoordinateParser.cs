using System.Globalization;
using System.Text.RegularExpressions;

namespace Bitstream.Application.Services.Activation;

/// <summary>
/// Parses the location exactly as an ISP user types or pastes it (TR-ACT-02) into a normalised
/// latitude/longitude pair (TR-ACT-03).
/// <para>
/// Two input shapes are accepted, because that is what the TRD 5.1 form field actually receives:
/// a bare <c>latitude,longitude</c> pair, or a map URL copied from a mapping service, which
/// carries the coordinate either after an <c>@</c> (the common "centre of view" marker) or in a
/// <c>q=</c>/<c>ll=</c> query parameter. Anything else is rejected rather than guessed at — a
/// wrong coordinate silently accepted would misroute a GIS verification (TR-ACT-12).
/// </para>
/// </summary>
public static partial class CoordinateParser
{
    [GeneratedRegex(@"^\s*(-?\d{1,3}(?:\.\d+)?)\s*,\s*(-?\d{1,3}(?:\.\d+)?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BarePairPattern();

    [GeneratedRegex(@"[@,](-?\d{1,3}(?:\.\d+)?),(-?\d{1,3}(?:\.\d+)?)(?:,|$|[/?&])", RegexOptions.CultureInvariant)]
    private static partial Regex AtMarkerPattern();

    [GeneratedRegex(@"[?&](?:q|ll|query)=(-?\d{1,3}(?:\.\d+)?)(?:,|%2[cC])(-?\d{1,3}(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex QueryParameterPattern();

    /// <summary>
    /// Attempts to extract and normalise a coordinate pair from <paramref name="raw"/>.
    /// Returns false — with <paramref name="latitude"/>/<paramref name="longitude"/> left at
    /// zero — when nothing recognisable is present, or the numbers found are out of range.
    /// </summary>
    public static bool TryParse(string? raw, out decimal latitude, out decimal longitude)
    {
        latitude = 0m;
        longitude = 0m;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var match = BarePairPattern().Match(raw);

        if (!match.Success)
        {
            match = QueryParameterPattern().Match(raw);
        }

        if (!match.Success)
        {
            match = AtMarkerPattern().Match(raw);
        }

        if (!match.Success)
        {
            return false;
        }

        if (!TryParseCoordinate(match.Groups[1].Value, -90m, 90m, out latitude) ||
            !TryParseCoordinate(match.Groups[2].Value, -180m, 180m, out longitude))
        {
            latitude = 0m;
            longitude = 0m;
            return false;
        }

        return true;
    }

    private static bool TryParseCoordinate(string token, decimal min, decimal max, out decimal value)
    {
        if (!decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        // decimal(9,6): three integer digits and six fractional — more than enough range for
        // -90..90 / -180..180, but the fractional part still needs rounding to fit the column.
        value = Math.Round(value, 6, MidpointRounding.AwayFromZero);

        return value >= min && value <= max;
    }
}
