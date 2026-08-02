using Xunit;
using ZibasheERP.Application.Notifications;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class N8nDeliveryTargetValidatorTests
{
    [Fact]
    public void MatchesTelegramGroup_RequiresExactEventDestination()
    {
        const string payload = "{\"Delivery\":{\"Channel\":\"TelegramGroup\",\"ChatId\":\"-100123\"}}";

        Assert.True(N8nDeliveryTargetValidator.MatchesTelegramGroup(payload, "-100123"));
        Assert.False(N8nDeliveryTargetValidator.MatchesTelegramGroup(payload, "-100456"));
    }

    [Fact]
    public void MatchesTelegramGroup_InvalidOrMissingDelivery_ReturnsFalse()
    {
        Assert.False(N8nDeliveryTargetValidator.MatchesTelegramGroup("{}", "-100123"));
        Assert.False(N8nDeliveryTargetValidator.MatchesTelegramGroup("invalid", "-100123"));
    }
}
