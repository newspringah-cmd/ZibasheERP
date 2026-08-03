// Place this immediately after an n8n Webhook node protected by Header Auth.
// Configure Header Auth to require X-Zibashe-Webhook-Token using an encrypted n8n credential.
const webhook = $input.first().json;
const headers = Object.fromEntries(
  Object.entries(webhook.headers ?? {}).map(([key, value]) => [key.toLowerCase(), value]));
const body = typeof webhook.body === 'string' ? JSON.parse(webhook.body) : webhook.body;

function reject(message) {
  throw new Error(`Rejected Zibashe event: ${message}`);
}

const eventIdHeader = String(headers['x-zibashe-event-id'] ?? '').toLowerCase();
const timestampHeader = String(headers['x-zibashe-timestamp'] ?? '');
const signatureHeader = String(headers['x-zibashe-signature'] ?? '');
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

if (!body || typeof body !== 'object' || Array.isArray(body)) reject('body is not a JSON object');
if (!uuidPattern.test(eventIdHeader)) reject('event ID header is invalid');
if (String(body.eventId ?? '').toLowerCase() !== eventIdHeader) reject('event ID header and body differ');
if (!/^sha256=[0-9a-f]{64}$/i.test(signatureHeader)) reject('HMAC signature header format is invalid');

const timestamp = Number(timestampHeader);
if (!Number.isSafeInteger(timestamp) || timestamp <= 0) reject('timestamp is invalid');
const ageSeconds = Math.abs(Math.floor(Date.now() / 1000) - timestamp);
if (ageSeconds > 300) reject('timestamp is outside the five-minute window');

const allowedTypes = new Set(['InvoiceIssued', 'OrderDecanted', 'OrderShipped']);
if (!allowedTypes.has(body.eventType)) reject('event type is not supported');
if (!uuidPattern.test(String(body.customerId ?? ''))) reject('customer ID is invalid');
if (!uuidPattern.test(String(body.orderId ?? ''))) reject('order ID is invalid');
if (!body.data || typeof body.data !== 'object' || Array.isArray(body.data)) reject('data is invalid');

const delivery = body.data.Delivery;
if (delivery !== null) {
  if (!delivery || delivery.Channel !== 'TelegramGroup') reject('delivery channel is invalid');
  if (!/^-[1-9][0-9]*$/.test(String(delivery.ChatId ?? ''))) reject('delivery ChatId is invalid');
  if (!String(delivery.Title ?? '').trim()) reject('delivery title is missing');
}

return [{
  json: {
    ...body,
    deliveryReady: delivery !== null,
    receivedAt: new Date().toISOString()
  }
}];
