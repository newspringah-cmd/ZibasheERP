// Paste this file into an n8n Code node configured to run once for all items.
const event = $input.first().json;

function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function money(value) {
  const number = Number(value);
  if (!Number.isFinite(number)) {
    throw new Error('Invoice contains an invalid monetary value.');
  }
  return `${new Intl.NumberFormat('fa-IR', { maximumFractionDigits: 0 }).format(number)} تومان`;
}

if (event.eventType !== 'InvoiceIssued' || !event.data) {
  throw new Error('Expected an InvoiceIssued event.');
}
if (!event.data.Delivery?.ChatId) {
  throw new Error('Invoice event has no approved Telegram group delivery target.');
}

const invoice = event.data;
const customer = invoice.Customer ?? {};
const items = Array.isArray(invoice.Items) ? invoice.Items : [];
const paymentAccounts = Array.isArray(invoice.PaymentAccounts) ? invoice.PaymentAccounts.slice(0, 4) : [];
const username = String(customer.Username ?? '').trim().replace(/^@/, '');
const invoiceTitle = username ? `فاکتور عطر — @${username}` : `فاکتور عطر — ${customer.FullName ?? ''}`;
if (items.length === 0) {
  throw new Error('Invoice has no items.');
}

const rows = items.map((item) => `
  <tr>
    <td>${escapeHtml(item.RowNumber)}</td>
    <td>${escapeHtml(item.PerfumeBrand)} ${escapeHtml(item.PerfumeName)}</td>
    <td>${escapeHtml(item.RequestedVolumeMl)} میلی‌لیتر</td>
    <td>${money(item.PerfumeAmount)}</td>
    <td>${item.IsBottleOwner ? escapeHtml(item.BottleName || 'شیشه') : '—'}</td>
    <td>${money(item.LineTotal)}</td>
  </tr>`).join('');

const accountRows = paymentAccounts.map((account) => `
  <div class="account">
    <strong>${escapeHtml(account.CardNumber)}</strong>
    <span>${escapeHtml(account.AccountHolder)} — بانک ${escapeHtml(account.BankName)}</span>
  </div>`).join('');

const invoiceHtml = `<!doctype html>
<html lang="fa" dir="rtl">
<head>
  <meta charset="utf-8">
  <title>فاکتور ${escapeHtml(invoice.InvoiceNumber)}</title>
  <style>
    @page { size: A4; margin: 18mm; }
    * { box-sizing: border-box; }
    body { margin: 0; color: #202124; font-family: "Noto Sans Arabic", Tahoma, sans-serif; font-size: 12px; }
    header { display: flex; justify-content: space-between; align-items: flex-start; border-bottom: 2px solid #8b5e3c; padding-bottom: 14px; margin-bottom: 18px; }
    h1 { margin: 0 0 6px; color: #8b5e3c; font-size: 24px; }
    .brand { font-size: 18px; font-weight: 700; }
    .meta { display: grid; grid-template-columns: 1fr 1fr; gap: 8px 24px; background: #faf6f1; padding: 12px; border-radius: 8px; margin-bottom: 18px; }
    table { width: 100%; border-collapse: collapse; }
    th { background: #8b5e3c; color: white; font-weight: 600; }
    th, td { border: 1px solid #ddd; padding: 8px 6px; text-align: right; vertical-align: top; }
    tbody tr:nth-child(even) { background: #fafafa; }
    .totals { width: 48%; margin: 18px 0 0 auto; }
    .totals div { display: flex; justify-content: space-between; padding: 6px 0; border-bottom: 1px solid #ddd; }
    .totals .final { color: #8b5e3c; font-size: 15px; font-weight: 700; border-bottom: 0; }
    .payment { margin-top: 22px; padding: 14px; background: #faf6f1; border-radius: 8px; }
    .account { display: flex; justify-content: space-between; padding: 7px 0; border-bottom: 1px solid #ddd; }
    footer { margin-top: 30px; padding-top: 12px; border-top: 1px solid #ddd; color: #666; text-align: center; }
  </style>
</head>
<body>
  <header>
    <div><div class="brand">زیباشی</div><div>${escapeHtml(invoiceTitle)}</div></div>
    <div><h1>${escapeHtml(invoice.InvoiceNumber)}</h1><div>سفارش: ${escapeHtml(invoice.OrderNumber)}</div></div>
  </header>
  <section class="meta">
    <div><strong>مشتری:</strong> ${escapeHtml(customer.FullName)}</div>
    <div><strong>شماره موبایل:</strong> ${escapeHtml(customer.Mobile)}</div>
    <div><strong>تاریخ صدور:</strong> ${escapeHtml(new Date(invoice.IssuedAt).toLocaleString('fa-IR', { timeZone: 'Asia/Tehran' }))}</div>
    <div><strong>شماره سفارش:</strong> ${escapeHtml(invoice.OrderNumber)}</div>
  </section>
  <table>
    <thead><tr><th>ردیف</th><th>عطر</th><th>حجم</th><th>مبلغ عطر</th><th>شیشه</th><th>جمع ردیف</th></tr></thead>
    <tbody>${rows}</tbody>
  </table>
  <section class="totals">
    <div><span>جمع عطر</span><span>${money(invoice.PerfumeTotal)}</span></div>
    <div><span>جمع شیشه</span><span>${money(invoice.BottleTotal)}</span></div>
    <div class="final"><span>مبلغ نهایی</span><span>${money(invoice.TotalAmount)}</span></div>
  </section>
  ${accountRows ? `<section class="payment"><h3>شماره کارت جهت واریز</h3>${accountRows}<p><strong>مهلت پرداخت فاکتور: ۲۴ ساعت</strong></p></section>` : ''}
  <footer>این فاکتور به‌صورت خودکار توسط سامانه زیباشی صادر شده است.</footer>
</body>
</html>`;

return [{
  json: {
    ...event,
    invoiceHtml,
    invoiceFileName: `invoice-${String(invoice.InvoiceNumber).replace(/[^A-Za-z0-9_-]/g, '_')}.pdf`,
    artifactType: 'InvoicePdf'
  }
}];
