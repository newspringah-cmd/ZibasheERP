using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.N8n;

public sealed record N8nDeliveryResult(bool IsSuccessful, string? Error = null);

public interface IN8nWebhookSender
{
    Task<N8nDeliveryResult> SendAsync(
        NotificationOutbox notification,
        CancellationToken cancellationToken = default);
}

public sealed class N8nWebhookSender : IN8nWebhookSender, IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly N8nOptions _options;

    public N8nWebhookSender(IOptions<N8nOptions> options)
    {
        _options = options.Value;
    }

    public async Task<N8nDeliveryResult> SendAsync(
        NotificationOutbox notification,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var payloadDocument = JsonDocument.Parse(notification.Payload);
            var envelope = new
            {
                eventId = notification.Id,
                eventType = notification.EventType,
                occurredAt = notification.CreatedAt,
                customerId = notification.CustomerId,
                orderId = notification.OrderId,
                data = payloadDocument.RootElement
            };
            var body = JsonSerializer.Serialize(envelope);
            var headers = N8nWebhookHeaders.Create(
                notification.Id,
                _options.WebhookSecret,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                body);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.WebhookUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Zibashe-Event-Id", headers.EventId);
            request.Headers.Add("X-Zibashe-Timestamp", headers.Timestamp);
            request.Headers.Add("X-Zibashe-Signature", headers.Signature);
            request.Headers.Add("X-Zibashe-Webhook-Token", headers.AuthenticationToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new N8nDeliveryResult(true);
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            return new N8nDeliveryResult(
                false,
                $"n8n returned HTTP {(int)response.StatusCode}: {error}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return new N8nDeliveryResult(false, exception.Message);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
