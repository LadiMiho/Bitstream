using Bitstream.Application.Services.Activation;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>TR-ACT-02/03: the location is coordinates only; CRM receives them as a Google Maps link.</summary>
public sealed class LocationParserTests
{
    [Theory]
    [InlineData("39.87781886431,19.98230910308", "39.87781886431,19.98230910308")]
    [InlineData("  39.87781886431,19.98230910308  ", "39.87781886431,19.98230910308")]
    [InlineData("39.87781886431, 19.98230910308", "39.87781886431,19.98230910308")]
    [InlineData("-33.8688,151.2093", "-33.8688,151.2093")]
    [InlineData("41,19", "41,19")]
    [InlineData("90,-180", "90,-180")]
    public void Accepts_coordinates_and_stores_them_without_spaces(string raw, string expected)
    {
        Assert.True(LocationParser.TryParse(raw, out var normalized, out var lat, out var lng));
        Assert.Equal(expected, normalized);
        Assert.NotNull(lat);
        Assert.NotNull(lng);
    }

    [Fact]
    public void Rounds_the_numeric_columns_to_six_decimal_places_but_keeps_the_text_as_typed()
    {
        Assert.True(LocationParser.TryParse("39.87781886431,19.98230910308", out var normalized, out var lat, out var lng));
        Assert.Equal("39.87781886431,19.98230910308", normalized);
        Assert.Equal(39.877819m, lat);
        Assert.Equal(19.982309m, lng);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://maps.google.com/?q=39.87781886431,19.98230910308")]   // links are no longer accepted
    [InlineData("http://maps.google.com/?q=41.3275,19.8187")]
    [InlineData("https://maps.app.goo.gl/wpd59capnslqrxsh7")]
    [InlineData("39.87781886431")]                                              // one number
    [InlineData("39.87781886431;19.98230910308")]                               // wrong separator
    [InlineData("39,87781886431,19,98230910308")]                               // decimal commas
    [InlineData("39.877 19.982")]
    [InlineData("N39.877,E19.982")]
    [InlineData("91,19")]                                                       // out of range
    [InlineData("-90.000001,19")]
    [InlineData("41,180.5")]
    [InlineData("1e1,19")]
    [InlineData("not a location")]
    public void Rejects_anything_else(string? raw)
    {
        Assert.False(LocationParser.TryParse(raw, out var normalized, out var lat, out var lng));
        Assert.Null(normalized);
        Assert.Null(lat);
        Assert.Null(lng);
    }

    [Theory]
    [InlineData("39.87781886431,19.98230910308", "https://maps.google.com/?q=39.87781886431,19.98230910308")]
    [InlineData("-33.8688,151.2093", "https://maps.google.com/?q=-33.8688,151.2093")]
    // Requests submitted before coordinates became the only form already hold a full link.
    [InlineData("https://maps.app.goo.gl/wpd59capnslqrxsh7", "https://maps.app.goo.gl/wpd59capnslqrxsh7")]
    [InlineData("http://maps.google.com/?q=41.3275,19.8187", "http://maps.google.com/?q=41.3275,19.8187")]
    public void Builds_the_link_sent_to_CRM(string stored, string expected)
    {
        Assert.Equal(expected, LocationParser.ToCrmLink(stored));
    }
}
