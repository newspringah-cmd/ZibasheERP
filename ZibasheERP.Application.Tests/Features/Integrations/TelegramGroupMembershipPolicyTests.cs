using Xunit;
using ZibasheERP.Application.Features.Integrations.TrackTelegramGroupMembership;

namespace ZibasheERP.Application.Tests.Features.Integrations;

public sealed class TelegramGroupMembershipPolicyTests
{
    [Fact]
    public void CanDeliver_MemberOrAdministrator_ReturnsTrue()
    {
        Assert.True(TelegramGroupMembershipPolicy.CanDeliver("member", null, null));
        Assert.True(TelegramGroupMembershipPolicy.CanDeliver("administrator", null, null));
    }

    [Fact]
    public void CanDeliver_RemovedOrBlocked_ReturnsFalse()
    {
        Assert.False(TelegramGroupMembershipPolicy.CanDeliver("left", null, null));
        Assert.False(TelegramGroupMembershipPolicy.CanDeliver("kicked", null, null));
    }

    [Fact]
    public void CanDeliver_Restricted_RequiresMembershipAndSendPermission()
    {
        Assert.True(TelegramGroupMembershipPolicy.CanDeliver("restricted", true, true));
        Assert.False(TelegramGroupMembershipPolicy.CanDeliver("restricted", true, false));
        Assert.False(TelegramGroupMembershipPolicy.CanDeliver("restricted", false, true));
    }

    [Fact]
    public void ApplyAvailability_ActiveLinkIsMarkedForRestorationWhenBotLeaves()
    {
        var result = TelegramGroupMembershipPolicy.ApplyAvailability(
            isActive: true,
            restoreOnBotRejoin: false,
            canDeliver: false);

        Assert.False(result.IsActive);
        Assert.True(result.RestoreOnBotRejoin);
    }

    [Fact]
    public void ApplyAvailability_BotRejoinRestoresOnlyPreviouslyActiveLink()
    {
        var recoverable = TelegramGroupMembershipPolicy.ApplyAvailability(
            isActive: false,
            restoreOnBotRejoin: true,
            canDeliver: true);
        var historicalInactive = TelegramGroupMembershipPolicy.ApplyAvailability(
            isActive: false,
            restoreOnBotRejoin: false,
            canDeliver: true);

        Assert.True(recoverable.IsActive);
        Assert.False(recoverable.RestoreOnBotRejoin);
        Assert.False(historicalInactive.IsActive);
        Assert.False(historicalInactive.RestoreOnBotRejoin);
    }

    [Fact]
    public void ApplyAvailability_RepeatedUnavailableEventKeepsRestorationMarker()
    {
        var result = TelegramGroupMembershipPolicy.ApplyAvailability(
            isActive: false,
            restoreOnBotRejoin: true,
            canDeliver: false);

        Assert.False(result.IsActive);
        Assert.True(result.RestoreOnBotRejoin);
    }
}
