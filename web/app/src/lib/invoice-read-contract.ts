// Boundary for the future authenticated read API. No fetch, storage, identity or financial recalculation.
export type ExactDecimal = string;
export type ReadInvoice = Readonly<{
 id:string;number:string;issuedAt:string;status:'paid'|'pending';source:'inventory'|'sales-list';
 total:ExactDecimal;confirmedPayments:ExactDecimal;outstanding:ExactDecimal;
 lines:readonly Readonly<{name:string;volumeMl:ExactDecimal;perfumeAmount:ExactDecimal;bottleAmount:ExactDecimal;bottle:string}>[];
}>;
export type CustomerInvoicesRead = Readonly<{
 schemaVersion:1;currency:'IRT';asOf:string;
 summary:Readonly<{paidCount:number;paidInvoiceTotal:ExactDecimal;pendingCount:number;pendingInvoiceTotal:ExactDecimal;confirmedPartialPayments:ExactDecimal;outstanding:ExactDecimal}>;
 page:number;pageSize:number;totalCount:number;hasMore:boolean;invoices:readonly ReadInvoice[];
}>;

const decimalPattern=/^(0|[1-9][0-9]*)(\.[0-9]{1,28})?$/;
function invalid():never{throw new Error('Invalid customer invoice read response');}
function object(value:unknown):Record<string,unknown>{if(!value||typeof value!=='object'||Array.isArray(value))return invalid();return value as Record<string,unknown>;}
function text(value:unknown):string{if(typeof value!=='string'||!value.trim())return invalid();return value;}
function decimal(value:unknown):string{const s=text(value);if(s.length>60||!decimalPattern.test(s))return invalid();return s;}
function count(value:unknown,min=0,max=2147483647):number{if(typeof value!=='number'||!Number.isInteger(value)||value<min||value>max)return invalid();return value;}
function date(value:unknown):string{const s=text(value);if(s.length>40||!/(Z|[+-][0-9]{2}:[0-9]{2})$/.test(s)||!Number.isFinite(Date.parse(s)))return invalid();return s;}
function guid(value:unknown):string{const s=text(value);if(!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s)||s==='00000000-0000-0000-0000-000000000000')return invalid();return s;}
function array(value:unknown):unknown[]{if(!Array.isArray(value))return invalid();return value;}

export function parseCustomerInvoicesRead(value:unknown):CustomerInvoicesRead{
 const o=object(value);if(o.schemaVersion!==1||o.currency!=='IRT')return invalid();
 const s=object(o.summary);
 const summary={paidCount:count(s.paidCount),paidInvoiceTotal:decimal(s.paidInvoiceTotal),pendingCount:count(s.pendingCount),pendingInvoiceTotal:decimal(s.pendingInvoiceTotal),confirmedPartialPayments:decimal(s.confirmedPartialPayments),outstanding:decimal(s.outstanding)};
 const page=count(o.page,1),pageSize=count(o.pageSize,1,50),totalCount=count(o.totalCount);
 if(typeof o.hasMore!=='boolean'||summary.paidCount+summary.pendingCount!==totalCount)return invalid();
 const invoices:ReadInvoice[]=array(o.invoices).map(value=>{
  const i=object(value);
  if((i.status!=='paid'&&i.status!=='pending')||(i.source!=='inventory'&&i.source!=='sales-list'))return invalid();
  const lines=array(i.lines).map(value=>{const l=object(value);return {name:text(l.name),volumeMl:decimal(l.volumeMl),perfumeAmount:decimal(l.perfumeAmount),bottleAmount:decimal(l.bottleAmount),bottle:text(l.bottle)};});
  if(lines.length===0)return invalid();
  return {id:guid(i.id),number:text(i.number),issuedAt:date(i.issuedAt),status:i.status,source:i.source,total:decimal(i.total),confirmedPayments:decimal(i.confirmedPayments),outstanding:decimal(i.outstanding),lines};
 });
 const offset=(page-1)*pageSize;
 if(offset>2147483647||invoices.length>pageSize||invoices.length>totalCount||new Set(invoices.map(i=>i.id.toLowerCase())).size!==invoices.length||o.hasMore!==(offset+invoices.length<totalCount))return invalid();
 // Construct a whitelist rather than forwarding extra backend fields into client component props.
 return {schemaVersion:1,currency:'IRT',asOf:date(o.asOf),summary,page,pageSize,totalCount,hasMore:o.hasMore,invoices};
}

export function formatExactDecimal(value:ExactDecimal, decimalSeparator = '٫'):string{
 const canonical=decimal(value);const [whole,fraction]=canonical.split('.');
 const amount=new Intl.NumberFormat('fa-IR').format(BigInt(whole));
 const digits=fraction?.replace(/[0-9]/g,c=>String.fromCharCode('۰'.charCodeAt(0)+c.charCodeAt(0)-48));
 return `${amount}${digits?decimalSeparator+digits:''}`;
}
export function formatExactToman(value:ExactDecimal):string{
 return `${formatExactDecimal(value)} تومان`;
}
