import {account} from '../lib/account-fixture';
import {invoiceLabels, money, sourceLabel, type Invoice, type Parcel} from '../lib/contracts';

export type AccountFilter = 'all' | 'pending' | 'paid' | 'parcels';
const filters: Record<AccountFilter,string> = {all:'همهٔ فاکتورها',pending:'در انتظار پرداخت',paid:'پرداخت‌شده',parcels:'بسته‌های من'};

export function CustomerAccount({filter,onFilter,onInvoice,onParcel}: {
 filter:AccountFilter;onFilter:(filter:AccountFilter)=>void;onInvoice:(i:Invoice)=>void;onParcel:(p:Parcel)=>void;
}) {
 return <section className="card account-card"><div className="card-body">
  <span className="sender-name">حساب مشتری نمونه</span><h2>فاکتورها و بسته‌های من</h2>
  <p className="description">اطلاعات زیر از ERP خوانده نشده‌اند؛ حساب و مبلغ‌ها ساختگی‌اند.</p>
  <div className="summary"><div className="stat">جمع فاکتورهای پرداخت‌شده<b>{money(account.summary.paidInvoiceTotal)}</b></div><div className="stat debt">بدهی باقی‌مانده<b>{money(account.summary.outstanding)}</b></div></div>
  <p className="account-note">پرداخت تأییدشدهٔ فاکتورهای باز: {money(account.summary.confirmedPartialPayments)}؛ از بدهی بالا کسر شده است.</p>
  <div className="account-filters" role="group" aria-label="فیلتر سوابق">{(Object.keys(filters) as AccountFilter[]).map(key=><button key={key} data-account-filter={key} aria-pressed={key===filter} onClick={()=>onFilter(key)}>{filters[key]}</button>)}</div>
  <div className="account-results" aria-live="polite">{filter==='parcels'?account.parcels.map(p=><button className="account-row" key={p.id} data-parcel={p.id} onClick={()=>onParcel(p)}>
   <span className="account-row-title"><b>{p.title}</b><span className={`status ${p.status==='shipped'?'success':'pending'}`}>{p.status==='shipped'?'ارسال‌شده':'در حال آماده‌سازی'}</span></span><span className="account-row-description">ثبت: {p.registeredOn} · {new Intl.NumberFormat('fa-IR').format(p.items.length)} آیتم</span><span className="account-open">نمایش اقلام و روند ارسال ‹</span>
  </button>):account.invoices.filter(i=>filter==='all'||i.status===filter).map(i=><button className="account-row" key={i.id} data-invoice={i.id} onClick={()=>onInvoice(i)}>
   <span className="account-row-title"><b dir="ltr">{i.number}</b><span className={`status ${i.status==='paid'?'success':'pending'}`}>{invoiceLabels[i.status]}</span></span>
   <span className="account-row-description">{i.lines.map(l=>l.name).join('، ')}</span><span className="account-row-title"><span>{sourceLabel(i.source)} · {i.issuedOn}</span><b>{money(i.total)}</b></span>
   {i.confirmedPaid>0&&i.outstanding>0&&<span className="partial-payment">پرداخت تأییدشده: {money(i.confirmedPaid)} · باقی‌مانده: {money(i.outstanding)}</span>}<span className="account-open">مشاهدهٔ فاکتور ‹</span>
  </button>)}</div><p className="account-note">آخرین وضعیت: {account.asOf} · همهٔ مبلغ‌ها به تومان</p>
 </div></section>;
}

export function InvoiceDetail({invoice:i}:{invoice:Invoice}) {
 return <><p className={`status ${i.status==='paid'?'success':'pending'}`}>{invoiceLabels[i.status]}</p><p>{sourceLabel(i.source)} · تاریخ {i.issuedOn}</p>
 {i.lines.map((l,index)=><section className="invoice-line" key={index}><h3>{l.name}</h3><p>مقدار: {l.volumeMl} میل · شیشه: {l.bottle}</p><dl><dt>مبلغ عطر</dt><dd>{money(l.perfumeAmount)}</dd><dt>هزینهٔ شیشه</dt><dd>{money(l.bottleAmount)}</dd></dl></section>)}
 <dl className="invoice-totals"><dt>مبلغ فاکتور</dt><dd>{money(i.total)}</dd><dt>پرداخت تأییدشده</dt><dd>{money(i.confirmedPaid)}</dd><dt>بدهی باقی‌مانده</dt><dd>{money(i.outstanding)}</dd></dl>
 <p className="account-note">مبلغ‌ها از نسخهٔ نمونهٔ فاکتور نمایش داده می‌شوند؛ با قیمت امروز محاسبه نشده‌اند.</p><button className="account-disabled" disabled>ارسال رسید پرداخت · غیرفعال در پیش‌نمایش</button></>;
}
export function ParcelDetail({parcel:p}:{parcel:Parcel}) {
 const kinds={own:'خرید خودم','gift-received':'هدیهٔ دریافتی',inventory:'موجودی آماده'};
 return <><ol className="parcel-timeline">{p.steps.map((s,index)=><li key={s} className={index<=p.currentStep?'complete':''} aria-current={index===p.currentStep?'step':undefined}>{s}{index===p.currentStep?' · وضعیت فعلی':''}</li>)}</ol><h3>اقلام همین بسته</h3>
 {p.items.map((i,index)=><div className="sample-row" key={index}><b>{i.name}</b> — {i.volumeMl} میل<small>{kinds[i.kind]}</small></div>)}<p className="account-note">{p.note}</p></>;
}
