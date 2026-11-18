using System.Globalization;
using System.Text.RegularExpressions;

namespace Bitstream.Application.Services.Activation;

/// <summary>
/// The location an ISP user enters on the activation form (TR-ACT-02/03): coordinates only,
/// <c>latitude,longitude</c> in decimal degrees, for example <c>39.87781886431,19.98230910308</c>.
/// Links are not accepted. CRM receives the location as a Google Maps link, built by
/// <see cref="ToCrmLink"/> just before it is sent: <c>https://maps.google.com/?q=39.87781886431,19.98230910308</c>.
/// </summary>
public static partial class LocationParser
{
    /// <summary>Put in front of the coordinates to make the link CRM receives.</summary>
    public const string CrmLinkPrefix = "https://maps.google.com/?q=";

    /// <summary>Shown next to the field when the location is not a pair of coordinates.</summary>
    public const string FormatMessage =
        "Location must be coordinates: latitude,longitude, for example 39.87781886431,19.98230910308. Links are not accepted.";

    // Latitude, comma, longitude; a space after the comma is tolerated and dropped.
    [GeneratedRegex(@"^(-?\d{1,3}(?:\.\d+)?)\s*,\s*(-?\d{1,3}(?:\.\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex CoordinatesPattern();

    /// <summary>
    /// True when <paramref name="raw"/> is a valid <c>latitude,longitude</c> pair.
    /// <paramref name="normalized"/> is the pair as typed, without spaces (what is stored and sent);
    /// <paramref name="latitude"/> and <paramref name="longitude"/> are rounded to six decimal
    /// places, the precision of their columns.
    /// </summary>
    public static bool TryParse(string? raw, out string? normalized, out decimal? latitude, out decimal? longitude)
    {
        normalized = null;
        latitude = null;
        longitude = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var match = CoordinatesPattern().Match(raw.Trim());

        if (!match.Success
            || !TryParseCoordinate(match.Groups[1].Value, -90m, 90m, out var lat)
            || !TryParseCoordinate(match.Groups[2].Value, -180m, 180m, out var lng))
        {
            return false;
        }

        normalized = $"{match.Groups[1].Value},{match.Groups[2].Value}";
        latitude = lat;
        longitude = lng;
        return true;
    }

    /// <summary>
    /// The location as CRM receives it: <c>https://maps.google.com/?q=</c> followed by the stored
    /// coordinates. A request submitted before coordinates became the only accepted form already
    /// holds a full link, which is sent unchanged.
    /// </summary>
    public static string? ToCrmLink(string? storedLocation)
    {
        if (string.IsNullOrWhiteSpace(storedLocation))
        {
            return storedLocation;
        }

        var location = storedLocation.Trim();

        return location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? location
            : CrmLinkPrefix + location;
    }

    private static bool TryParseCoordinate(string token, decimal min, decimal max, out decimal value)
    {
        if (!decimal.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var exact))
        {
            value = default;
            return false;
        }

        // decimal(9,6): the fractional part needs rounding to fit the column.
        value = Math.Round(exact, 6, MidpointRounding.AwayFromZero);

        return exact >= min && exact <= max;
    }
}
