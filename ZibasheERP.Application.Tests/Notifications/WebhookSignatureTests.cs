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

    [Fact]
    public void N8nHeaders_Create_BindsAuthenticationAndSignatureToEvent()
    {
        var eventId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        const string secret = "a-secure-secret-with-at-least-32-characters";
        const long timestamp = 1785758400;
        const string body = "{\"eventId\":\"11111111-1111-4111-8111-111111111111\"}";

        var headers = N8nWebhookHeaders.Create(eventId, secret, timestamp, body);

        Assert.Equal(eventId.ToString(), headers.EventId);
        Assert.Equal(timestamp.ToString(), headers.Timestamp);
        Assert.Equal(secret, headers.AuthenticationToken);
        Assert.Equal($"sha256={WebhookSignature.Create(secret, timestamp.ToString(), body)}", headers.Signature);
    }
}
