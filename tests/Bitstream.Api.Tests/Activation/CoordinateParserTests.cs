using Bitstream.Application.Services.Activation;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>TR-ACT-02/03: location as entered, parsed into normalised coordinates.</summary>
public sealed class CoordinateParserTests
{
    [Theory]
    [InlineData("41.3275, 19.8187", 41.3275, 19.8187)]
    [InlineData("-41.3275,-19.8187", -41.3275, -19.8187)]
    [InlineData("  41.3275 , 19.8187  ", 41.3275, 19.8187)]
    public void Parses_a_bare_coordinate_pair(string raw, decimal expectedLat, decimal expectedLng)
    {
        Assert.True(CoordinateParser.TryParse(raw, out var lat, out var lng));
        Assert.Equal(expectedLat, lat);
        Assert.Equal(expectedLng, lng);
    }

    [Theory]
    [InlineData("https://www.google.com/maps/@41.3275,19.8187,15z", 41.3275, 19.8187)]
    [InlineData("https://maps.google.com/maps/place/Tirana/@41.327500,19.818700,17z/data=abc", 41.327500, 19.818700)]
    public void Parses_the_at_marker_in_a_map_url(string raw, decimal expectedLat, decimal expectedLng)
    {
        Assert.True(CoordinateParser.TryParse(raw, out var lat, out var lng));
        Assert.Equal(expectedLat, lat);
        Assert.Equal(expectedLng, lng);
    }

    [Theory]
    [InlineData("https://www.google.com/maps?q=41.3275,19.8187", 41.3275, 19.8187)]
    [InlineData("https://maps.example.com/?ll=41.3275,19.8187&z=15", 41.3275, 19.8187)]
    public void Parses_a_query_parameter_in_a_map_url(string raw, decimal expectedLat, decimal expectedLng)
    {
        Assert.True(CoordinateParser.TryParse(raw, out var lat, out var lng));
        Assert.Equal(expectedLat, lat);
        Assert.Equal(expectedLng, lng);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a location")]
    [InlineData("https://www.google.com/maps/search/pizza")]
    public void Rejects_input_with_no_recognisable_coordinate(string? raw)
    {
        Assert.False(CoordinateParser.TryParse(raw, out var lat, out var lng));
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Theory]
    [InlineData("91,19")]   // latitude out of range
    [InlineData("41,181")]  // longitude out of range
    [InlineData("-91,19")]
    [InlineData("41,-181")]
    public void Rejects_a_coordinate_pair_out_of_range(string raw)
    {
        Assert.False(CoordinateParser.TryParse(raw, out _, out _));
    }

    [Fact]
    public void Rounds_to_six_decimal_places_to_fit_the_decimal_9_6_column()
    {
        Assert.True(CoordinateParser.TryParse("41.32751234567,19.81871234567", out var lat, out var lng));
        Assert.Equal(41.327512m, lat);
        Assert.Equal(19.818712m, lng);
    }
}
