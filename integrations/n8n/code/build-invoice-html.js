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
    timeZone: 'Asia/Tehran', year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
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
const pageClass = items.length > 10 ? 'page compact' : 'page';

const rows = items.map((item) => {
  const englishName = item.PerfumeEnglishName ?? item.PerfumeName ?? 'آیتم دستی';
  const persianName = item.PerfumePersianName ?? item.PerfumeName ?? 'آیتم دستی';
  const englishTitle = [item.PerfumeBrand, englishName].filter(Boolean).join(' ');
  return `<tr><td class="row-number">${number(item.RowNumber)}</td>
    <td class="item-name"><strong>${escapeHtml(persianName)}</strong><small dir="ltr">${escapeHtml(englishTitle)}</small></td>
    <td>${number(item.RequestedVolumeMl)} میل</td><td>${number(item.Quantity ?? 1)}</td>
    <td>${money(item.PerfumePricePerMl ?? 0)}</td><td class="line-total">${money(item.LineTotal)}</td></tr>`;
}).join('');
const accountRows = paymentAccounts.map((account) => `<div class="account">
  <div class="card" dir="ltr">${escapeHtml(formatCard(account.CardNumber))}</div>
  <div>${escapeHtml(account.AccountHolder)} - بانک ${escapeHtml(account.BankName)}</div></div>`).join('');

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

const invoiceHtml = `<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<title>فاکتور ${escapeHtml(invoice.InvoiceNumber)}</title><style>
@page{size:A4;margin:8mm}:root{--wine:#78151c;--rose:#c98f83;--cream:#fff9f3;--gold:#aa7a32;--ink:#4f2928}*{box-sizing:border-box}html{background:#f3e8dc}body{margin:0;color:var(--ink);font-family:"Noto Sans Arabic","Vazirmatn",Tahoma,sans-serif;font-size:11px;background:var(--cream)}
.page{min-height:277mm;position:relative;overflow:hidden;padding:13mm 12mm 10mm;border:1px solid #e5c9bc;background:radial-gradient(circle at 8% 8%,rgba(225,170,158,.24),transparent 18%),linear-gradient(145deg,#fffaf5 0%,#fdf2e9 58%,#fffaf5 100%)}.page:before,.page:after{content:"";position:absolute;border:1px solid rgba(170,122,50,.35);border-radius:50%;pointer-events:none}.page:before{width:120px;height:120px;left:-62px;top:-55px;box-shadow:28px 22px 0 -27px var(--rose),55px 48px 0 -52px var(--wine)}.page:after{width:150px;height:150px;right:-88px;bottom:-88px;box-shadow:-35px -30px 0 -34px var(--rose)}
header{display:grid;grid-template-columns:1fr 1.08fr;gap:24px;align-items:center;margin-bottom:11mm}.brand-panel{text-align:center;border-left:1px solid rgba(170,122,50,.25);padding-left:18px}.emblem{width:68px;height:68px;margin:0 auto 4px;border:4px solid var(--wine);border-top-color:transparent;border-radius:50%;position:relative}.emblem:before{content:"Z";position:absolute;inset:9px 0 0;color:var(--wine);font:700 34px Georgia,serif}.emblem:after{content:"";position:absolute;width:24px;height:7px;border-radius:6px;background:var(--wine);top:-7px;left:18px;box-shadow:0 -7px 0 -2px var(--wine)}.brand-en{color:var(--wine);font:500 29px Georgia,serif;letter-spacing:5px;direction:ltr}.tagline{color:var(--gold);font-size:12px;margin-top:2px}
.invoice-head h1{margin:0 0 10px;color:var(--wine);font-size:25px}.ornament{display:flex;align-items:center;gap:8px;width:145px;margin:0 0 14px auto;color:var(--gold)}.ornament:before,.ornament:after{content:"";height:1px;background:rgba(170,122,50,.55);flex:1}.ornament i{width:8px;height:8px;background:var(--gold);transform:rotate(45deg)}.meta{display:grid;grid-template-columns:1fr 1fr;gap:7px 18px}.meta div{border-bottom:1px dotted #d8b8ae;padding:3px 0;min-height:23px}.meta strong{color:var(--wine)}.meta span[dir="ltr"]{display:inline-block;white-space:nowrap;font-size:9px}
table{width:100%;border-collapse:separate;border-spacing:0;table-layout:fixed;border:1px solid #dcbdb2;border-radius:14px;overflow:hidden;background:rgba(255,255,255,.45)}thead{display:table-header-group}th{background:linear-gradient(#ead0c7,#e4c3b8);color:#5d211f;padding:9px 5px;font-size:10.5px;border-left:1px solid #d5b2a6}th:last-child,td:last-child{border-left:0}td{padding:8px 6px;text-align:center;vertical-align:middle;border-top:1px dotted #d9c0b7;border-left:1px solid #ead6cf;line-height:1.55}tbody tr{break-inside:avoid}.row-number{color:var(--wine);font-weight:700}.item-name{text-align:right}.item-name strong,.item-name small{display:block}.item-name small{color:#8f6963;font-size:9px;margin-top:2px}.line-total{font-weight:700;color:var(--wine)}
.summary{display:grid;grid-template-columns:1.45fr .9fr;gap:16px;margin-top:9mm;break-inside:avoid}.payment{border:1px solid #dec1b7;border-radius:12px;padding:12px;background:rgba(255,255,255,.42)}.payment h3{color:var(--wine);margin:0 0 8px;font-size:13px}.account{display:grid;grid-template-columns:1fr 1fr;gap:8px;border-top:1px dotted #d8b8ae;padding:7px 0}.account:first-of-type{border-top:0}.card{font-weight:700;letter-spacing:.6px;color:var(--wine)}.totals{border-radius:12px;overflow:hidden;border:1px solid #dec1b7;align-self:start}.totals div{display:flex;justify-content:space-between;padding:10px 12px;background:rgba(255,255,255,.48)}.totals .final{background:linear-gradient(90deg,#edd3ca,#e4bdb2);color:var(--wine);font-size:14px;font-weight:800;border-top:1px solid #d6ada2}.deadline{text-align:center;color:var(--wine);font-weight:700;margin:9px 0 0}
footer{margin-top:11mm;text-align:center;color:var(--wine);font-size:13px;break-inside:avoid}.footer-tagline{color:var(--gold);font-size:10px;margin-top:3px}.social{margin-top:8px;padding-top:8px;border-top:1px solid rgba(170,122,50,.3);direction:ltr;color:#865754;font-size:9px;letter-spacing:.4px}@media print{html,body{background:white}.page{break-after:page}}
.compact header{margin-bottom:5mm}.compact .emblem{width:48px;height:48px}.compact .emblem:before{inset:3px 0 0;font-size:29px}.compact .emblem:after{left:8px}.compact .brand-en{font-size:22px}.compact .invoice-head h1{font-size:20px;margin-bottom:4px}.compact .ornament{margin-bottom:5px}.compact .meta{gap:3px 14px}.compact .meta div{min-height:18px;padding:1px 0}.compact td{padding:5px}.compact .summary{margin-top:4mm}.compact .payment{padding:8px}.compact .account{padding:4px 0}.compact .totals div{padding:7px 9px}.compact footer{display:none}</style></head><body><main class="${pageClass}"><header>
<section class="brand-panel"><div class="emblem"></div><div class="brand-en">ZibaShe</div><div class="tagline">فروشگاه تخصصی عطر</div></section>
<section class="invoice-head"><h1>فاکتور خرید</h1><div class="ornament"><i></i></div><div class="meta">
<div><strong>تاریخ:</strong> ${escapeHtml(persianDate(invoice.IssuedAt))}</div><div><strong>شماره فاکتور:</strong> <span dir="ltr">${escapeHtml(invoice.InvoiceNumber)}</span></div>
<div><strong>نام کاربری:</strong> ${escapeHtml(customerName)}</div><div><strong>شماره تماس:</strong> <span dir="ltr">${escapeHtml(customer.Mobile ?? '-')}</span></div>
<div><strong>شماره سفارش:</strong> <span dir="ltr">${escapeHtml(invoice.OrderNumber)}</span></div></div></section></header>
<table><colgroup><col style="width:7%"><col style="width:30%"><col style="width:13%"><col style="width:9%"><col style="width:18%"><col style="width:23%"></colgroup>
<thead><tr><th>ردیف</th><th>نام عطر</th><th>مقدار (میل)</th><th>تعداد</th><th>قیمت واحد</th><th>قیمت کل</th></tr></thead><tbody>${rows}</tbody></table>
<section class="summary"><div class="payment"><h3>اطلاعات پرداخت</h3>${accountRows || '<div>اطلاعات حساب ثبت نشده است.</div>'}<p class="deadline">مهلت پرداخت فاکتور: ۲۴ ساعت</p></div>
<div class="totals"><div><span>جمع عطر و شیشه:</span><strong>${money(invoice.TotalAmount)}</strong></div><div class="final"><span>مبلغ قابل پرداخت:</span><span>${money(invoice.TotalAmount)}</span></div></div></section>
<footer><div>ممنون از اعتماد شما</div><div class="footer-tagline">عطر، حس خوب ماندگار</div><div class="social">zibasheperfume &nbsp; | &nbsp; Ziblog</div></footer>
</main></body></html>`;

return [{json:{...event,invoiceHtml,invoiceFileName:`invoice-${String(invoice.InvoiceNumber).replace(/[^A-Za-z0-9_-]/g,'_')}.pdf`,telegramCaption,paidCallbackData:`invoicepay:paid:${invoiceId}`,waitingCallbackData:`invoicepay:waiting:${invoiceId}`,artifactType:'InvoicePdf'}}];
