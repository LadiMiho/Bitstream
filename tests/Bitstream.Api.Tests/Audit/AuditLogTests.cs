using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Domain.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Audit;

/// <summary>The Audit Logs screen: access, search and filters, the details drawer and the audited export.</summary>
public sealed class AuditLogTests
{
    private const string AuditorEmail = "auditor@example.com";
    private const string ViewerEmail = "no-audit@example.com";

    private sealed record Seeded(long AuditorId, long AnnaId);

    /// <summary>
    /// Three entries: Anna submitted TRING001 (2 days ago), the system confirmed TRING001 (today),
    /// Anna locked user 42 (today, correlation abc-123).
    /// </summary>
    private static async Task<Seeded> SeedAsync(IdentityApiFactory factory)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        var auditorRole = await IdentitySeeder.AddRoleAsync(db, "Auditor", "audit.read");
        var viewerRole = await IdentitySeeder.AddRoleAsync(db, "Viewer", "activation.read.all");
        var auditor = await IdentitySeeder.AddUserAsync(db, auditorRole, ispId: null, AuditorEmail);
        await IdentitySeeder.AddUserAsync(db, viewerRole, ispId: null, ViewerEmail);
        var anna = await IdentitySeeder.AddUserAsync(db, viewerRole, ispId: null, "anna@example.com");

        var now = DateTimeOffset.UtcNow;
        db.AuditLog.AddRange(
            Entry(now.AddDays(-2), anna.Id, "ActivationRequest.Submitted", "ActivationRequest", "TRING001", null, """{"publicId":"TRING001"}""", "corr-1"),
            Entry(now, null, "ActivationRequest.AutoConfirmed", "ActivationRequest", "TRING001", """{"status":"A"}""", """{"status":"B"}""", "corr-2"),
            Entry(now, anna.Id, "User.Locked", "User", "42", """{"status":"Active"}""", """{"status":"Locked"}""", "abc-123"));
        await db.SaveChangesAsync();

        return new Seeded(auditor.Id, anna.Id);
    }

    private static AuditLog Entry(DateTimeOffset at, long? actor, string action, string entityType, string entityId, string? oldValue, string? newValue, string correlation) => new()
    {
        Timestamp = at,
        ActorUserId = actor,
        ActorIp = actor is null ? "auto-confirmation-job" : "10.0.0.5",
        ActionCode = action,
        EntityType = entityType,
        EntityId = entityId,
        OldValue = oldValue,
        NewValue = newValue,
        CorrelationId = correlation
    };

    private static async Task<HttpClient> SignInAsync(IdentityApiFactory factory, string email)
    {
        var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);
        return client;
    }

    private static async Task<string[]> SearchAsync(HttpClient client, string query)
    {
        var result = await client.GetFromJsonAsync<AuditLogListResponse>(new Uri($"/AccessManagement/AuditLog/Search?{query}", UriKind.Relative));
        return [.. result!.Items.Where(i => !i.ActionCode.EndsWith(".Exported", StringComparison.Ordinal) && !i.ActionCode.StartsWith("Security.", StringComparison.Ordinal)).Select(i => i.ActionCode).Order()];
    }

    [Fact]
    public async Task Without_audit_read_the_screen_and_its_data_are_refused_and_the_menu_hides_it()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory);
        using var client = await SignInAsync(factory, ViewerEmail);

        using var search = await client.GetAsync(new Uri("/AccessManagement/AuditLog/Search", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, search.StatusCode);

        using var page = await client.GetAsync(new Uri("/AccessManagement/AuditLog", UriKind.Relative));
        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);

        var dashboard = await client.GetStringAsync(new Uri("/Dashboard", UriKind.Relative));
        Assert.DoesNotContain("href=\"/AccessManagement/AuditLog\"", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_grid_shows_when_it_was_last_refreshed()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory);
        using var client = await SignInAsync(factory, AuditorEmail);

        var page = await client.GetStringAsync(new Uri("/AccessManagement/AuditLog", UriKind.Relative));

        Assert.Contains("id=\"audit-refresh-status\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"audit-refresh-button\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_filter_narrows_the_entries_and_actor_names_are_shown()
    {
        await using var factory = new IdentityApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory, AuditorEmail);

        Assert.Equal(["ActivationRequest.AutoConfirmed", "ActivationRequest.Submitted", "User.Locked"], await SearchAsync(client, ""));
        Assert.Equal(["ActivationRequest.Submitted", "User.Locked"], await SearchAsync(client, $"actor={seeded.AnnaId}"));
        Assert.Equal(["ActivationRequest.AutoConfirmed"], await SearchAsync(client, "actor=system"));
        Assert.Equal(["User.Locked"], await SearchAsync(client, "entityType=User"));
        Assert.Equal(["ActivationRequest.Submitted"], await SearchAsync(client, "actionCode=ActivationRequest.Submitted"));
        Assert.Equal(["ActivationRequest.AutoConfirmed", "ActivationRequest.Submitted"], await SearchAsync(client, "search=tring001"));
        Assert.Equal(["User.Locked"], await SearchAsync(client, "search=ABC-123"));
        Assert.Equal(["ActivationRequest.Submitted"], await SearchAsync(client, $"before={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}"));

        var page = await client.GetFromJsonAsync<AuditLogListResponse>(new Uri($"/AccessManagement/AuditLog/Search?actor={seeded.AnnaId}&take=1", UriKind.Relative));
        Assert.Single(page!.Items);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal("Test User", page.Items[0].ActorName);

        var system = await client.GetFromJsonAsync<AuditLogListResponse>(new Uri("/AccessManagement/AuditLog/Search?actor=system", UriKind.Relative));
        Assert.True(system!.Items[0].IsSystem);
        Assert.Equal("System", system.Items[0].ActorName);
    }

    [Fact]
    public async Task The_details_drawer_shows_the_before_and_after_values()
    {
        await using var factory = new IdentityApiFactory();
        await SeedAsync(factory);
        using var client = await SignInAsync(factory, AuditorEmail);

        var list = await client.GetFromJsonAsync<AuditLogListResponse>(new Uri("/AccessManagement/AuditLog/Search?actionCode=User.Locked", UriKind.Relative));
        var drawer = await client.GetStringAsync(new Uri($"/AccessManagement/AuditLog/{list!.Items[0].AuditId}/ViewDrawer", UriKind.Relative));

        Assert.Contains("User.Locked", drawer, StringComparison.Ordinal);
        Assert.Contains("&quot;Locked&quot;", drawer, StringComparison.Ordinal);
        Assert.Contains("abc-123", drawer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_export_is_an_Excel_workbook_and_is_itself_audited()
    {
        await using var factory = new IdentityApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = await SignInAsync(factory, AuditorEmail);

        using var response = await client.GetAsync(new Uri("/AccessManagement/AuditLog/Export?entityType=ActivationRequest", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);

        await using (var stream = await response.Content.ReadAsStreamAsync())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        using (var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()))
        {
            var sheet = await reader.ReadToEndAsync();
            Assert.Contains(">ActivationRequest.Submitted<", sheet, StringComparison.Ordinal);
            Assert.Contains(">ActivationRequest.AutoConfirmed<", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain(">User.Locked<", sheet, StringComparison.Ordinal);
        }

        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var exported = await db.AuditLog.AsNoTracking().SingleAsync(a => a.ActionCode == "AuditLog.Exported");
        Assert.Equal(seeded.AuditorId, exported.ActorUserId);
        Assert.Contains("\"rows\":2", exported.NewValue, StringComparison.Ordinal);
    }
}
