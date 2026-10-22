using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Domain.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.PostActivation;

/// <summary>The Post-Activation grid's filters, paging and Excel export, through the real pipeline.</summary>
public sealed class PostActivationSearchAndExportTests
{
    private const string AdminEmail = "pa-filters-admin@example.com";
    private const string IspUserEmail = "pa-filters-isp@example.com";

    private sealed record Seeded(long TringIspId, long AbcIspId);

    /// <summary>
    /// PAR_1 Tring, complaint (40 › 400144), Submitted, 10 days ago.
    /// PAR_2 Tring, request (43 › 430015), PendingCrmSync, today.
    /// PAR_3 Abc, complaint (40 › 400144), Failed, today.
    /// </summary>
    private static async Task<Seeded> SeedAsync(IdentityApiFactory factory)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "ticket.read.all");
        var ispRole = await IdentitySeeder.AddRoleAsync(db, "IspUser", "ticket.read.own");
        var tring = await IdentitySeeder.AddIspAsync(db, "Tring", "L00000601", "TRING");
        var abc = await IdentitySeeder.AddIspAsync(db, "Abc", "L00000602", "ABC");
        await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, AdminEmail);
        await IdentitySeeder.AddUserAsync(db, ispRole, tring.IspId, IspUserEmail);

        var now = DateTimeOffset.UtcNow;
        var complaint = new SupportClassification { Code = "40", Name = "BITSTREAM COMPLAINTS", Level = 1, CreatedAt = now };
        var network = new SupportClassification { Code = "400144", Name = "NETWORK", Level = 2, Parent = complaint, CreatedAt = now };
        var speed = new SupportClassification { Code = "4001440011", Name = "Speed Problem", Level = 3, Parent = network, CreatedAt = now };
        var request = new SupportClassification { Code = "43", Name = "BITSTREAM REQUEST", Level = 1, DisplayOrder = 20, CreatedAt = now };
        var cancellation = new SupportClassification { Code = "430015", Name = "Contract CANCELLATION", Level = 2, Parent = request, CreatedAt = now };
        var tariffs = new SupportClassification { Code = "4300150012", Name = "HIGH TARIFFS", Level = 3, Parent = cancellation, CreatedAt = now };
        db.SupportClassifications.AddRange(complaint, network, speed, request, cancellation, tariffs);
        await db.SaveChangesAsync();

        PostActivationRequest Request(string publicId, long ispId, string phone, SupportClassification l1, SupportClassification l2, SupportClassification l3, string status, DateTimeOffset createdAt) => new()
        {
            PublicId = publicId,
            IspId = ispId,
            Phonenumber = phone,
            ClassificationId = l3.ClassificationId,
            Classification1Code = l1.Code,
            Classification1Name = l1.Name,
            Classification2Code = l2.Code,
            Classification2Name = l2.Name,
            Classification3Code = l3.Code,
            Classification3Name = l3.Name,
            Status = status,
            CreatedAt = createdAt
        };

        db.PostActivationRequests.AddRange(
            Request("PAR_1", tring.IspId, "TRING_101", complaint, network, speed, PostActivationRequestStatus.Submitted, now.AddDays(-10)),
            Request("PAR_2", tring.IspId, "TRING_102", request, cancellation, tariffs, PostActivationRequestStatus.PendingCrmSync, now),
            Request("PAR_3", abc.IspId, "ABC_201", complaint, network, speed, PostActivationRequestStatus.Failed, now));
        await db.SaveChangesAsync();

        return new Seeded(tring.IspId, abc.IspId);
    }

    private static async Task<HttpClient> SignInAsync(IdentityApiFactory factory, string email)
    {
        var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);
        return client;
    }

    private static async Task<string[]> SearchAsync(HttpClient client, string query)
    {
        var result = await client.GetFromJsonAsync<PostActivationRequestListResponse>(new Uri($"/PostActivation/Search?{query}", UriKind.Relative));
        return [.. result!.Items.Select(i => i.PublicId).Order()];
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    [Fact]
    public async Task Each_filter_narrows_the_grid_and_paging_reports_the_total()
    {
        await using var factory = new IdentityApiFactory();
        var seeded = await SeedAsync(factory);
        using var admin = await SignInAsync(factory, AdminEmail);

        Assert.Equal(["PAR_1", "PAR_2", "PAR_3"], await SearchAsync(admin, ""));
        Assert.Equal(["PAR_1", "PAR_2"], await SearchAsync(admin, $"ispId={seeded.TringIspId}"));
        Assert.Equal(["PAR_3"], await SearchAsync(admin, "status=Failed"));
        Assert.Equal(["PAR_1", "PAR_3"], await SearchAsync(admin, "classification1Code=40"));
        Assert.Equal(["PAR_2"], await SearchAsync(admin, "classification2Code=430015"));
        Assert.Equal(["PAR_2", "PAR_3"], await SearchAsync(admin, $"submittedFrom={Iso(DateTimeOffset.UtcNow.AddDays(-1))}"));
        Assert.Equal(["PAR_1"], await SearchAsync(admin, $"submittedBefore={Iso(DateTimeOffset.UtcNow.AddDays(-1))}"));
        Assert.Equal(["PAR_3"], await SearchAsync(admin, "search=abc_2"));
        Assert.Equal(["PAR_2"], await SearchAsync(admin, "search=par_2"));

        var page = await admin.GetFromJsonAsync<PostActivationRequestListResponse>(new Uri("/PostActivation/Search?take=1&skip=1", UriKind.Relative));
        Assert.Single(page!.Items);
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task An_ISP_user_cannot_filter_their_way_into_another_ISPs_requests()
    {
        await using var factory = new IdentityApiFactory();
        var seeded = await SeedAsync(factory);
        using var ispUser = await SignInAsync(factory, IspUserEmail);

        Assert.Equal(["PAR_1", "PAR_2"], await SearchAsync(ispUser, ""));
        Assert.Equal(["PAR_1", "PAR_2"], await SearchAsync(ispUser, $"ispId={seeded.AbcIspId}"));

        var page = await ispUser.GetStringAsync(new Uri("/PostActivation", UriKind.Relative));
        Assert.DoesNotContain("id=\"pa-filter-isp\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"pa-filter-type\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_export_holds_every_matching_request_across_pages_and_only_the_users_own_ISPs()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory);

        using var admin = await SignInAsync(factory, AdminEmail);
        using var response = await admin.GetAsync(new Uri("/PostActivation/Export?classification1Code=40", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);

        var sheet = await ReadSheetAsync(response);
        Assert.Contains(">PAR_1<", sheet, StringComparison.Ordinal);
        Assert.Contains(">PAR_3<", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain(">PAR_2<", sheet, StringComparison.Ordinal);
        Assert.Contains(">Speed Problem<", sheet, StringComparison.Ordinal);

        using var ispUser = await SignInAsync(factory, IspUserEmail);
        using var ownResponse = await ispUser.GetAsync(new Uri("/PostActivation/Export", UriKind.Relative));
        var ownSheet = await ReadSheetAsync(ownResponse);
        Assert.Contains(">PAR_1<", ownSheet, StringComparison.Ordinal);
        Assert.Contains(">PAR_2<", ownSheet, StringComparison.Ordinal);
        Assert.DoesNotContain("PAR_3", ownSheet, StringComparison.Ordinal);

        // TR-REP-08: both exports are in the audit log.
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        Assert.Equal(2, db.AuditLog.Count(a => a.ActionCode == "PostActivationRequest.Exported"));
    }

    private static async Task<string> ReadSheetAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return await reader.ReadToEndAsync();
    }
}
