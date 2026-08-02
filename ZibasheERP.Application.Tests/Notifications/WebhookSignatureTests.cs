using ZibasheERP.Application.Notifications;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class WebhookSignatureTests
{
    [Fact]
    public void Create_IsDeterministicAndChangesWithPayload()
    {
        const string secret = "a-secure-secret-with-at-least-32-characters";
        var first = WebhookSignature.Create(secret, "1785700000", "{\"id\":1}");
        var second = WebhookSignature.Create(secret, "1785700000", "{\"id\":1}");
        var changed = WebhookSignature.Create(secret, "1785700000", "{\"id\":2}");

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
        Assert.Equal(64, first.Length);
    }
}
