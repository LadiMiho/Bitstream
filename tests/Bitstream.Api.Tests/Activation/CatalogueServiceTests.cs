using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>The Packages &amp; contract durations screen's rules, against the in-memory catalogue fake.</summary>
public sealed class CatalogueServiceTests
{
    private readonly FakeActivationCatalogueRepository _catalogue = new();
    private readonly FakeAuditWriter _auditWriter = new();

    public CatalogueServiceTests()
    {
        _catalogue.Packages.Add(new Package { Code = "BITSTREAM_STD", Name = "Standard", Tier = 20, IsActive = true });
        _catalogue.ContractDurations.Add(new ContractDuration { Months = 12, Label = "12 months", IsActive = true });
    }

    private CatalogueService CreateService() => new(_catalogue, new FakeUnitOfWork(), _auditWriter);

    // --- Packages ---------------------------------------------------------------------------

    [Fact]
    public async Task CreatePackageAsync_uppercases_the_code_adds_an_active_package_and_audits_it()
    {
        var package = await CreateService().CreatePackageAsync(" bitstream_ultra ", " Bitstream Ultra ", 40);

        Assert.Equal("BITSTREAM_ULTRA", package.Code);
        Assert.Equal("Bitstream Ultra", package.Name);
        Assert.Equal(40, package.Tier);
        Assert.True(package.IsActive);
        Assert.Contains(package, _catalogue.Packages);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "Package.Created" && e.EntityId == "BITSTREAM_ULTRA");
    }

    [Theory]
    [InlineData("bitstream_std", "Name", 10, "code")]
    [InlineData("BITSTREAM-PRO", "Name", 10, "code")]
    [InlineData("", "Name", 10, "code")]
    [InlineData("BITSTREAM_PRO", " ", 10, "name")]
    [InlineData("BITSTREAM_PRO", "Name", -1, "tier")]
    public async Task CreatePackageAsync_rejects_a_duplicate_or_invalid_code_an_empty_name_or_a_negative_tier(
        string code, string name, int tier, string field)
    {
        var exception = await Assert.ThrowsAsync<CatalogueValidationException>(() => CreateService().CreatePackageAsync(code, name, tier));

        Assert.True(exception.FieldErrors.ContainsKey(field));
    }

    [Fact]
    public async Task UpdatePackageAsync_changes_name_and_tier_but_not_the_code()
    {
        var package = await CreateService().UpdatePackageAsync("BITSTREAM_STD", "Bitstream Standard", 25);

        Assert.Equal("BITSTREAM_STD", package.Code);
        Assert.Equal("Bitstream Standard", package.Name);
        Assert.Equal(25, package.Tier);
    }

    [Fact]
    public async Task UpdatePackageAsync_of_an_unknown_code_is_not_found()
    {
        await Assert.ThrowsAsync<CatalogueNotFoundException>(() => CreateService().UpdatePackageAsync("NOPE", "Name", 1));
    }

    [Fact]
    public async Task SetPackageActiveAsync_deactivates_and_reactivates()
    {
        var service = CreateService();
        var package = _catalogue.Packages.Single();

        await service.SetPackageActiveAsync("BITSTREAM_STD", isActive: false);
        Assert.False(package.IsActive);

        await service.SetPackageActiveAsync("BITSTREAM_STD", isActive: true);
        Assert.True(package.IsActive);

        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "Package.Deactivated");
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "Package.Activated");
    }

    // --- Contract durations -----------------------------------------------------------------

    [Fact]
    public async Task CreateContractDurationAsync_defaults_an_empty_label_to_N_months()
    {
        var duration = await CreateService().CreateContractDurationAsync(36, "  ");

        Assert.Equal(36, duration.Months);
        Assert.Equal("36 months", duration.Label);
        Assert.True(duration.IsActive);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "ContractDuration.Created" && e.EntityId == "36");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(0)]
    [InlineData(121)]
    public async Task CreateContractDurationAsync_rejects_an_existing_or_out_of_range_month_count(int months)
    {
        var exception = await Assert.ThrowsAsync<CatalogueValidationException>(() => CreateService().CreateContractDurationAsync(months, null));

        Assert.True(exception.FieldErrors.ContainsKey("months"));
    }

    [Fact]
    public async Task UpdateContractDurationAsync_changes_the_label()
    {
        var duration = await CreateService().UpdateContractDurationAsync(12, "1 year");

        Assert.Equal("1 year", duration.Label);
    }

    [Fact]
    public async Task SetContractDurationActiveAsync_of_an_unknown_duration_is_not_found()
    {
        await Assert.ThrowsAsync<CatalogueNotFoundException>(() => CreateService().SetContractDurationActiveAsync(48, isActive: false));
    }
}
