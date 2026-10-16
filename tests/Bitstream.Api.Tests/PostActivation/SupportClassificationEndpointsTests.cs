using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.PostActivation;

/// <summary>The classifications screen through the real HTTP pipeline: RBAC, the JSON actions and the page.</summary>
public sealed class SupportClassificationEndpointsTests
{
    private const string Base = "/PostActivation/Classifications";

    private static async Task<HttpClient> SignedInAsync(IdentityApiFactory factory, string email, params string[] permissions)
    {
        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "Role-" + email, permissions);
            await IdentitySeeder.AddUserAsync(db, role, ispId: null, email);
        }

        var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);
        return client;
    }

    [Fact]
    public async Task Without_catalogue_manage_the_page_and_the_writes_are_forbidden()
    {
        await using var factory = new IdentityApiFactory();
        using var client = await SignedInAsync(factory, "auditor@example.com", "activation.read.all");

        using var create = await client.PostAsJsonAsync(new Uri(Base, UriKind.Relative), new CreateSupportClassificationHttpRequest(null, "40", "BITSTREAM COMPLAINTS", 10));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        using var page = await client.GetAsync(new Uri(Base, UriKind.Relative));
        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task Three_linked_levels_are_created_shown_and_edited()
    {
        await using var factory = new IdentityApiFactory();
        using var client = await SignedInAsync(factory, "classifications-admin@example.com", "catalogue.manage");

        var l1 = await CreateAsync(client, null, "43", "BITSTREAM REQUEST");
        var l2 = await CreateAsync(client, l1, "430015", "Contract CANCELLATION");
        var l3 = await CreateAsync(client, l2, "4300150012", "HIGH TARIFFS");

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var item = await db.SupportClassifications.AsNoTracking().SingleAsync(c => c.ClassificationId == l3);
            Assert.Equal(3, item.Level);
            Assert.Equal(l2, item.ParentId);
        }

        var html = await client.GetStringAsync(new Uri($"{Base}?l1={l1}&l2={l2}", UriKind.Relative));
        Assert.Contains("4300150012", html, StringComparison.Ordinal);
        Assert.Contains("HIGH TARIFFS", html, StringComparison.Ordinal);

        using var duplicate = await client.PostAsJsonAsync(new Uri(Base, UriKind.Relative), new CreateSupportClassificationHttpRequest(l2, "4300150012", "Again", 20));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        using var edit = await client.PutAsJsonAsync(new Uri($"{Base}/{l3}", UriKind.Relative), new UpdateSupportClassificationHttpRequest("4300150012", "High tariffs", 30));
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        using var deactivate = await client.PatchAsJsonAsync(new Uri($"{Base}/{l3}/status", UriKind.Relative), new SetActiveHttpRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        using var missing = await client.PatchAsJsonAsync(new Uri($"{Base}/999999/status", UriKind.Relative), new SetActiveHttpRequest(false));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var item = await db.SupportClassifications.AsNoTracking().SingleAsync(c => c.ClassificationId == l3);
            Assert.Equal("High tariffs", item.Name);
            Assert.False(item.IsActive);
        }

        var drawer = await client.GetStringAsync(new Uri($"{Base}/AddDrawer?parentId={l2}", UriKind.Relative));
        Assert.Contains("value=\"4300150013\"", drawer, StringComparison.Ordinal);
    }

    private static async Task<long> CreateAsync(HttpClient client, long? parentId, string code, string name)
    {
        using var response = await client.PostAsJsonAsync(new Uri(Base, UriKind.Relative), new CreateSupportClassificationHttpRequest(parentId, code, name, 10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("classificationId").GetInt64();
    }
}
