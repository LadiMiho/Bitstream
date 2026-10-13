using Bitstream.Api.Tests.Identity;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>The dashboard: an ISP user sees only their own ISP's figures; an administrator sees every ISP and the CRM integration.</summary>
public sealed class DashboardTests
{
    private static async Task<(long OwnIspId, long OtherIspId)> SeedAsync(IdentityApiFactory factory, string ispUserEmail, string adminEmail)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        var ispRole = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.read.own");
        var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "activation.read.all");
        var own = await IdentitySeeder.AddIspAsync(db, "Own ISP", "L00000970", "OWN");
        var other = await IdentitySeeder.AddIspAsync(db, "Other ISP", "L00000971", "OTHER");
        await IdentitySeeder.AddUserAsync(db, ispRole, own.IspId, ispUserEmail);
        await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, adminEmail);

        await ActivationSeeder.AddRequestAsync(db, own.IspId, "OWN_001", ActivationRequestStatus.AwaitingOperatorConfirmation);
        await ActivationSeeder.AddRequestAsync(db, own.IspId, "OWN_002", ActivationRequestStatus.Completed);
        await ActivationSeeder.AddRequestAsync(db, other.IspId, "OTHER_001", ActivationRequestStatus.PendingCrmSync);

        return (own.IspId, other.IspId);
    }

    [Fact]
    public async Task An_ISP_user_sees_only_their_own_ISPs_requests()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory, "isp-dash@example.com", "admin-dash@example.com");
        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, "isp-dash@example.com");

        var html = await client.GetStringAsync(new Uri("/Dashboard", UriKind.Relative));

        Assert.Contains("Own ISP", html, StringComparison.Ordinal);
        Assert.Contains("OWN_001", html, StringComparison.Ordinal);
        Assert.Contains("OWN_002", html, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER_001", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Other ISP", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Messages to CRM", html, StringComparison.Ordinal);
        Assert.Contains("waiting for the operator", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_sees_every_ISP_and_the_CRM_integration()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory, "isp-dash2@example.com", "admin-dash2@example.com");
        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, "admin-dash2@example.com");

        var html = await client.GetStringAsync(new Uri("/Dashboard", UriKind.Relative));

        Assert.Contains("All ISPs", html, StringComparison.Ordinal);
        Assert.Contains("OWN_001", html, StringComparison.Ordinal);
        Assert.Contains("OTHER_001", html, StringComparison.Ordinal);
        Assert.Contains("ISPs with most requests", html, StringComparison.Ordinal);
        Assert.Contains("Messages to CRM", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_home_page_and_sign_in_lead_to_the_dashboard()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory, "isp-dash3@example.com", "admin-dash3@example.com");
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, "isp-dash3@example.com");

        using var home = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal("/Dashboard", home.Headers.Location?.OriginalString);

        using var login = await client.GetAsync(new Uri("/Login", UriKind.Relative));
        Assert.Equal("/Dashboard", login.Headers.Location?.OriginalString);
    }
}
