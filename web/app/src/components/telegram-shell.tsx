'use client';

import {useEffect,useRef,useState,type ReactNode} from 'react';
import {catalog,rooms,type Room,type CatalogItem} from '../lib/catalog-fixture';
import {CustomerAccount,InvoiceDetail,ParcelDetail,type AccountFilter} from './account';
import type {CustomerInvoicesRead} from '../lib/invoice-read-contract';

type Detail={title:string;body:ReactNode};

function ProductCard({item,onDetail}:{item:CatalogItem;onDetail:()=>void}) {
 return <article className="card message-post"><div className={`scene ${item.style}`}><span className="scene-code">{item.code}</span><div className="bottle" aria-hidden="true"><span>ZIBASHE<br/>DEMO</span></div><span className="scene-label">تصویر نمونه</span></div>
 <div className="card-body"><span className="sender-name">زیباشی</span><h2>🧴 {item.name}</h2><div className="english">{item.english}</div><p className="description">{item.description}</p><div className="caption-lines"><div>حجم: <b>{item.volume}</b></div><div>{item.minimum}</div><div>باقی‌مانده: <b>{item.remaining}</b></div></div><div className="price-row"><span>{item.room==='inventory'?'مبلغ بسته':'قیمت هر میل'}:</span><strong>{item.price} <small>تومان</small></strong></div><div className="message-meta"><span>نمونه</span><time>۱۰:۲۴</time><span aria-hidden="true">◉</span></div></div>
 <div className="actions"><button data-detail={item.id} onClick={onDetail}>مشاهدهٔ جزئیات</button><button disabled>ثبت سفارش · غیرفعال</button></div></article>;
}

function SampleMessages() {
 return <><div className="welcome incoming"><span className="sender-name">زیبا | زیباشی</span><p>سلام عزیزم 🌸<br/>اینجا می‌توانید پیام‌ها و پیگیری سفارش‌ها را کنار هم ببینید.</p><div className="message-meta">پیام نمونه · ۱۰:۲۰</div></div>
 <div className="outgoing"><div className="reply-sample">زیبا | زیباشی<br/><span>پیگیری سفارش‌ها</span></div><p>سلام، وضعیت بسته‌ام را از کجا ببینم؟</p><div className="message-meta">پیام نمونه · ۱۰:۲۱ <span>✓✓</span></div></div>
 <div className="welcome incoming"><span className="sender-name">زیبا | زیباشی</span><p>در بخش «حساب من»، وضعیت سفارش و فاکتورها نمایش داده می‌شود.</p><div className="message-meta">پیام نمونه · ۱۰:۲۲</div></div><div className="preview-notice">این گفتگو ساختگی است؛ ارسال پیام و رسید در پیش‌نمایش غیرفعال است.</div></>;
}

export function TelegramShell({invoicePage}: {invoicePage: CustomerInvoicesRead}) {
 const [activeRoom,setRoom]=useState<Room>('sales');
 const [query,setQuery]=useState('');
 const [showChatList,setShowChatList]=useState(false);
 const [filter,setFilter]=useState<AccountFilter>('all');
 const [detail,setDetail]=useState<Detail|null>(null);
 const dialog=useRef<HTMLDialogElement>(null);
 const conversation=useRef<HTMLDivElement>(null);
 useEffect(()=>{
  document.body.classList.toggle('show-chat-list',showChatList);
  return ()=>document.body.classList.remove('show-chat-list');
 },[showChatList]);
 useEffect(()=>{if(detail&&!dialog.current?.open)dialog.current?.showModal();},[detail]);
 const selectRoom=(room:Room)=>{setRoom(room);setShowChatList(false);setFilter('all');conversation.current?.scrollTo(0,0);};
 const closeDetail=()=>{dialog.current?.close();setDetail(null);};
 const items=catalog.filter(i=>i.room===activeRoom&&`${i.name} ${i.english} ${i.code}`.toLowerCase().includes(query.trim().toLowerCase()));
 const room=rooms[activeRoom];
 return <><div className="safety" role="status">محیط آزمایشی مستقل · اطلاعات ساختگی · خرید و عملیات مالی غیرفعال</div><div className="app">
 <aside className="sidebar"><div className="brand"><span className="brand-mark">➤</span><div><strong>زیباشی</strong><small>گفتگوهای زیباشی</small></div><span className="demo-tag">پیش‌نمایش</span></div>
 <label className="search-box"><span>⌕</span><input id="search" type="search" placeholder="جست‌وجو در عطرهای نمونه" aria-label="جست‌وجوی عطرهای نمونه" value={query} onChange={e=>setQuery(e.target.value)}/></label>
 <p className="nav-label">همهٔ گفتگوها</p><nav id="rooms" aria-label="انتخاب فضای نمایشی">{(Object.keys(rooms) as Room[]).map(key=><button key={key} className={`room ${activeRoom===key?'active':''}`} data-room={key} aria-pressed={activeRoom===key} onClick={()=>selectRoom(key)}><span className={`room-icon ${rooms[key].color}`}>{rooms[key].icon}</span><span><b>{rooms[key].title}</b><small>{rooms[key].subtitle}</small></span>{rooms[key].count&&<i>{rooms[key].count}</i>}</button>)}</nav>
 <div className="sidebar-note"><span>◉</span><div><b>پیش‌نمایش مستقل</b><p>طرح رابط برای ادامهٔ کار روی وب؛ اتصال به ERP در مراحل بعد بررسی می‌شود.</p><a className="login-entry" href="/login/">ورود با موبایل یا تلگرام</a></div></div></aside>
 <main><header className="room-header"><button id="open-rooms" className="header-back" aria-label="بازگشت به فهرست گفتگوها" aria-expanded={showChatList} onClick={()=>setShowChatList(true)}>←</button><div className="heading-icon">{room.icon}</div><div className="header-text"><h1 id="room-title">{room.title}</h1><p id="room-subtitle">{room.subtitle}</p></div><span className="preview-badge">آزمایشی</span><button id="preview-info" className="header-more" aria-label="اطلاعات پیش‌نمایش" onClick={()=>setDetail({title:'پیش‌نمایش زیباشی',body:<p>این نمونه با چیدمان آشنای پیام‌رسان ساخته شده است. داده‌ها ساختگی‌اند و به حساب یا سفارش واقعی متصل نیست.</p>})}>⋮</button></header>
 <div className="pinned-message"><span className="pin-icon">⌖</span><div><b>پیام سنجاق‌شده</b><p>اطلاعات نمونه‌اند؛ خرید و عملیات مالی در این محیط غیرفعال است.</p></div></div>
 <div className="conversation" id="conversation" ref={conversation}><div className="day-chip">امروز · گفتگوهای نمونه</div><div id="room-content" aria-live="polite">
 {(activeRoom==='sales'||activeRoom==='inventory')&&<><div className="welcome"><b>به فضای آزمایشی زیباشی خوش آمدی 🌿</b><br/>این چیدمان اولیهٔ سایت است؛ کارت‌ها و اعداد نمونه‌اند و سفارش واقعی ثبت نمی‌شود.</div>{items.length?items.map(item=><ProductCard key={item.id} item={item} onDetail={()=>setDetail({title:item.name,body:<p>{item.description}</p>})}/>):<div className="empty">در داده‌های نمونه نتیجه‌ای پیدا نشد.</div>}</>}
 {activeRoom==='account'&&<CustomerAccount invoicePage={invoicePage} filter={filter} onFilter={setFilter} onInvoice={invoice=>setDetail({title:`فاکتور نمونه ${invoice.number}`,body:<InvoiceDetail invoice={invoice}/>})} onParcel={parcel=>setDetail({title:parcel.title,body:<ParcelDetail parcel={parcel}/>})}/>}
 {activeRoom==='messages'&&<SampleMessages/>}
 {activeRoom==='staff'&&<section className="card"><div className="card-body"><span className="eyebrow">طرح چیدمان کارکنان</span><h2>کارهای روز، در یک نگاه</h2><p className="description">نمونهٔ صف‌های کاری؛ تعدادها واقعی نیستند و عملیاتی اجرا نمی‌شود.</p><div className="sample-row">درخواست‌های موجودی<small>نمونه · ۳ درخواست برای بررسی</small></div><div className="sample-row">رسیدهای پرداخت<small>نمونه · ۲ رسید برای بررسی</small></div><div className="sample-row">آماده‌سازی بسته<small>نمونه · ۴ بسته</small></div><div className="callout">این نما احراز هویت یا مجوز واقعی مدیر و حسابدار ندارد؛ همهٔ دکمه‌های عملیاتی غیرفعال‌اند.</div></div></section>}
 </div></div><footer className="composer"><span aria-hidden="true">☺</span><input disabled placeholder="پیام بنویسید · پیش‌نمایش غیرفعال" aria-label="ورودی غیرفعال پیام"/><button disabled aria-label="ارسال غیرفعال">➤</button></footer></main></div>
 <dialog id="details" ref={dialog} aria-labelledby="detail-title" onClose={()=>setDetail(null)}><div className="dialog-body"><button id="close-details" className="close" aria-label="بستن جزئیات" onClick={closeDetail}>×</button><span className="eyebrow">اطلاعات نمونه</span><h2 id="detail-title">{detail?.title}</h2><div id="detail-extra">{detail?.body}</div><div className="callout">این بخش فقط برای بررسی ظاهر است. قیمت قطعی، ورود مشتری و ثبت سفارش هنوز به هستهٔ ERP متصل نشده‌اند.</div></div></dialog>
 </>;
}
