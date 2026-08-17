using Bitstream.Domain.Enums;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>
/// Exhaustive proof of the TRD 5.3 state table: every ordered pair of statuses is checked, so
/// this covers not only every permitted transition but every one that must be rejected —
/// including self-transitions and jumps that skip a step (e.g. Submitted straight to
/// Completed), none of which the state machine allows.
/// </summary>
public sealed class ActivationRequestTransitionsTests
{
    /// <summary>Exactly the table in <see cref="ActivationRequestTransitions"/>, restated independently so this test does not just re-assert the production map against itself.</summary>
    private static readonly HashSet<(ActivationRequestStatus From, ActivationRequestStatus To)> Permitted =
    [
        (ActivationRequestStatus.Submitted, ActivationRequestStatus.PendingCrmSync),
        (ActivationRequestStatus.Submitted, ActivationRequestStatus.AwaitingGisVerification),
        (ActivationRequestStatus.PendingCrmSync, ActivationRequestStatus.AwaitingGisVerification),
        (ActivationRequestStatus.PendingCrmSync, ActivationRequestStatus.IntegrationFailed),
        (ActivationRequestStatus.AwaitingGisVerification, ActivationRequestStatus.RejectedNoLine),
        (ActivationRequestStatus.AwaitingGisVerification, ActivationRequestStatus.LineAvailable),
        (ActivationRequestStatus.RejectedNoLine, ActivationRequestStatus.Closed),
        (ActivationRequestStatus.LineAvailable, ActivationRequestStatus.SalesOrderOpened),
        (ActivationRequestStatus.SalesOrderOpened, ActivationRequestStatus.InProvisioning),
        (ActivationRequestStatus.InProvisioning, ActivationRequestStatus.Completed),
        (ActivationRequestStatus.IntegrationFailed, ActivationRequestStatus.PendingCrmSync)
    ];

    private static IEnumerable<ActivationRequestStatus> AllStatuses => Enum.GetValues<ActivationRequestStatus>();

    public static IEnumerable<object[]> AllOrderedPairs()
    {
        foreach (var from in AllStatuses)
        {
            foreach (var to in AllStatuses)
            {
                yield return [from, to];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllOrderedPairs))]
    public void IsPermitted_matches_the_TRD_5_3_table_exactly(ActivationRequestStatus from, ActivationRequestStatus to)
    {
        var expected = Permitted.Contains((from, to));

        Assert.Equal(expected, ActivationRequestTransitions.IsPermitted(from, to));
    }

    [Fact]
    public void No_status_permits_a_transition_to_itself()
    {
        Assert.All(AllStatuses, status => Assert.False(ActivationRequestTransitions.IsPermitted(status, status)));
    }

    [Fact]
    public void Closed_and_Completed_are_terminal()
    {
        Assert.Empty(ActivationRequestTransitions.PermittedFrom(ActivationRequestStatus.Closed));
        Assert.Empty(ActivationRequestTransitions.PermittedFrom(ActivationRequestStatus.Completed));
    }

    [Fact]
    public void Every_non_terminal_status_has_at_least_one_permitted_transition()
    {
        var nonTerminal = AllStatuses.Except([ActivationRequestStatus.Closed, ActivationRequestStatus.Completed]);

        Assert.All(nonTerminal, status => Assert.NotEmpty(ActivationRequestTransitions.PermittedFrom(status)));
    }
}
