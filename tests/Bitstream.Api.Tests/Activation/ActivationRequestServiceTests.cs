using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>
/// TRD 5: activation request submission, the GIS verification branches and sales order
/// application. Unit-tested against hand-written fakes (see Fakes.cs and
/// tests/Bitstream.Api.Tests/Identity/Fakes.cs), the same style as
/// <c>AdministrationServiceTests</c> — no database, no CRM adapter, no stored procedure.
/// </summary>
public sealed class ActivationRequestServiceTests
{
    private readonly FakeActivationRequestRepository _requestRepository = new();
    private readonly FakeIspRepository _ispRepository = new();
    private readonly FakeActivationCatalogueRepository _catalogueRepository = new();
    private readonly FakePublicIdentifierGenerator _identifierGenerator = new();
    private readonly FakeIntegrationOutbox _outbox = new();
    private readonly FakeAuditWriter _auditWriter = new();
    private readonly FakeCurrentUserContext _currentUser = new() { UserId = 1, RoleName = "IspUser", IspId = 1 };
    private readonly FakeClock _clock = new();

    public ActivationRequestServiceTests()
    {
        _catalogueRepository.Packages.AddRange(
        [
            new Package { Code = "BITSTREAM_STD", Name = "Standard", Tier = 20, IsActive = true },
            new Package { Code = "BITSTREAM_OLD", Name = "Retired", Tier = 5, IsActive = false }
        ]);
        _catalogueRepository.Classifications.Add(
            new ActivationClassification { Code = "REQUEST_FOR_ACTIVATION", Name = "Request for Activation", IsDefault = true, IsActive = true });
        _catalogueRepository.ContractDurations.AddRange(
        [
            new ContractDuration { Months = 12, Label = "12 months", IsActive = true },
            new ContractDuration { Months = 24, Label = "24 months", IsActive = true }
        ]);
    }

    private ActivationRequestService CreateService() =>
        new(
            _requestRepository,
            _ispRepository,
            _catalogueRepository,
            _identifierGenerator,
            _outbox,
            new FakeUnitOfWork(),
            _auditWriter,
            _clock,
            _currentUser);

    private Isp AddActiveIsp(long ispId = 1)
    {
        var isp = new Isp
        {
            IspId = ispId,
            Name = "Alpha",
            Nipt = "L1",
            ContactPerson = "A",
            ContactEmail = "a@example.com",
            ContactMobile = "+355691234567",
            CrmBpReference = "BP1",
            Status = IspStatus.Active
        };
        _ispRepository.Isps[ispId] = isp;
        return isp;
    }

    private static SubmitActivationRequest ValidRequest(long ispId = 1, string? comments = null) =>
        new(ispId, "BITSTREAM_STD", "41.3275,19.8187", "REQUEST_FOR_ACTIVATION", 12, comments);

    [Fact]
    public async Task SubmitAsync_issues_an_identifier_persists_Submitted_then_moves_to_PendingCrmSync()
    {
        AddActiveIsp();
        var service = CreateService();

        var result = await service.SubmitAsync(ValidRequest());

        Assert.Equal("ISP_1", result.PublicId);
        Assert.Equal(ActivationRequestStatus.PendingCrmSync, result.Status);
        Assert.Equal(41.3275m, result.LocationLat);
        Assert.Equal(19.8187m, result.LocationLng);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "ActivationRequest.Submitted");
    }

    [Fact]
    public async Task SubmitAsync_enqueues_INT_CRM_01_but_never_calls_CRM_directly()
    {
        // INT-CRM-02 needs the Business Partner INT-CRM-01 returns, so it is not enqueued here
        // at all — OutboxDispatcher enqueues it once INT-CRM-01 actually succeeds (see
        // Integration/CrmClosureEndToEndTests, which drives both messages through the real
        // dispatcher and a fake CRM).
        AddActiveIsp();
        var service = CreateService();

        await service.SubmitAsync(ValidRequest());

        var message = Assert.Single(_outbox.Outbound);
        Assert.Equal("INT-CRM-01", message.InterfaceCode);
        Assert.Equal(TargetSystem.Crm, message.TargetSystem);
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_package_not_in_the_catalogue()
    {
        AddActiveIsp();
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<ActivationRequestValidationException>(() =>
            service.SubmitAsync(ValidRequest() with { PackageCode = "NOPE" }));

        Assert.Contains(exception.Violations, v => v.Contains("catalogue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_retired_package()
    {
        AddActiveIsp();
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<ActivationRequestValidationException>(() =>
            service.SubmitAsync(ValidRequest() with { PackageCode = "BITSTREAM_OLD" }));

        Assert.Contains(exception.Violations, v => v.Contains("no longer offered", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not a location")]
    [InlineData("")]
    [InlineData("91,19")]
    public async Task SubmitAsync_rejects_an_unparsable_or_out_of_range_location(string locationRaw)
    {
        AddActiveIsp();
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<ActivationRequestValidationException>(() =>
            service.SubmitAsync(ValidRequest() with { LocationRaw = locationRaw }));

        Assert.Contains(exception.Violations, v => v.Contains("Location", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_contract_duration_not_offered()
    {
        AddActiveIsp();
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<ActivationRequestValidationException>(() =>
            service.SubmitAsync(ValidRequest() with { ContractDurationMonths = 6 }));

        Assert.Contains(exception.Violations, v => v.Contains("Contract duration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmitAsync_rejects_comments_over_2000_characters()
    {
        AddActiveIsp();
        var service = CreateService();
        var tooLong = new string('a', 2001);

        var exception = await Assert.ThrowsAsync<ActivationRequestValidationException>(() =>
            service.SubmitAsync(ValidRequest(comments: tooLong)));

        Assert.Contains(exception.Violations, v => v.Contains("2000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmitAsync_strips_HTML_from_comments()
    {
        AddActiveIsp();
        var service = CreateService();

        var result = await service.SubmitAsync(ValidRequest(comments: "<b>Urgent</b> please <i>expedite</i>"));

        Assert.Equal("Urgent please expedite", result.Comments);
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_locked_ISP()
    {
        var isp = AddActiveIsp();
        isp.Status = IspStatus.Locked;
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<ActivationRequestValidationException>(() => service.SubmitAsync(ValidRequest()));

        Assert.Contains(exception.Violations, v => v.Contains("locked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmitAsync_rejects_an_ISP_user_submitting_for_a_different_ISP()
    {
        AddActiveIsp(1);
        AddActiveIsp(2);
        _currentUser.IspId = 1;
        var service = CreateService();

        await Assert.ThrowsAsync<ActivationRequestValidationException>(() => service.SubmitAsync(ValidRequest(ispId: 2)));
    }

    [Fact]
    public async Task SubmitAsync_allows_an_internal_caller_to_submit_for_any_ISP()
    {
        AddActiveIsp(2);
        _currentUser.IspId = null;
        _currentUser.RoleName = "Administrator";
        var service = CreateService();

        var result = await service.SubmitAsync(ValidRequest(ispId: 2));

        Assert.Equal(2, result.IspId);
    }

    [Fact]
    public async Task RecordGisOutcomeAsync_line_available_transitions_to_LineAvailable()
    {
        var request = SeedRequest(ActivationRequestStatus.AwaitingGisVerification);
        var service = CreateService();

        await service.RecordGisOutcomeAsync(request.RequestId, lineAvailable: true, reason: null);

        Assert.Equal(ActivationRequestStatus.LineAvailable, request.Status);
        Assert.Null(request.StatusReason);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "ActivationRequest.GisOutcomeRecorded");
    }

    [Fact]
    public async Task RecordGisOutcomeAsync_no_line_requires_a_reason()
    {
        var request = SeedRequest(ActivationRequestStatus.AwaitingGisVerification);
        var service = CreateService();

        await Assert.ThrowsAsync<ActivationRequestValidationException>(() =>
            service.RecordGisOutcomeAsync(request.RequestId, lineAvailable: false, reason: null));
    }

    [Fact]
    public async Task RecordGisOutcomeAsync_no_line_with_a_reason_transitions_to_RejectedNoLine()
    {
        var request = SeedRequest(ActivationRequestStatus.AwaitingGisVerification);
        var service = CreateService();

        await service.RecordGisOutcomeAsync(request.RequestId, lineAvailable: false, reason: "No fibre in the area");

        Assert.Equal(ActivationRequestStatus.RejectedNoLine, request.Status);
        Assert.Equal("No fibre in the area", request.StatusReason);
    }

    [Theory]
    [InlineData(ActivationRequestStatus.Submitted)]
    [InlineData(ActivationRequestStatus.PendingCrmSync)]
    [InlineData(ActivationRequestStatus.LineAvailable)]
    [InlineData(ActivationRequestStatus.Completed)]
    public async Task RecordGisOutcomeAsync_rejects_a_request_not_awaiting_GIS_verification(ActivationRequestStatus currentStatus)
    {
        var request = SeedRequest(currentStatus);
        var service = CreateService();

        await Assert.ThrowsAsync<ActivationRequestConflictException>(() =>
            service.RecordGisOutcomeAsync(request.RequestId, lineAvailable: true, reason: null));

        // Rejected outright: the status must not have moved.
        Assert.Equal(currentStatus, request.Status);
    }

    [Fact]
    public async Task RecordGisOutcomeAsync_throws_not_found_for_an_unknown_request()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ActivationRequestNotFoundException>(() =>
            service.RecordGisOutcomeAsync(999, lineAvailable: true, reason: null));
    }

    [Fact]
    public async Task ApplySalesOrderAsync_transitions_LineAvailable_to_SalesOrderOpened()
    {
        var request = SeedRequest(ActivationRequestStatus.LineAvailable);
        var service = CreateService();

        await service.ApplySalesOrderAsync(request.PublicId, "SO-12345");

        Assert.Equal(ActivationRequestStatus.SalesOrderOpened, request.Status);
        Assert.Equal("SO-12345", request.SalesOrderId);
    }

    [Fact]
    public async Task ApplySalesOrderAsync_rejects_a_request_not_yet_LineAvailable()
    {
        var request = SeedRequest(ActivationRequestStatus.AwaitingGisVerification);
        var service = CreateService();

        await Assert.ThrowsAsync<ActivationRequestConflictException>(() => service.ApplySalesOrderAsync(request.PublicId, "SO-12345"));
    }

    [Fact]
    public async Task GetByPublicIdAsync_returns_null_for_an_ISP_users_cross_ISP_request()
    {
        var request = SeedRequest(ActivationRequestStatus.Submitted, ispId: 2);
        _currentUser.IspId = 1;
        var service = CreateService();

        var result = await service.GetByPublicIdAsync(request.PublicId);

        Assert.Null(result);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "Security.AccessDenied.CrossIsp");
    }

    [Fact]
    public async Task GetByPublicIdAsync_returns_the_request_for_its_own_ISP()
    {
        var request = SeedRequest(ActivationRequestStatus.Submitted, ispId: 1);
        _currentUser.IspId = 1;
        var service = CreateService();

        var result = await service.GetByPublicIdAsync(request.PublicId);

        Assert.NotNull(result);
        Assert.Equal(request.RequestId, result.RequestId);
    }

    [Fact]
    public async Task GetByPublicIdAsync_lets_activation_read_all_see_any_ISPs_request()
    {
        var request = SeedRequest(ActivationRequestStatus.Submitted, ispId: 2);
        _currentUser.IspId = null;
        _currentUser.Permissions.Add(ActivationPermissionCodes.ActivationReadAll);
        var service = CreateService();

        var result = await service.GetByPublicIdAsync(request.PublicId);

        Assert.NotNull(result);
    }

    private ActivationRequest SeedRequest(ActivationRequestStatus status, long ispId = 1)
    {
        var request = new ActivationRequest
        {
            RequestId = _requestRepository.Requests.Count + 1,
            PublicId = $"ISP_{_requestRepository.Requests.Count + 1}",
            IspId = ispId,
            PackageCode = "BITSTREAM_STD",
            LocationRaw = "41.3275,19.8187",
            LocationLat = 41.3275m,
            LocationLng = 19.8187m,
            Classification = "REQUEST_FOR_ACTIVATION",
            ContractDurationMonths = 12,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _requestRepository.Requests[request.RequestId] = request;
        return request;
    }
}
