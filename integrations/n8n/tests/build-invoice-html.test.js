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

const run = new Function('$input', '$env', code);
const [result] = run({ first: () => ({ json: event }) });
if (!result.json.invoiceHtml.includes('&lt;script&gt;') ||
    result.json.invoiceHtml.includes('<script>مشتری')) {
  throw new Error('Invoice renderer did not escape customer input.');
}
if (result.json.invoiceFileName !== 'INV-TEST.pdf') {
  throw new Error('Invoice renderer produced an unexpected filename.');
}
const usernameEvent = structuredClone(event);
usernameEvent.data.Customer.Username = '@test_customer';
const [usernameResult] = run({ first: () => ({ json: usernameEvent }) });
if (usernameResult.json.invoiceFileName !== 'INV-TEST.pdf') {
  throw new Error('Invoice renderer did not use the invoice number as the PDF filename.');
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
if (!result.json.invoiceHtml.includes('grid-template-rows:repeat(8,1fr)') ||
    !result.json.invoiceHtml.includes('font-size:10px;line-height:1.2') ||
    result.json.invoiceHtml.includes('flex:1;font-size:8.3px')) {
  throw new Error('Invoice rows are not fixed to readable aligned table slots.');
}
if (result.json.telegramCaption.includes('شماره فاکتور:') ||
    result.json.telegramCaption.includes('INV-TEST')) {
  throw new Error('Invoice renderer leaked the invoice number into the PDF Telegram caption.');
}
if (!result.json.telegramCaption.includes('مبلغ قابل پرداخت') ||
    result.json.paidCallbackData !== 'invoicepay:paid:aaaaaaaabbbbccccddddeeeeeeeeeeee' ||
    result.json.waitingCallbackData !== 'invoicepay:waiting:aaaaaaaabbbbccccddddeeeeeeeeeeee' ||
    result.json.telegramCaption.length > 1024) {
  throw new Error('Invoice renderer did not prepare the combined Telegram document message.');
}

const inventoryEvent = structuredClone(event);
inventoryEvent.data.IsInventory = true;
const [inventoryResult] = run({ first: () => ({ json: inventoryEvent }) });
if (!inventoryResult.json.telegramCaption.startsWith('🔴🔴موجودی🔴🔴\n')) {
  throw new Error('Inventory invoice caption did not start with its inventory marker.');
}

const giftGiverEvent = structuredClone(event);
giftGiverEvent.data.Items[0].IsGift = true;
giftGiverEvent.data.Items[0].GiftRecipientUsername = '@MB_Sama';
const [giftGiverResult] = run({ first: () => ({ json: giftGiverEvent }) });
if (!giftGiverResult.json.telegramCaption.includes('۱۰ میل — ۱٬۲۰۰ تومان') ||
    !giftGiverResult.json.telegramCaption.includes('هدیه به @MB_Sama')) {
  throw new Error('Gift-giver invoice caption did not name the gift recipient.');
}

const giftRecipientEvent = structuredClone(giftGiverEvent);
giftRecipientEvent.data.GiftDeliveryRole = 'Recipient';
giftRecipientEvent.data.GiverUsername = '@gift_giver';
const [giftRecipientResult] = run({ first: () => ({ json: giftRecipientEvent }) });
if (!giftRecipientResult.json.telegramCaption.includes('هدیه از طرف @gift_giver')) {
  throw new Error('Gift-recipient invoice caption did not name the gift giver.');
}

const combinedGiftEvent = structuredClone(event);
combinedGiftEvent.data.Items.push({
  ...event.data.Items[0],
  RowNumber: 2,
  IsGift: true,
  GiftRecipientUsername: '@combined_receiver'
});
const [combinedGiftResult] = run({ first: () => ({ json: combinedGiftEvent }) });
if (!combinedGiftResult.json.telegramCaption.includes('هدیه به @combined_receiver')) {
  throw new Error('Combined invoice caption did not name its gift recipient.');
}
if (result.json.firstCardCopyText !== '6280231544451379' ||
    result.json.secondCardCopyText !== '6037997450926374' ||
    !result.json.firstCardCopyLabel.includes('مسکن') ||
    !result.json.secondCardCopyLabel.includes('ملی')) {
  throw new Error('Invoice renderer did not prepare payment card copy buttons.');
}

const manualReviewEvent = structuredClone(event);
manualReviewEvent.data.Delivery = null;
const [manualReviewResult] = run({ first: () => ({ json: manualReviewEvent }) });
if (!manualReviewResult.json.isManualReview ||
    manualReviewResult.json.invoiceDeliveryChatId !== '' ||
    !manualReviewResult.json.telegramCaption.includes('بررسی دستی')) {
  throw new Error('Invoice renderer did not prepare the manual-review delivery package.');
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

const halfEvent = structuredClone(event);
halfEvent.data.IsInventory = true;
halfEvent.data.Items[0].RequestedVolumeMl = 1.5;
halfEvent.data.Items[0].PerfumePricePerMl = 100000;
halfEvent.data.Items[0].LineTotal = 150000;
halfEvent.data.TotalAmount = 150000;
const [halfResult] = run({ first: () => ({ json: halfEvent }) });
if (!halfResult.json.telegramCaption.includes('۱٫۵ میل') || !halfResult.json.invoiceHtml.includes('۱٫۵')) {
  throw new Error('Half ml volume was rounded in the invoice caption or PDF.');
}

const transferredPaid = structuredClone(event);
transferredPaid.data.IsTransferredInvoice = true;
transferredPaid.data.IsPaid = true;
transferredPaid.data.PaidAmount = 1200;
transferredPaid.data.OutstandingAmount = 0;
transferredPaid.data.PaymentDeadlineHours = 0;
transferredPaid.data.Customer.Username = 'new_invoice_owner';
const [paidTransferResult] = run({ first: () => ({ json: transferredPaid }) });
if (!paidTransferResult.json.telegramCaption.includes('@new_invoice_owner') ||
    !paidTransferResult.json.telegramCaption.includes('پرداخت مجدد لازم نیست') ||
    !paidTransferResult.json.telegramCaption.includes('مبلغ کل فاکتور:') ||
    paidTransferResult.json.telegramCaption.includes('شماره کارت جهت واریز') ||
    paidTransferResult.json.telegramCaption.includes('این فاکتور هدیه است') ||
    !paidTransferResult.json.invoiceHtml.includes('— ✅ پرداخت‌شده') ||
    !paidTransferResult.json.invoiceHtml.includes('class="total final">۰ تومان')) {
  throw new Error('Paid transfer did not preserve paid status and zero outstanding without asking the recipient to pay again.');
}
const transferredPartial = structuredClone(transferredPaid);
transferredPartial.data.IsPaid = false;
transferredPartial.data.PaidAmount = 500;
transferredPartial.data.OutstandingAmount = 700;
transferredPartial.data.PaymentDeadlineHours = 24;
const [partialTransferResult] = run({ first: () => ({ json: transferredPartial }) });
if (!partialTransferResult.json.telegramCaption.includes('مانده قابل پرداخت: ۷۰۰ تومان') ||
    !partialTransferResult.json.telegramCaption.includes('پرداخت‌شده: ۵۰۰ تومان') ||
    !partialTransferResult.json.invoiceHtml.includes('class="total final">۷۰۰ تومان') ||
    !partialTransferResult.json.telegramCaption.includes('شماره کارت جهت واریز')) {
  throw new Error('Partial transfer did not preserve the full invoice amount while requesting only the unpaid remainder.');
}
transferredPartial.data.OutstandingAmount = 800;
let rejectedTransferAmounts = false;
try { run({ first: () => ({ json: transferredPartial }) }); } catch { rejectedTransferAmounts = true; }
if (!rejectedTransferAmounts) throw new Error('Transferred invoice accepted inconsistent payment amounts.');
console.log('Invoice transfer renderer tests: PASS');
