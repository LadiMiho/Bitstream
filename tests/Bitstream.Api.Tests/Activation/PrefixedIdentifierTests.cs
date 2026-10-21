using Bitstream.Infrastructure.Persistence;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>Activation request IDs are ticket code + number, whatever version of the database procedure issued them.</summary>
public sealed class PrefixedIdentifierTests
{
    [Theory]
    [InlineData("TRINGGPON", "TRINGGPON_011", "TRINGGPON011")]
    [InlineData("TRINGGPON", "TRINGGPON011", "TRINGGPON011")]
    [InlineData("TRING", "TRING_1000", "TRING1000")]
    [InlineData("TRING", "TRINGX_001", "TRINGX_001")]
    public void The_separator_after_the_ticket_code_is_removed(string prefix, string issued, string expected) =>
        Assert.Equal(expected, PrefixedIdentifier.WithoutSeparator(prefix, issued));
}
