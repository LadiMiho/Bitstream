using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>The Activation Requests grid's filters and its Excel export, through the real pipeline.</summary>
public sealed class ActivationSearchAndExportTests
{
    private const string AdminEmail = "filters-admin@example.com";
    private const string IspUserEmail = "filters-isp@example.com";

    private sealed record Seeded(long AlphaIspId, long BetaIspId);

    /// <summary>
    /// Alpha: ALPHA_1 (STD, 12 months, Completed, 10 days ago), ALPHA_2 (PRO, 24 months, today).
    /// Beta: BETA_1 (STD, 12 months, today).
    /// </summary>
    private static async Task<Seeded> SeedAsync(IdentityApiFactory factory)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "activation.read.all");
        var ispRole = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.read.own");
        var alpha = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000401");
        var beta = await IdentitySeeder.AddIspAsync(db, "Beta", "L00000402");
        await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, AdminEmail);
        await IdentitySeeder.AddUserAsync(db, ispRole, alpha.IspId, IspUserEmail);
        await ActivationSeeder.SeedCatalogueAsync(db);

        var old = await ActivationSeeder.AddRequestAsync(db, alpha.IspId, "ALPHA_1", ActivationRequestStatus.Completed);
        old.CreatedAt = DateTimeOffset.UtcNow.AddDays(-10);
        var pro = await ActivationSeeder.AddRequestAsync(db, alpha.IspId, "ALPHA_2");
        pro.PackageCode = "BITSTREAM_PRO";
        pro.ContractDurationMonths = 24;
        await ActivationSeeder.AddRequestAsync(db, beta.IspId, "BETA_1");
        await db.SaveChangesAsync();

        return new Seeded(alpha.IspId, beta.IspId);
    }

    private static async Task<HttpClient> SignInAsync(IdentityApiFactory factory, string email)
    {
        var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);
        return client;
    }

    private static async Task<string[]> SearchAsync(HttpClient client, string query)
    {
        var result = await client.GetFromJsonAsync<ActivationRequestListResponse>(new Uri($"/ActivationRequests/Search?{query}", UriKind.Relative));
        return [.. result!.Items.Select(i => i.PublicId).Order()];
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    [Fact]
    public async Task Each_filter_narrows_the_grid()
    {
        await using var factory = new IdentityApiFactory();
        var seeded = await SeedAsync(factory);
        using var admin = await SignInAsync(factory, AdminEmail);

        Assert.Equal(["ALPHA_1", "ALPHA_2", "BETA_1"], await SearchAsync(admin, ""));
        Assert.Equal(["ALPHA_1", "ALPHA_2"], await SearchAsync(admin, $"ispId={seeded.AlphaIspId}"));
        Assert.Equal(["ALPHA_2"], await SearchAsync(admin, "packageCode=BITSTREAM_PRO"));
        Assert.Equal(["ALPHA_2"], await SearchAsync(admin, "contractDurationMonths=24"));
        Assert.Equal(["ALPHA_1"], await SearchAsync(admin, "status=Completed"));
        Assert.Equal(["ALPHA_2", "BETA_1"], await SearchAsync(admin, $"submittedFrom={Iso(DateTimeOffset.UtcNow.AddDays(-1))}"));
        Assert.Equal(["ALPHA_1"], await SearchAsync(admin, $"submittedBefore={Iso(DateTimeOffset.UtcNow.AddDays(-1))}"));
        Assert.Equal(["BETA_1"], await SearchAsync(admin, $"ispId={seeded.BetaIspId}&packageCode=BITSTREAM_STD&contractDurationMonths=12"));
    }

    [Fact]
    public async Task The_ISP_filter_lists_every_ISP_for_whoever_sees_every_request_and_is_absent_for_an_ISP_user()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory);

        // The admin role here has activation.read.all only — no ISP Administration permission.
        using var admin = await SignInAsync(factory, AdminEmail);
        var adminPage = await admin.GetStringAsync(new Uri("/ActivationRequests", UriKind.Relative));
        Assert.Contains("id=\"activation-filter-isp\"", adminPage, StringComparison.Ordinal);
        Assert.Contains(">Alpha</option>", adminPage, StringComparison.Ordinal);
        Assert.Contains(">Beta</option>", adminPage, StringComparison.Ordinal);
        Assert.Contains("id=\"activation-export-button\"", adminPage, StringComparison.Ordinal);

        using var ispUser = await SignInAsync(factory, IspUserEmail);
        var ispPage = await ispUser.GetStringAsync(new Uri("/ActivationRequests", UriKind.Relative));
        Assert.DoesNotContain("id=\"activation-filter-isp\"", ispPage, StringComparison.Ordinal);
        Assert.Contains("id=\"activation-filter-package\"", ispPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_grid_shows_the_package_name_falling_back_to_the_code_when_unknown()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory);
        using var admin = await SignInAsync(factory, AdminEmail);

        var result = await admin.GetFromJsonAsync<ActivationRequestListResponse>(new Uri("/ActivationRequests/Search", UriKind.Relative));
        var byId = result!.Items.ToDictionary(i => i.PublicId);

        Assert.Equal("Standard", byId["BETA_1"].PackageName);
        Assert.Equal(12, byId["BETA_1"].ContractDurationMonths);
        Assert.Equal("BITSTREAM_PRO", byId["ALPHA_2"].PackageName); // not in the catalogue
    }

    [Fact]
    public async Task An_ISP_user_cannot_filter_their_way_into_another_ISPs_requests()
    {
        await using var factory = new IdentityApiFactory();
        var seeded = await SeedAsync(factory);
        using var ispUser = await SignInAsync(factory, IspUserEmail);

        Assert.Equal(["ALPHA_1", "ALPHA_2"], await SearchAsync(ispUser, ""));
        Assert.Equal(["ALPHA_1", "ALPHA_2"], await SearchAsync(ispUser, $"ispId={seeded.BetaIspId}"));
    }

    [Fact]
    public async Task The_export_is_an_Excel_workbook_of_every_matching_request_across_pages()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory);
        using var admin = await SignInAsync(factory, AdminEmail);

        // One row per page in the grid, but the export ignores paging.
        Assert.Single(await SearchAsync(admin, "packageCode=BITSTREAM_STD&take=1"));

        using var response = await admin.GetAsync(new Uri("/ActivationRequests/Export?packageCode=BITSTREAM_STD", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".xlsx", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);

        var sheet = await ReadSheetAsync(response);
        Assert.Contains(">Request<", sheet, StringComparison.Ordinal);
        Assert.Contains(">ALPHA_1<", sheet, StringComparison.Ordinal);
        Assert.Contains(">BETA_1<", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain(">ALPHA_2<", sheet, StringComparison.Ordinal);
        Assert.Contains(">Completed<", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ISP_users_export_holds_only_their_own_requests()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory);
        using var ispUser = await SignInAsync(factory, IspUserEmail);

        using var response = await ispUser.GetAsync(new Uri("/ActivationRequests/Export", UriKind.Relative));
        var sheet = await ReadSheetAsync(response);

        Assert.Contains(">ALPHA_1<", sheet, StringComparison.Ordinal);
        Assert.Contains(">ALPHA_2<", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("BETA_1", sheet, StringComparison.Ordinal);
    }

    private static async Task<string> ReadSheetAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));

        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return await reader.ReadToEndAsync();
    }
}
