/* Synthetic read-only view contract. No identity, network, storage or payment operations. */
(function (root) {
 'use strict';
 const snapshot = {
  schemaVersion: 1, synthetic: true, currency: 'IRT', asOf: '۱۴۰۵/۰۸/۲۷ · نمونه',
  summary: {paidInvoiceTotal: 380000, outstanding: 630000, confirmedPartialPayments: 50000},
  invoices: [
   {id:'sample-paid',number:'DEMO-001',issuedOn:'۱۴۰۵/۰۸/۲۵',status:'paid',source:'sales-list',total:380000,confirmedPaid:380000,outstanding:0,
    lines:[{name:'عطر نمونهٔ گل رز',volumeMl:'۲',perfumeAmount:360000,bottleAmount:20000,bottle:'نرمال ۲ میل'}]},
   {id:'sample-inventory',number:'DEMO-002',issuedOn:'۱۴۰۵/۰۸/۲۶',status:'pending',source:'inventory',total:150000,confirmedPaid:50000,outstanding:100000,
    lines:[{name:'بستهٔ نمونهٔ آماده',volumeMl:'۱.۵',perfumeAmount:150000,bottleAmount:0,bottle:'باتل اصلی رایگان'}]},
   {id:'sample-waiting',number:'DEMO-003',issuedOn:'۱۴۰۵/۰۸/۲۷',status:'pending',source:'sales-list',total:530000,confirmedPaid:0,outstanding:530000,
    lines:[{name:'عطر نمونهٔ وانیلی',volumeMl:'۵',perfumeAmount:500000,bottleAmount:30000,bottle:'فانتزی ۵ میل'}]}
  ],
  parcels: [
   {id:'sample-ready',title:'بستهٔ نمونهٔ آماده‌سازی',registeredOn:'۱۴۰۵/۰۸/۲۶',status:'preparing',
    steps:['درخواست ارسال ثبت شد','در حال آماده‌سازی','تحویل به پست','کد رهگیری'],currentStep:1,
    items:[{name:'عطر نمونهٔ گل رز',volumeMl:'۲',kind:'own'},{name:'عطر نمونهٔ وانیلی',volumeMl:'۱',kind:'gift-received'},{name:'بستهٔ نمونهٔ آماده',volumeMl:'۱.۵',kind:'inventory'}],
    note:'هدیهٔ دریافتی در بستهٔ گیرنده قرار می‌گیرد. اقلام در انتظار رسیدن به ایران در این بسته نیستند.'},
   {id:'sample-shipped',title:'بستهٔ نمونهٔ قبلی',registeredOn:'۱۴۰۵/۰۸/۲۰',status:'shipped',
    steps:['درخواست ارسال ثبت شد','آماده‌سازی انجام شد','تحویل به پست','کد رهگیری ثبت شد'],currentStep:3,
    items:[{name:'عطر نمونهٔ قبلی',volumeMl:'۳',kind:'own'}],
    note:'این بسته و وضعیت آن ساختگی است؛ کد رهگیری واقعی ندارد.'}
  ]
 };
 const labels={all:'همهٔ فاکتورها',pending:'در انتظار پرداخت',paid:'پرداخت‌شده',parcels:'بسته‌های من'};
 const escape = value => String(value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const money = value => `${new Intl.NumberFormat('fa-IR').format(value)} تومان`;
 const origin = value => value==='inventory'?'🔴 موجودی آماده':'لیست عطر';
 const kind = value => ({own:'خرید خودم','gift-received':'هدیهٔ دریافتی',inventory:'موجودی آماده'}[value]);
 function invoiceRow(i) {
  return `<button class="account-row" data-invoice="${escape(i.id)}"><span class="account-row-title"><b dir="ltr">${escape(i.number)}</b><span class="status ${i.status==='paid'?'success':'pending'}">${labels[i.status]}</span></span><span class="account-row-description">${escape(i.lines.map(l=>l.name).join('، '))}</span><span class="account-row-title"><span>${escape(origin(i.source))} · ${escape(i.issuedOn)}</span><b>${money(i.total)}</b></span>${i.confirmedPaid>0&&i.outstanding>0?`<span class="partial-payment">پرداخت تأییدشده: ${money(i.confirmedPaid)} · باقی‌مانده: ${money(i.outstanding)}</span>`:''}<span class="account-open">مشاهدهٔ فاکتور ‹</span></button>`;
 }
 function parcelRow(p) {
  return `<button class="account-row" data-parcel="${escape(p.id)}"><span class="account-row-title"><b>${escape(p.title)}</b><span class="status ${p.status==='shipped'?'success':'pending'}">${p.status==='shipped'?'ارسال‌شده':'در حال آماده‌سازی'}</span></span><span class="account-row-description">ثبت: ${escape(p.registeredOn)} · ${new Intl.NumberFormat('fa-IR').format(p.items.length)} آیتم</span><span class="account-open">نمایش اقلام و روند ارسال ‹</span></button>`;
 }
 function render(filter='all') {
  if(!Object.hasOwn(labels,filter))filter='all';
  const rows=filter==='parcels'?snapshot.parcels.map(parcelRow):snapshot.invoices.filter(i=>filter==='all'||i.status===filter).map(invoiceRow);
  return `<section class="card account-card"><div class="card-body"><span class="sender-name">حساب مشتری نمونه</span><h2>فاکتورها و بسته‌های من</h2><p class="description">اطلاعات زیر از ERP خوانده نشده‌اند؛ حساب و مبلغ‌ها ساختگی‌اند.</p><div class="summary"><div class="stat">جمع فاکتورهای پرداخت‌شده<b>${money(snapshot.summary.paidInvoiceTotal)}</b></div><div class="stat debt">بدهی باقی‌مانده<b>${money(snapshot.summary.outstanding)}</b></div></div><p class="account-note">پرداخت تأییدشدهٔ فاکتورهای باز: ${money(snapshot.summary.confirmedPartialPayments)}؛ از بدهی بالا کسر شده است.</p><div class="account-filters" role="group" aria-label="فیلتر سوابق">${Object.entries(labels).map(([key,label])=>`<button data-account-filter="${key}" aria-pressed="${key===filter}">${label}</button>`).join('')}</div><div class="account-results" aria-live="polite">${rows.join('')||'<p class="empty">موردی وجود ندارد.</p>'}</div><p class="account-note">آخرین وضعیت: ${snapshot.asOf} · همهٔ مبلغ‌ها به تومان</p></div></section>`;
 }
 function invoiceDetail(id) {
  const i=snapshot.invoices.find(x=>x.id===id);if(!i)return null;
  return {title:`فاکتور نمونه ${i.number}`,html:`<p class="status ${i.status==='paid'?'success':'pending'}">${labels[i.status]}</p><p>${escape(origin(i.source))} · تاریخ ${escape(i.issuedOn)}</p>${i.lines.map(l=>`<section class="invoice-line"><h3>${escape(l.name)}</h3><p>مقدار: ${escape(l.volumeMl)} میل · شیشه: ${escape(l.bottle)}</p><dl><dt>مبلغ عطر</dt><dd>${money(l.perfumeAmount)}</dd><dt>هزینهٔ شیشه</dt><dd>${money(l.bottleAmount)}</dd></dl></section>`).join('')}<dl class="invoice-totals"><dt>مبلغ فاکتور</dt><dd>${money(i.total)}</dd><dt>پرداخت تأییدشده</dt><dd>${money(i.confirmedPaid)}</dd><dt>بدهی باقی‌مانده</dt><dd>${money(i.outstanding)}</dd></dl><p class="account-note">مبلغ‌ها از نسخهٔ نمونهٔ فاکتور نمایش داده می‌شوند؛ با قیمت امروز محاسبه نشده‌اند.</p><button class="account-disabled" disabled>ارسال رسید پرداخت · غیرفعال در پیش‌نمایش</button>`};
 }
 function parcelDetail(id) {
  const p=snapshot.parcels.find(x=>x.id===id);if(!p)return null;
  return {title:p.title,html:`<ol class="parcel-timeline">${p.steps.map((s,index)=>`<li class="${index<=p.currentStep?'complete':''}" ${index===p.currentStep?'aria-current="step"':''}>${escape(s)}${index===p.currentStep?' · وضعیت فعلی':''}</li>`).join('')}</ol><h3>اقلام همین بسته</h3>${p.items.map(i=>`<div class="sample-row"><b>${escape(i.name)}</b> — ${escape(i.volumeMl)} میل<small>${escape(kind(i.kind))}</small></div>`).join('')}<p class="account-note">${escape(p.note)}</p>`};
 }
 // Freeze fixture recursively: filters and details never change source amounts or ownership.
 function freeze(value){Object.values(value).forEach(v=>{if(v&&typeof v==='object')freeze(v);});return Object.freeze(value);}
 freeze(snapshot);
 const api={snapshot,render,invoiceDetail,parcelDetail};
 if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.ZibasheAccount=api;
})(typeof globalThis!=='undefined'?globalThis:this);
