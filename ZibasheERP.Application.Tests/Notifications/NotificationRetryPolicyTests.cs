using ZibasheERP.Application.Notifications;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class NotificationRetryPolicyTests
{
    [Fact]
    public void DelayAfter_UsesExponentialBackoffWithMaximum()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), NotificationRetryPolicy.DelayAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(1), NotificationRetryPolicy.DelayAfter(2));
        Assert.Equal(TimeSpan.FromMinutes(16), NotificationRetryPolicy.DelayAfter(6));
        Assert.Equal(TimeSpan.FromMinutes(30), NotificationRetryPolicy.DelayAfter(20));
    }
}
