const fs = require('node:fs');
const path = require('node:path');

const code = fs.readFileSync(
  path.join(__dirname, '..', 'code', 'validate-event-metadata.js'),
  'utf8');
const run = new Function('$input', 'Date', code);
const nowSeconds = 1785758400;
const FixedDate = class extends Date {
  constructor(...args) {
    super(...(args.length ? args : [nowSeconds * 1000]));
  }
  static now() { return nowSeconds * 1000; }
};

function webhook(overrides = {}) {
  const eventId = '11111111-1111-4111-8111-111111111111';
  return {
    headers: {
      'x-zibashe-event-id': eventId,
      'x-zibashe-timestamp': String(nowSeconds),
      'x-zibashe-signature': `sha256=${'a'.repeat(64)}`,
      ...(overrides.headers ?? {})
    },
    body: {
      eventId,
      eventType: 'InvoiceIssued',
      customerId: '22222222-2222-4222-8222-222222222222',
      orderId: '33333333-3333-4333-8333-333333333333',
      data: {
        Delivery: {
          Channel: 'TelegramGroup',
          ChatId: '-1001234567890',
          Title: 'Test group',
          Username: null
        }
      },
      ...(overrides.body ?? {})
    }
  };
}

function execute(input) {
  return run({ first: () => ({ json: input }) }, FixedDate);
}

const [accepted] = execute(webhook());
if (!accepted.json.deliveryReady || accepted.json.eventType !== 'InvoiceIssued') {
  throw new Error('Valid event was not accepted correctly.');
}

const [withoutDelivery] = execute(webhook({ body: { data: { Delivery: null } } }));
if (withoutDelivery.json.deliveryReady) {
  throw new Error('Null delivery was marked ready.');
}

for (const [name, invalid] of [
  ['stale timestamp', webhook({ headers: { 'x-zibashe-timestamp': String(nowSeconds - 301) } })],
  ['mismatched event ID', webhook({ body: { eventId: '99999999-9999-4999-8999-999999999999' } })],
  ['invalid signature format', webhook({ headers: { 'x-zibashe-signature': 'invalid' } })],
  ['private delivery target', webhook({ body: { data: { Delivery: { Channel: 'TelegramGroup', ChatId: '123', Title: 'Private' } } } })]
]) {
  let rejected = false;
  try { execute(invalid); } catch { rejected = true; }
  if (!rejected) throw new Error(`${name} was not rejected.`);
}

console.log('Event metadata validator test: PASS');
