"use strict";

const state={key:sessionStorage.getItem("zibashi-admin-key")||"",perfumes:[],batches:[],bottles:[],salesLists:[],customers:[],orders:[]};
const $=selector=>document.querySelector(selector);
const $$=selector=>[...document.querySelectorAll(selector)];
const money=value=>new Intl.NumberFormat("fa-IR").format(Number(value||0));
const text=value=>String(value??"").replace(/[&<>"']/g,char=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[char]));

async function api(path,options={}){
  const response=await fetch(path,{...options,headers:{"X-Api-Key":state.key,...(options.body?{"Content-Type":"application/json"}:{}),...(options.headers||{})},cache:"no-store"});
  if(response.status===401||response.status===403)throw new Error("کلید مدیریت معتبر نیست یا دسترسی کافی ندارد.");
  if(!response.ok){let body={};try{body=await response.json()}catch{}const detail=body.message||body.Message||body.title||Object.values(body.errors||{}).flat().join("، ");throw new Error(detail||`خطای سرور (${response.status})`)}
  return response.status===204?null:response.json();
}

function notify(message,isError=false){const box=$("#notice");box.textContent=message;box.className=`notice${isError?" error":""}`;setTimeout(()=>box.classList.add("hidden"),5000)}
function setBusy(form,busy){const button=form.querySelector("button[type=submit]");button.disabled=busy;button.dataset.label??=button.textContent;button.textContent=busy?"در حال ثبت...":button.dataset.label}

async function loadAll(){
  const [perfumes,batches,bottles,salesLists,customers,orders]=await Promise.all([
    api("/api/perfumes?includeInactive=true"),api("/api/batches"),api("/api/bottles?includeInactive=true"),api("/api/sales-lists"),api("/api/customers"),api("/api/orders")]);
  Object.assign(state,{perfumes,batches,bottles,salesLists,customers,orders});render();
}

function render(){
  const stats=[["عطر",state.perfumes.length],["بچ",state.batches.length],["شیشه",state.bottles.length],["لیست فروش",state.salesLists.length],["مشتری",state.customers.length],["سفارش",state.orders.length]];
  $("#stats").innerHTML=stats.map(([label,count])=>`<div class="stat"><strong>${money(count)}</strong><span>${label}</span></div>`).join("");
  $("#perfumeList").innerHTML=cards(state.perfumes,x=>`<h3>${text(x.name)} — ${text(x.brand)}</h3><p>${text(x.englishName)}</p><p>هر میل ${money(x.pricePerMl)} تومان · شیشه اصلی ${money(x.originalBottleVolumeMl)} میل</p>`);
  $("#batchList").innerHTML=cards(state.batches,x=>`<h3>${text(x.perfumeName)} — ${text(x.batchNumber)}</h3><p>${text(x.brand)} · موجودی ${money(x.remainingVolumeMl)} از ${money(x.totalVolumeMl)} میل</p><p>خرید ${money(x.purchasePrice)} تومان</p>`);
  $("#bottleList").innerHTML=cards(state.bottles,x=>`<h3>${text(x.name)} · ${money(x.volumeMl)} میل</h3><p>${text(x.type)} · ${money(x.salePrice)} تومان</p><span class="badge">${x.isActive?"فعال":"غیرفعال"}</span>`);
  $("#salesListList").innerHTML=cards(state.salesLists,x=>`<h3>${text(x.perfumeName)} — ${text(x.batchNumber)}</h3><p>هر میل ${money(x.pricePerMl)} تومان · باقیمانده ${money(x.remainingVolume)} میل</p><span class="badge">${text(x.status)}</span>`);
  $("#customerList").innerHTML=cards(state.customers,x=>`<h3>${text(x.fullName)}</h3><p dir="ltr">${text(x.mobile)}</p><p>${x.username?"@"+text(x.username):"بدون نام کاربری تلگرام"}</p>`);
  $("#orderList").innerHTML=cards(state.orders,x=>`<h3>${text(x.orderNumber||x.id)}</h3><p>${text(x.customerName||"")} · ${money(x.finalAmount)} تومان</p><span class="badge">${text(x.status)}</span>${String(x.status).toLowerCase()!=="invoiced"?`<p><button type="button" class="issue-invoice" data-order-id="${x.id}">صدور فاکتور</button></p>`:""}`);
  options("perfumes",state.perfumes,x=>`${x.name} — ${x.brand}`);options("batches",state.batches,x=>`${x.perfumeName} — ${x.batchNumber}`);options("bottles",state.bottles.filter(x=>x.isActive),x=>`${x.name} — ${x.volumeMl} میل`,true);options("salesLists",state.salesLists.filter(x=>String(x.status).toLowerCase()==="open"),x=>`${x.perfumeName} — ${x.remainingVolume} میل`);options("customers",state.customers,x=>`${x.fullName} — ${x.mobile}`);
}

function cards(items,template){return items.length?items.map(x=>`<article class="card">${template(x)}</article>`).join(""):`<p>هنوز موردی ثبت نشده است.</p>`}
function options(name,items,label,preserveFirst=false){$$(`[data-options=${name}]`).forEach(select=>{const first=preserveFirst?'<option value="">بدون شیشه (فقط صاحب باتل)</option>':'<option value="">انتخاب کنید</option>';const value=select.value;select.innerHTML=first+items.map(x=>`<option value="${x.id}">${text(label(x))}</option>`).join("");select.value=value})}

const formConfigs={
  perfumeForm:{url:"/api/perfumes",body:f=>({name:f.name,englishName:f.englishName,brand:f.brand,pricePerMl:+f.pricePerMl,originalBottleVolumeMl:+f.originalBottleVolumeMl,notes:f.notes||null})},
  batchForm:{url:"/api/batches",body:f=>({perfumeId:f.perfumeId,batchNumber:f.batchNumber,purchasePrice:+f.purchasePrice,totalVolumeMl:+f.totalVolumeMl,purchaseDate:new Date(f.purchaseDate+"T00:00:00").toISOString(),status:"Open"})},
  bottleForm:{url:"/api/bottles",body:f=>({name:f.name,volumeMl:+f.volumeMl,type:+f.type,salePrice:+f.salePrice,isDefault:f.isDefault==="on",notes:f.notes||null})},
  salesListForm:{url:"/api/sales-lists",body:f=>({batchId:f.batchId,pricePerMl:+f.pricePerMl,totalVolume:+f.totalVolume,telegramChannelId:f.telegramChannelId||null,notes:f.notes||null})},
  customerForm:{url:"/api/customers",body:f=>({fullName:f.fullName,mobile:f.mobile,telegramId:f.telegramId||null,username:f.username||null,notes:f.notes||null,creditLimit:+f.creditLimit,walletBalance:+f.walletBalance})},
  orderForm:{url:"/api/orders",body:f=>({customerId:f.customerId,salesListId:f.salesListId,requestedVolumeMl:+f.requestedVolumeMl,isBottleOwner:f.isBottleOwner==="on",bottleId:f.bottleId||null,notes:f.notes||null})}
};

Object.entries(formConfigs).forEach(([id,config])=>$("#"+id).addEventListener("submit",async event=>{event.preventDefault();const form=event.currentTarget;setBusy(form,true);try{const data=Object.fromEntries(new FormData(form));const result=await api(config.url,{method:"POST",body:JSON.stringify(config.body(data))});if(config.after)await config.after(result,data);form.reset();notify("اطلاعات با موفقیت ثبت شد.");await loadAll()}catch(error){notify(error.message,true)}finally{setBusy(form,false)}}));

$("#orderList").addEventListener("click",async event=>{const button=event.target.closest(".issue-invoice");if(!button)return;if(!confirm("فاکتور این سفارش صادر و اعلان‌های تلگرام و PDF ایجاد شود؟"))return;button.disabled=true;try{const invoice=await api(`/api/invoices/order/${button.dataset.orderId}`,{method:"POST"});notify(`فاکتور ${invoice.invoiceNumber||""} صادر شد.`);await loadAll()}catch(error){notify(error.message,true)}finally{button.disabled=false}});

$("#orderForm [name=isBottleOwner]").addEventListener("change",event=>{const bottle=$("#orderForm [name=bottleId]");if(event.target.checked){bottle.value="";bottle.disabled=true}else bottle.disabled=false});

$$('.tab').forEach(tab=>tab.addEventListener("click",()=>{$$('.tab,.panel').forEach(x=>x.classList.remove("active"));tab.classList.add("active");$("#"+tab.dataset.panel).classList.add("active")}));
$$('[data-action=refresh]').forEach(button=>button.addEventListener("click",()=>loadAll().then(()=>notify("اطلاعات به‌روز شد.")).catch(error=>notify(error.message,true))));

$("#loginForm").addEventListener("submit",async event=>{event.preventDefault();state.key=$("#apiKey").value.trim();$("#loginError").textContent="";try{await api("/api/customers?limit=1");sessionStorage.setItem("zibashi-admin-key",state.key);showApp();await loadAll()}catch(error){state.key="";$("#loginError").textContent=error.message}});
$("#logout").addEventListener("click",()=>{sessionStorage.removeItem("zibashi-admin-key");location.reload()});
function showApp(){$("#loginView").classList.add("hidden");$("#appView").classList.remove("hidden");$("#logout").classList.remove("hidden")}

if(state.key){showApp();loadAll().catch(error=>{sessionStorage.removeItem("zibashi-admin-key");state.key="";location.reload()})}
