using Xunit;
using ZibasheERP.Application.Notifications;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class TelegramDeliveryFailurePolicyTests
{
    [Fact]
    public void PermanentGroupAccessErrors_AreDetected()
    {
        Assert.True(TelegramDeliveryFailurePolicy.IsPermanentGroupAccessFailure(
            "-1001234567890",
            "Forbidden: bot was kicked from the supergroup chat"));
        Assert.True(TelegramDeliveryFailurePolicy.IsPermanentGroupAccessFailure(
            "-123456",
            "Bad Request: not enough rights to send text messages to the chat"));
    }

    [Fact]
    public void TemporaryOrPrivateChatErrors_AreNotGroupAccessFailures()
    {
        Assert.False(TelegramDeliveryFailurePolicy.IsPermanentGroupAccessFailure(
            "-1001234567890",
            "The operation timed out"));
        Assert.False(TelegramDeliveryFailurePolicy.IsPermanentGroupAccessFailure(
            "123456789",
            "Bad Request: chat not found"));
    }
}
