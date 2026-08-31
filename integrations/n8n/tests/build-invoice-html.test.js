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
    InvoiceId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
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
    PaymentAccounts: [
      { CardNumber: '6280231544451379', BankName: 'مسکن', AccountHolder: 'زیباشی' },
      { CardNumber: '6037997450926374', BankName: 'ملی', AccountHolder: 'زیباشی' }
    ],
    Items: [{
      RowNumber: 1,
      PerfumeBrand: 'Brand',
      PerfumeEnglishName: 'English Perfume',
      PerfumePersianName: 'عطر فارسی',
      RequestedVolumeMl: 10,
      PerfumeAmount: 1000,
      IsBottleOwner: true,
      BottleName: 'شیشه',
      BottlePrice: 200,
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
if (!result.json.invoiceHtml.includes('__INVOICE_BACKGROUND_DATA_URI__') ||
    !result.json.invoiceHtml.includes('class="background"') ||
    !result.json.invoiceHtml.includes('English Perfume') ||
    !result.json.invoiceHtml.includes('عطر فارسی') ||
    !result.json.invoiceHtml.includes('class="invoice-row"') ||
    !result.json.invoiceHtml.includes('قیمت شیشه') ||
    !result.json.invoiceHtml.includes('class="total final"') ||
    result.json.invoiceHtml.includes('هزینه ارسال')) {
  throw new Error('Invoice renderer did not render data over the fixed background.');
}
if (!result.json.invoiceHtml.includes('class="field date"') ||
    result.json.invoiceHtml.includes('جمع شیشه</span>')) {
  throw new Error('Invoice renderer did not use the Persian date or combined total layout.');
}
if (!result.json.telegramCaption.includes('INV-TEST')) {
  throw new Error('Invoice renderer did not prepare the PDF Telegram caption.');
}
if (!result.json.telegramCaption.includes('مبلغ قابل پرداخت') ||
    result.json.paidCallbackData !== 'invoicepay:paid:aaaaaaaabbbbccccddddeeeeeeeeeeee' ||
    result.json.waitingCallbackData !== 'invoicepay:waiting:aaaaaaaabbbbccccddddeeeeeeeeeeee' ||
    result.json.telegramCaption.length > 1024) {
  throw new Error('Invoice renderer did not prepare the combined Telegram document message.');
}
if (result.json.firstCardCopyText !== '6280231544451379' ||
    result.json.secondCardCopyText !== '6037997450926374' ||
    !result.json.firstCardCopyLabel.includes('مسکن') ||
    !result.json.secondCardCopyLabel.includes('ملی')) {
  throw new Error('Invoice renderer did not prepare payment card copy buttons.');
}

const paginationEvent = structuredClone(event);
paginationEvent.data.Items = Array.from({ length: 9 }, (_, index) => ({
  ...event.data.Items[0],
  RowNumber: index + 1,
  PerfumePersianName: `عطر ${index + 1}`
}));
const [paginatedResult] = run({ first: () => ({ json: paginationEvent }) });
const pageCount = (paginatedResult.json.invoiceHtml.match(/<main class="page">/g) ?? []).length;
const finalTotalCount = (paginatedResult.json.invoiceHtml.match(/class="total final"/g) ?? []).length;
if (pageCount !== 2 || finalTotalCount !== 1 ||
    !paginatedResult.json.invoiceHtml.includes('صفحه ۲ از ۲')) {
  throw new Error('Invoice renderer did not paginate after eight rows.');
}

console.log('Invoice renderer test: PASS');
