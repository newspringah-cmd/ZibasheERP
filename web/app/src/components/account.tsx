import {parcels} from '../lib/parcel-fixture';
import {invoiceLabels, sourceLabel, type Parcel} from '../lib/contracts';

import {formatExactDecimal, formatExactToman as money, type CustomerInvoicesRead, type ReadInvoice} from '../lib/invoice-read-contract';

const persianDate = new Intl.DateTimeFormat('fa-IR-u-ca-persian', {
 timeZone: 'Asia/Tehran', year: 'numeric', month: '2-digit', day: '2-digit',
});
const displayDate = (value: string) => persianDate.format(new Date(value));
const isPositive = (value: string) => /[1-9]/.test(value);

export type AccountFilter = 'all' | 'pending' | 'paid' | 'parcels';
const filters: Record<AccountFilter,string> = {all:'همهٔ فاکتورها',pending:'در انتظار پرداخت',paid:'پرداخت‌شده',parcels:'بسته‌های من'};

export function CustomerAccount({invoicePage,filter,onFilter,onInvoice,onParcel}: {
 invoicePage:CustomerInvoicesRead;filter:AccountFilter;onFilter:(filter:AccountFilter)=>void;onInvoice:(i:ReadInvoice)=>void;onParcel:(p:Parcel)=>void;
}) {
 const {summary, invoices, asOf} = invoicePage;
 const visibleInvoices = invoices.filter(invoice => filter === 'all' || invoice.status === filter);
 return <section className="card account-card"><div className="card-body">
  <span className="sender-name">حساب مشتری نمونه</span><h2>فاکتورها و بسته‌های من</h2>
  <p className="description">اطلاعات زیر از ERP خوانده نشده‌اند؛ حساب و مبلغ‌ها ساختگی‌اند.</p>
  <div className="summary"><div className="stat">جمع فاکتورهای پرداخت‌شده<b>{money(summary.paidInvoiceTotal)}</b></div><div className="stat debt">بدهی باقی‌مانده<b>{money(summary.outstanding)}</b></div></div>
  <p className="account-note">پرداخت تأییدشدهٔ فاکتورهای باز: {money(summary.confirmedPartialPayments)}؛ از بدهی بالا کسر شده است.</p>
  <div className="account-filters" role="group" aria-label="فیلتر سوابق">{(Object.keys(filters) as AccountFilter[]).map(key=><button key={key} data-account-filter={key} aria-pressed={key===filter} onClick={()=>onFilter(key)}>{filters[key]}</button>)}</div>
  <div className="account-results" aria-live="polite">{filter==='parcels'?parcels.map(p=><button className="account-row" key={p.id} data-parcel={p.id} onClick={()=>onParcel(p)}>
   <span className="account-row-title"><b>{p.title}</b><span className={`status ${p.status==='shipped'?'success':'pending'}`}>{p.status==='shipped'?'ارسال‌شده':'در حال آماده‌سازی'}</span></span><span className="account-row-description">ثبت: {p.registeredOn} · {new Intl.NumberFormat('fa-IR').format(p.items.length)} آیتم</span><span className="account-open">نمایش اقلام و روند ارسال ‹</span>
  </button>):visibleInvoices.map(i=><button className="account-row" key={i.id} data-invoice={i.number} onClick={()=>onInvoice(i)}>
   <span className="account-row-title"><b dir="ltr">{i.number}</b><span className={`status ${i.status==='paid'?'success':'pending'}`}>{invoiceLabels[i.status]}</span></span>
   <span className="account-row-description">{i.lines.map(l=>l.name).join('، ')}</span><span className="account-row-title"><span>{sourceLabel(i.source)} · {displayDate(i.issuedAt)}</span><b>{money(i.total)}</b></span>
   {i.status==='pending'&&isPositive(i.confirmedPayments)&&isPositive(i.outstanding)&&<span className="partial-payment">پرداخت تأییدشده: {money(i.confirmedPayments)} · باقی‌مانده: {money(i.outstanding)}</span>}<span className="account-open">مشاهدهٔ فاکتور ‹</span>
  </button>)}
  {filter !== 'parcels' && visibleInvoices.length === 0 && <p className="account-note">فاکتوری در این بخش نیست.</p>}
  </div><p className="account-note">آخرین وضعیت: {displayDate(asOf)} · همهٔ مبلغ‌ها به تومان</p>
 </div></section>;
}

export function InvoiceDetail({invoice:i}:{invoice:ReadInvoice}) {
 return <><p className={`status ${i.status==='paid'?'success':'pending'}`}>{invoiceLabels[i.status]}</p><p>{sourceLabel(i.source)} · تاریخ {displayDate(i.issuedAt)}</p>
 {i.lines.map((l,index)=><section className="invoice-line" key={index}><h3>{l.name}</h3><p>مقدار: {formatExactDecimal(l.volumeMl, '.')} میل · شیشه: {l.bottle}</p><dl><dt>مبلغ عطر</dt><dd>{money(l.perfumeAmount)}</dd><dt>هزینهٔ شیشه</dt><dd>{money(l.bottleAmount)}</dd></dl></section>)}
 <dl className="invoice-totals"><dt>مبلغ فاکتور</dt><dd>{money(i.total)}</dd>{(i.status !== 'paid' || isPositive(i.confirmedPayments)) && <><dt>پرداخت تأییدشده</dt><dd>{money(i.confirmedPayments)}</dd></>}<dt>بدهی باقی‌مانده</dt><dd>{money(i.outstanding)}</dd></dl>
 <p className="account-note">مبلغ‌ها بر اساس نسخهٔ ثبت‌شدهٔ فاکتور نمونه‌اند.</p><button className="account-disabled" disabled>ارسال رسید پرداخت · غیرفعال در پیش‌نمایش</button></>;
}
export function ParcelDetail({parcel:p}:{parcel:Parcel}) {
 const kinds={own:'خرید خودم','gift-received':'هدیهٔ دریافتی',inventory:'موجودی آماده'};
 return <><ol className="parcel-timeline">{p.steps.map((s,index)=><li key={s} className={index<=p.currentStep?'complete':''} aria-current={index===p.currentStep?'step':undefined}>{s}{index===p.currentStep?' · وضعیت فعلی':''}</li>)}</ol><h3>اقلام همین بسته</h3>
 {p.items.map((i,index)=><div className="sample-row" key={index}><b>{i.name}</b> — {i.volumeMl} میل<small>{kinds[i.kind]}</small></div>)}<p className="account-note">{p.note}</p></>;
}
