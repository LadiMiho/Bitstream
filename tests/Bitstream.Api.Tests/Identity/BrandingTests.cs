using Bitstream.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>The "one" logo: alone above the sign-in box on the login page, and in the header everywhere else.</summary>
public sealed class BrandingTests
{
    [Fact]
    public async Task The_login_page_shows_the_logo_and_no_page_header()
    {
        await using var factory = new IdentityApiFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/Login", UriKind.Relative));

        Assert.Contains("/img/one-logo.png", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<header", html, StringComparison.Ordinal);
        Assert.Contains("min-h-screen", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_in_page_has_the_header_with_the_logo()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "brand-admin@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "Administrator", "activation.read.all");
            await IdentitySeeder.AddUserAsync(db, role, ispId: null, email);
        }

        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        var html = await client.GetStringAsync(new Uri("/ActivationRequests", UriKind.Relative));

        Assert.Contains("<header", html, StringComparison.Ordinal);
        Assert.Contains("/img/one-logo-header.png", html, StringComparison.Ordinal);
        Assert.Contains("/img/favicon.png", html, StringComparison.Ordinal);
    }
}
