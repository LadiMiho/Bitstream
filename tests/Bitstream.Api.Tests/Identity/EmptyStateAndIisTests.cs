using Bitstream.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>Empty-grid placeholders, and the IIS setting that lets edits (PUT/PATCH) through.</summary>
public sealed class EmptyStateAndIisTests
{
    private static async Task<HttpClient> SignedInAdminAsync(IdentityApiFactory factory)
    {
        const string email = "empty-admin@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "Administrator", "activation.read.all", "activation.create", "catalogue.manage");
            await IdentitySeeder.AddUserAsync(db, role, ispId: null, email);
        }

        var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);
        return client;
    }

    [Fact]
    public async Task An_empty_catalogue_shows_placeholders_with_an_add_button()
    {
        await using var factory = new IdentityApiFactory();
        using var client = await SignedInAdminAsync(factory);

        var html = await client.GetStringAsync(new Uri("/ActivationRequests/Catalogue", UriKind.Relative));

        Assert.Contains("No packages yet", html, StringComparison.Ordinal);
        Assert.Contains("No contract durations yet", html, StringComparison.Ordinal);
        Assert.Contains("class=\"empty-state\"", html, StringComparison.Ordinal);
        Assert.Contains("data-catalogue-action=\"add\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_activation_grid_has_both_empty_state_variants()
    {
        await using var factory = new IdentityApiFactory();
        using var client = await SignedInAdminAsync(factory);

        var html = await client.GetStringAsync(new Uri("/ActivationRequests", UriKind.Relative));

        Assert.Contains("id=\"activation-empty-none\"", html, StringComparison.Ordinal);
        Assert.Contains("No activation requests yet", html, StringComparison.Ordinal);
        Assert.Contains("id=\"activation-empty-filtered\"", html, StringComparison.Ordinal);
        Assert.Contains("Clear search and filters", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_activation_grid_shows_when_it_was_last_refreshed()
    {
        await using var factory = new IdentityApiFactory();
        using var client = await SignedInAdminAsync(factory);

        var html = await client.GetStringAsync(new Uri("/ActivationRequests", UriKind.Relative));

        Assert.Contains("id=\"activation-refresh-status\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"activation-refresh-button\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bitstream.Web")]
    [InlineData("Bitstream.Api")]
    public void IIS_WebDAV_is_removed_so_edits_are_not_answered_405(string project)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Bitstream.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        var webConfig = File.ReadAllText(Path.Combine(root, "src", project, "web.config"));

        Assert.Contains("<remove name=\"WebDAVModule\" />", webConfig, StringComparison.Ordinal);
        Assert.Contains("<remove name=\"WebDAV\" />", webConfig, StringComparison.Ordinal);
    }
}
