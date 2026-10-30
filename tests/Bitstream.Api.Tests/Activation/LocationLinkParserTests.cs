using Bitstream.Application.Services.Activation;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>TR-ACT-02/03: only the two Google Maps link forms CRM supports are accepted.</summary>
public sealed class LocationLinkParserTests
{
    [Theory]
    [InlineData("https://maps.app.goo.gl/wpd59capnslqrxsh7")]
    [InlineData("http://maps.app.goo.gl/wpd59capnslqrxsh7")]
    [InlineData("  https://maps.app.goo.gl/wpd59capnslqrxsh7  ")]
    [InlineData("https://maps.app.goo.gl/wpd59capnslqrxsh7?g_st=iw")]
    public void Accepts_a_short_link_which_carries_no_coordinates(string raw)
    {
        Assert.True(LocationLinkParser.TryParse(raw, out var lat, out var lng));
        Assert.Null(lat);
        Assert.Null(lng);
    }

    [Theory]
    [InlineData("http://maps.google.com/?q=41.32648034965,19.76565957069", 41.32648, 19.76566)]
    [InlineData("https://maps.google.com/?q=41.3275,19.8187", 41.3275, 19.8187)]
    [InlineData("http://maps.google.com/?q=-41.3275,-19.8187", -41.3275, -19.8187)]
    public void Accepts_a_query_link_and_reads_its_coordinates(string raw, double expectedLat, double expectedLng)
    {
        Assert.True(LocationLinkParser.TryParse(raw, out var lat, out var lng));
        Assert.Equal((decimal)expectedLat, Math.Round(lat!.Value, 5));
        Assert.Equal((decimal)expectedLng, Math.Round(lng!.Value, 5));
    }

    [Fact]
    public void Rounds_to_six_decimal_places_to_fit_the_decimal_9_6_column()
    {
        Assert.True(LocationLinkParser.TryParse("http://maps.google.com/?q=41.32648034965,19.76565957069", out var lat, out var lng));
        Assert.Equal(41.326480m, lat);
        Assert.Equal(19.765660m, lng);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("41.3275,19.8187")]                                          // coordinates on their own
    [InlineData("41.3275, 19.8187")]
    [InlineData("not a location")]
    [InlineData("https://www.google.com/maps/@41.3275,19.8187,15z")]         // other Google Maps links
    [InlineData("https://www.google.com/maps?q=41.3275,19.8187")]
    [InlineData("https://maps.google.com/maps/place/Tirana/@41.3275,19.8187,17z")]
    [InlineData("https://maps.example.com/?q=41.3275,19.8187")]              // other hosts
    [InlineData("https://maps.app.goo.gl/")]                                 // short link with no ID
    [InlineData("https://maps.app.goo.gl.evil.com/abc")]
    [InlineData("http://maps.google.com/?q=Tirana")]                         // query without coordinates
    [InlineData("http://maps.google.com/?q=91,19")]                          // out of range
    [InlineData("http://maps.google.com/?q=41,181")]
    public void Rejects_anything_else(string? raw)
    {
        Assert.False(LocationLinkParser.TryParse(raw, out var lat, out var lng));
        Assert.Null(lat);
        Assert.Null(lng);
    }
}
