using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
using Xunit;

namespace Bitstream.Api.Tests.PostActivation;

/// <summary>The three-level classification rules, against an in-memory repository.</summary>
public sealed class SupportClassificationServiceTests
{
    private readonly FakeSupportClassificationRepository _repository = new();
    private readonly FakeAuditWriter _auditWriter = new();

    private SupportClassificationService CreateService() => new(_repository, new FakeUnitOfWork(), _auditWriter, new FakeClock());

    [Fact]
    public async Task The_level_follows_from_the_parent_and_the_tree_links_them()
    {
        var service = CreateService();

        var complaints = await service.CreateAsync(null, "40", "BITSTREAM COMPLAINTS", 10);
        var network = await service.CreateAsync(complaints.ClassificationId, "400144", "NETWORK", 10);
        var speed = await service.CreateAsync(network.ClassificationId, "4001440011", "Speed Problem", 20);
        await service.CreateAsync(network.ClassificationId, "4001440010", "Cannot access to the internet", 10);

        Assert.Equal(1, complaints.Level);
        Assert.Equal(2, network.Level);
        Assert.Equal(3, speed.Level);
        Assert.Equal(network.ClassificationId, speed.ParentId);

        var tree = await service.GetTreeAsync();
        var level2 = Assert.Single(Assert.Single(tree).Children);
        Assert.Equal(["4001440010", "4001440011"], level2.Children.Select(n => n.Classification.Code));
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "SupportClassification.Created" && e.EntityType == "SupportClassification");
    }

    [Fact]
    public async Task A_level_3_item_cannot_have_children()
    {
        var service = CreateService();
        var l1 = await service.CreateAsync(null, "40", "BITSTREAM COMPLAINTS", 10);
        var l2 = await service.CreateAsync(l1.ClassificationId, "400144", "NETWORK", 10);
        var l3 = await service.CreateAsync(l2.ClassificationId, "4001440010", "Cannot access to the internet", 10);

        var exception = await Assert.ThrowsAsync<SupportClassificationValidationException>(
            () => service.CreateAsync(l3.ClassificationId, "40014400100", "Too deep", 10));

        Assert.True(exception.FieldErrors.ContainsKey("request"));
    }

    [Theory]
    [InlineData("40", "Duplicate", 10, "code")]
    [InlineData("4A", "Letters", 10, "code")]
    [InlineData("", "Empty code", 10, "code")]
    [InlineData("123456789012345678901", "Too long", 10, "code")]
    [InlineData("41", " ", 10, "name")]
    [InlineData("41", "Negative order", -1, "displayOrder")]
    public async Task CreateAsync_rejects_a_duplicate_or_non_digit_code_an_empty_name_or_a_negative_order(
        string code, string name, int displayOrder, string field)
    {
        var service = CreateService();
        await service.CreateAsync(null, "40", "BITSTREAM COMPLAINTS", 10);

        var exception = await Assert.ThrowsAsync<SupportClassificationValidationException>(() => service.CreateAsync(null, code, name, displayOrder));

        Assert.True(exception.FieldErrors.ContainsKey(field));
    }

    [Fact]
    public async Task UpdateAsync_keeps_its_own_code_but_rejects_another_items_code()
    {
        var service = CreateService();
        var complaints = await service.CreateAsync(null, "40", "BITSTREAM COMPLAINTS", 10);
        await service.CreateAsync(null, "43", "BITSTREAM REQUEST", 20);

        var updated = await service.UpdateAsync(complaints.ClassificationId, "40", "Bitstream complaints", 5);
        Assert.Equal("Bitstream complaints", updated.Name);
        Assert.Equal(5, updated.DisplayOrder);
        Assert.Equal(1, updated.Level);

        var exception = await Assert.ThrowsAsync<SupportClassificationValidationException>(
            () => service.UpdateAsync(complaints.ClassificationId, "43", "Bitstream complaints", 5));
        Assert.Contains("BITSTREAM REQUEST", exception.FieldErrors["code"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetActiveAsync_deactivates_and_reactivates_and_audits_both()
    {
        var service = CreateService();
        var item = await service.CreateAsync(null, "40", "BITSTREAM COMPLAINTS", 10);

        await service.SetActiveAsync(item.ClassificationId, isActive: false);
        Assert.False(item.IsActive);

        await service.SetActiveAsync(item.ClassificationId, isActive: true);
        Assert.True(item.IsActive);

        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "SupportClassification.Deactivated");
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "SupportClassification.Activated");
    }

    [Fact]
    public async Task An_unknown_id_is_not_found()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<SupportClassificationNotFoundException>(() => service.UpdateAsync(999, "40", "Name", 10));
        await Assert.ThrowsAsync<SupportClassificationNotFoundException>(() => service.SetActiveAsync(999, isActive: false));
        await Assert.ThrowsAsync<SupportClassificationNotFoundException>(() => service.CreateAsync(999, "40", "Name", 10));
    }
}

public sealed class FakeSupportClassificationRepository : ISupportClassificationRepository
{
    private long _nextId = 1;

    public List<SupportClassification> Items { get; } = [];

    public Task<IReadOnlyList<SupportClassification>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SupportClassification>>([.. Items]);

    public Task<SupportClassification?> FindByIdAsync(long classificationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(c => c.ClassificationId == classificationId));

    public Task<SupportClassification?> FindByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.FirstOrDefault(c => c.Code == code));

    public Task AddAsync(SupportClassification classification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        classification.ClassificationId = _nextId++;
        Items.Add(classification);
        return Task.CompletedTask;
    }
}
