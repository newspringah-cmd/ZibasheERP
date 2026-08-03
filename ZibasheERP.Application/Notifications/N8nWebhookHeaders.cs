namespace ZibasheERP.Application.Notifications;

public sealed record N8nWebhookHeaders(
    string EventId,
    string Timestamp,
    string Signature,
    string AuthenticationToken)
{
    public static N8nWebhookHeaders Create(
        Guid eventId,
        string webhookSecret,
        long unixTimestamp,
        string rawBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookSecret);
        ArgumentNullException.ThrowIfNull(rawBody);
        if (eventId == Guid.Empty)
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        if (unixTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(unixTimestamp));

        var timestamp = unixTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = WebhookSignature.Create(webhookSecret, timestamp, rawBody);
        return new N8nWebhookHeaders(
            eventId.ToString(),
            timestamp,
            $"sha256={signature}",
            webhookSecret);
    }
}
