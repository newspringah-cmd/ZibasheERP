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
}
