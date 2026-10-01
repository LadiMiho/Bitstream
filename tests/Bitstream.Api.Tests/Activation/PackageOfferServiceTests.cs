using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>The Package offers screen's rules, against the in-memory catalogue fake.</summary>
public sealed class PackageOfferServiceTests
{
    private readonly FakeActivationCatalogueRepository _catalogue = new();
    private readonly FakeAuditWriter _auditWriter = new();

    public PackageOfferServiceTests()
    {
        _catalogue.Packages.AddRange(
        [
            new Package { Code = "BITSTREAM_STD", Name = "Standard", Tier = 20, IsActive = true },
            new Package { Code = "BITSTREAM_PRO", Name = "Professional", Tier = 30, IsActive = true }
        ]);
        _catalogue.ContractDurations.AddRange(
        [
            new ContractDuration { Months = 12, Label = "12 months", IsActive = true },
            new ContractDuration { Months = 24, Label = "24 months", IsActive = true }
        ]);
        _catalogue.PackageOffers.Add(
            new PackageOffer { PackageCode = "BITSTREAM_STD", ContractDurationMonths = 12, OfferCode = "5100020013", IsActive = true });
    }

    private PackageOfferService CreateService() => new(_catalogue, new FakeUnitOfWork(), _auditWriter);

    [Fact]
    public async Task CreateAsync_adds_an_active_offer_with_the_trimmed_code_and_audits_it()
    {
        var offer = await CreateService().CreateAsync(new CreatePackageOfferRequest("BITSTREAM_PRO", 24, " 5100020099 "));

        Assert.Equal("5100020099", offer.OfferCode);
        Assert.True(offer.IsActive);
        Assert.Contains(offer, _catalogue.PackageOffers);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "PackageOffer.Created" && e.EntityId == "BITSTREAM_PRO/24");
    }

    [Fact]
    public async Task CreateAsync_rejects_a_pair_that_already_has_an_offer()
    {
        var exception = await Assert.ThrowsAsync<PackageOfferValidationException>(() =>
            CreateService().CreateAsync(new CreatePackageOfferRequest("BITSTREAM_STD", 12, "5100020099")));

        Assert.True(exception.FieldErrors.ContainsKey("contractDurationMonths"));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_code_already_used_by_another_pair()
    {
        var exception = await Assert.ThrowsAsync<PackageOfferValidationException>(() =>
            CreateService().CreateAsync(new CreatePackageOfferRequest("BITSTREAM_PRO", 12, "5100020013")));

        Assert.True(exception.FieldErrors.ContainsKey("offerCode"));
    }

    [Theory]
    [InlineData("UNKNOWN", 12, "packageCode")]
    [InlineData("BITSTREAM_PRO", 36, "contractDurationMonths")]
    [InlineData("BITSTREAM_PRO", 12, "offerCode")]
    public async Task CreateAsync_rejects_an_unknown_package_or_duration_or_an_empty_code(string packageCode, int months, string field)
    {
        var offerCode = field == "offerCode" ? "  " : "5100020099";

        var exception = await Assert.ThrowsAsync<PackageOfferValidationException>(() =>
            CreateService().CreateAsync(new CreatePackageOfferRequest(packageCode, months, offerCode)));

        Assert.True(exception.FieldErrors.ContainsKey(field));
    }

    [Fact]
    public async Task UpdateAsync_changes_the_code_and_allows_keeping_the_same_one()
    {
        var service = CreateService();

        await service.UpdateAsync("BITSTREAM_STD", 12, new UpdatePackageOfferRequest("5100020013"));
        var offer = await service.UpdateAsync("BITSTREAM_STD", 12, new UpdatePackageOfferRequest("5100020014"));

        Assert.Equal("5100020014", offer.OfferCode);
    }

    [Fact]
    public async Task UpdateAsync_of_a_missing_pair_is_not_found()
    {
        await Assert.ThrowsAsync<PackageOfferNotFoundException>(() =>
            CreateService().UpdateAsync("BITSTREAM_PRO", 24, new UpdatePackageOfferRequest("5100020099")));
    }

    [Fact]
    public async Task SetActiveAsync_deactivates_and_reactivates_an_offer()
    {
        var service = CreateService();
        var offer = _catalogue.PackageOffers.Single();

        await service.SetActiveAsync("BITSTREAM_STD", 12, isActive: false);
        Assert.False(offer.IsActive);

        await service.SetActiveAsync("BITSTREAM_STD", 12, isActive: true);
        Assert.True(offer.IsActive);

        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "PackageOffer.Deactivated");
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "PackageOffer.Activated");
    }
}
