const fs = require('node:fs');
const path = require('node:path');

const code = fs.readFileSync(
  path.join(__dirname, '..', 'code', 'build-invoice-html.js'),
  'utf8');
const event = {
  eventId: '11111111-1111-1111-1111-111111111111',
  eventType: 'InvoiceIssued',
  data: {
    Delivery: { ChatId: '-1001234567890' },
    InvoiceNumber: 'INV-TEST',
    OrderNumber: 'ORD-TEST',
    IssuedAt: '2026-08-03T12:00:00Z',
    PerfumeTotal: 1000,
    BottleTotal: 200,
    TotalAmount: 1200,
    Customer: {
      FullName: '<script>مشتری</script>',
      Mobile: '09120000000'
    },
    Items: [{
      RowNumber: 1,
      PerfumeBrand: 'Brand',
      PerfumeName: 'عطر',
      RequestedVolumeMl: 10,
      PerfumeAmount: 1000,
      IsBottleOwner: true,
      BottleName: 'شیشه',
      LineTotal: 1200
    }]
  }
};

const run = new Function('$input', code);
const [result] = run({ first: () => ({ json: event }) });
if (!result.json.invoiceHtml.includes('&lt;script&gt;') ||
    result.json.invoiceHtml.includes('<script>مشتری')) {
  throw new Error('Invoice renderer did not escape customer input.');
}
if (result.json.invoiceFileName !== 'invoice-INV-TEST.pdf') {
  throw new Error('Invoice renderer produced an unexpected filename.');
}
if (result.json.artifactType !== 'InvoicePdf') {
  throw new Error('Invoice renderer produced an unexpected artifact type.');
}

console.log('Invoice renderer test: PASS');
