using System.Globalization;
using System.Text.RegularExpressions;

namespace Bitstream.Application.Services.Activation;

/// <summary>
/// Checks the location an ISP user enters (TR-ACT-02) against the only two forms CRM accepts,
/// and takes the coordinates out of it when it carries them (TR-ACT-03):
/// <list type="bullet">
/// <item>a Google Maps short link, <c>https://maps.app.goo.gl/wpd59capnslqrxsh7</c> — it carries
/// no coordinates, so none are stored;</item>
/// <item>a Google Maps query link, <c>http://maps.google.com/?q=41.32648034965,19.76565957069</c>.</item>
/// </list>
/// Either may use http or https. Anything else — coordinates on their own, other map links — is
/// rejected, because CRM does not support it.
/// </summary>
public static partial class LocationLinkParser
{
    /// <summary>Shown next to the field when the location is not one of the accepted forms.</summary>
    public const string FormatMessage =
        "Location must be a Google Maps link: https://maps.app.goo.gl/… or http://maps.google.com/?q=latitude,longitude. Coordinates on their own are not accepted.";

    // The short link's ID, optionally followed by the query string the Maps app appends (?g_st=…).
    [GeneratedRegex(@"^https?://maps\.app\.goo\.gl/[A-Za-z0-9_-]+/?(?:\?[^\s]*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ShortLinkPattern();

    [GeneratedRegex(@"^https?://maps\.google\.com/\?q=(-?\d{1,3}(?:\.\d+)?),(-?\d{1,3}(?:\.\d+)?)(?:&[^\s]*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QueryLinkPattern();

    /// <summary>
    /// True when <paramref name="raw"/> is one of the two accepted links. <paramref name="latitude"/>
    /// and <paramref name="longitude"/> are the coordinates of a query link (rounded to six decimal
    /// places, the column's precision) and null for a short link.
    /// </summary>
    public static bool TryParse(string? raw, out decimal? latitude, out decimal? longitude)
    {
        latitude = null;
        longitude = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var link = raw.Trim();

        if (ShortLinkPattern().IsMatch(link))
        {
            return true;
        }

        var match = QueryLinkPattern().Match(link);

        if (!match.Success
            || !TryParseCoordinate(match.Groups[1].Value, -90m, 90m, out var lat)
            || !TryParseCoordinate(match.Groups[2].Value, -180m, 180m, out var lng))
        {
            return false;
        }

        latitude = lat;
        longitude = lng;
        return true;
    }

    private static bool TryParseCoordinate(string token, decimal min, decimal max, out decimal value)
    {
        if (!decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        // decimal(9,6): the fractional part needs rounding to fit the column.
        value = Math.Round(value, 6, MidpointRounding.AwayFromZero);

        return value >= min && value <= max;
    }
}
