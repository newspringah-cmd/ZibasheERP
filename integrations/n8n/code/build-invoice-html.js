// Paste this file into an n8n Code node configured to run once for all items.
const event = $input.first().json;

function escapeHtml(value) {
  return String(value ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
}
function number(value) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) throw new Error('Invoice contains an invalid numeric value.');
  return new Intl.NumberFormat('fa-IR', { maximumFractionDigits: 0 }).format(parsed);
}
function money(value) { return `${number(value)} تومان`; }
function persianDate(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) throw new Error('Invoice contains an invalid issue date.');
  return new Intl.DateTimeFormat('fa-IR-u-ca-persian', {
    timeZone: 'Asia/Tehran', year: 'numeric', month: '2-digit', day: '2-digit'
  }).format(date);
}
function formatCard(value) {
  const digits = String(value ?? '').replace(/\D/g, '');
  return digits.length === 16 ? digits.match(/.{1,4}/g).join(' - ') : String(value ?? '');
}

if (event.eventType !== 'InvoiceIssued' || !event.data) throw new Error('Expected an InvoiceIssued event.');
if (!event.data.Delivery?.ChatId) throw new Error('Invoice event has no approved Telegram group delivery target.');
const invoice = event.data;
const customer = invoice.Customer ?? {};
const items = Array.isArray(invoice.Items) ? invoice.Items : [];
const paymentAccounts = Array.isArray(invoice.PaymentAccounts) ? invoice.PaymentAccounts.slice(0, 4) : [];
const username = String(customer.Username ?? '').trim().replace(/^@/, '');
const customerName = username ? `@${username}` : (customer.FullName ?? 'مشتری زیباشی');
if (items.length === 0) throw new Error('Invoice has no items.');

const renderRows = (pageItems) => pageItems.map((item) => {
  const englishName = item.PerfumeEnglishName ?? item.PerfumeName ?? 'آیتم دستی';
  const persianName = item.PerfumePersianName ?? item.PerfumeName ?? 'آیتم دستی';
  const englishTitle = [item.PerfumeBrand, englishName].filter(Boolean).join(' ');
  return `<div class="invoice-row"><span>${number(item.RowNumber)}</span>
    <span class="item-name"><strong>${escapeHtml(persianName)}</strong><small dir="ltr">${escapeHtml(englishTitle)}</small></span>
    <span>${number(item.RequestedVolumeMl)}</span><span>${number(item.Quantity ?? 1)}</span>
    <span>${number(item.PerfumePricePerMl ?? 0)}</span><span>${number(item.BottlePrice ?? 0)}</span>
    <span>${number(item.LineTotal)}</span></div>`;
}).join('');

const captionLines = [
  `🧾 فاکتور عطر ${customerName}`,
  `تاریخ: ${persianDate(invoice.IssuedAt)}`,
  `شماره فاکتور: ${invoice.InvoiceNumber}`,
  ''
];
items.forEach((item) => {
  const name = item.PerfumePersianName ?? item.PerfumeEnglishName ?? item.PerfumeName ?? 'آیتم دستی';
  captionLines.push(`🧴 ${name}`);
  captionLines.push(`مقدار: ${number(item.RequestedVolumeMl)} میل`);
  captionLines.push(`مبلغ عطر و شیشه: ${money(item.LineTotal)}`);
  captionLines.push('');
});
captionLines.push(`💰 مبلغ قابل پرداخت: ${money(invoice.TotalAmount)}`);
if (paymentAccounts.length > 0) {
  captionLines.push('', 'شماره کارت جهت واریز:');
  paymentAccounts.forEach((account) => {
    captionLines.push(formatCard(account.CardNumber));
    captionLines.push(`${account.AccountHolder} - بانک ${account.BankName}`);
  });
}
captionLines.push('', 'با تشکر از خرید شما', 'مهلت پرداخت فاکتور: ۲۴ ساعت');
const telegramCaption = captionLines.join('\n').slice(0, 1024);
const invoiceId = String(invoice.InvoiceId ?? '').replaceAll('-', '');
const firstPaymentAccount = paymentAccounts[0] ?? {};
const secondPaymentAccount = paymentAccounts[1] ?? {};
const backgroundImage = '__INVOICE_BACKGROUND_DATA_URI__';
const pages = Array.from(
  { length: Math.ceil(items.length / 8) },
  (_, index) => items.slice(index * 8, index * 8 + 8));
const pagesHtml = pages.map((pageItems, pageIndex) => {
  const isLastPage = pageIndex === pages.length - 1;
  return `<main class="page"><img class="background" src="${backgroundImage}" alt="">
    <div class="field date">${escapeHtml(persianDate(invoice.IssuedAt))}</div>
    <div class="field customer">${escapeHtml(customerName)}</div>
    <div class="field invoice-number">${escapeHtml(invoice.InvoiceNumber)}</div>
    <div class="field mobile">${escapeHtml(customer.Mobile ?? '-')}</div>
    <div class="table-head"><span>ردیف</span><span>نام عطر</span><span>مقدار (میل)</span><span>تعداد</span><span>قیمت واحد</span><span>قیمت شیشه</span><span>قیمت کل</span></div>
    <section class="rows">${renderRows(pageItems)}</section>
    ${isLastPage ? `<div class="total subtotal">${money(invoice.TotalAmount)}</div>
    <div class="shipping-mask"></div>
    <div class="total final">${money(invoice.TotalAmount)}</div>` : '<div class="shipping-mask"></div>'}
    ${pages.length > 1 ? `<div class="page-counter">صفحه ${number(pageIndex + 1)} از ${number(pages.length)}</div>` : ''}
  </main>`;
}).join('');

const invoiceHtml = `<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<title>فاکتور ${escapeHtml(invoice.InvoiceNumber)}</title><style>
@page{size:A4;margin:0}*{box-sizing:border-box}html,body{margin:0;width:210mm;background:#faefea}body{font-family:"Noto Sans Arabic","Vazirmatn",Tahoma,sans-serif;color:#55151b}.page{position:relative;width:210mm;height:297mm;overflow:hidden;break-after:page;page-break-after:always}.page:last-child{break-after:auto;page-break-after:auto}.background{position:absolute;inset:0;width:100%;height:100%;object-fit:fill;z-index:0}.field{position:absolute;z-index:2;text-align:right;font-weight:700;font-size:10px;line-height:1.2;color:#59131a;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.date{top:19.8%;right:24.8%;width:22%}.customer{top:23.5%;right:24.8%;width:22%}.invoice-number{top:27.1%;right:24.8%;width:22%;direction:ltr;text-align:right}.mobile{top:30.7%;right:24.8%;width:22%;direction:ltr;text-align:right}.table-head{position:absolute;z-index:3;top:36%;right:8%;width:84%;height:3.8%;display:grid;grid-template-columns:10% 22% 14% 11% 15% 14% 14%;direction:rtl;align-items:center;text-align:center;background:rgba(239,203,208,.97);border-radius:12px 12px 0 0;color:#531719;font-weight:700;font-size:8.5px}.table-head span{height:100%;display:flex;align-items:center;justify-content:center;border-left:1px solid rgba(125,66,70,.12)}.table-head span:last-child{border-left:0}.rows{position:absolute;z-index:2;top:39.7%;right:8%;width:84%;height:29.3%;display:flex;flex-direction:column;justify-content:space-around;overflow:hidden}.invoice-row{display:grid;grid-template-columns:10% 22% 14% 11% 15% 14% 14%;direction:rtl;align-items:center;text-align:center;min-height:0;flex:1;font-size:8.3px;line-height:1.15;color:#4e2324}.invoice-row .item-name{padding:0 3px}.invoice-row .item-name strong,.invoice-row .item-name small{display:block}.invoice-row .item-name small{font-size:.82em;color:#7f5555}.total{position:absolute;z-index:3;right:8%;width:14%;height:2.8%;display:flex;align-items:center;justify-content:center;font-weight:800;color:#68141b;font-size:9px}.subtotal{top:71.1%}.final{top:79.9%;font-size:10px}.shipping-mask{position:absolute;z-index:2;top:74.3%;right:7.5%;width:36%;height:4.2%;background:#faece8}.page-counter{position:absolute;z-index:2;bottom:1.3%;left:4%;font-size:7px;color:#8c5b5d}@media print{html,body{print-color-adjust:exact;-webkit-print-color-adjust:exact}}</style></head><body>${pagesHtml}</body></html>`;

return [{json:{...event,invoiceHtml,invoiceFileName:`invoice-${String(invoice.InvoiceNumber).replace(/[^A-Za-z0-9_-]/g,'_')}.pdf`,telegramCaption,paidCallbackData:`invoicepay:paid:${invoiceId}`,waitingCallbackData:`invoicepay:waiting:${invoiceId}`,firstCardCopyText:String(firstPaymentAccount.CardNumber??''),firstCardCopyLabel:`📋 کپی کارت ${firstPaymentAccount.BankName??'اول'}`,secondCardCopyText:String(secondPaymentAccount.CardNumber??''),secondCardCopyLabel:`📋 کپی کارت ${secondPaymentAccount.BankName??'دوم'}`,artifactType:'InvoicePdf'}}];
