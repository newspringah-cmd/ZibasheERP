import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {parseCustomerInvoicesRead,formatExactToman} from '../src/lib/invoice-read-contract.ts';

function fixture(){return JSON.parse(readFileSync(new URL('../test-fixtures/invoice-read.synthetic.json',import.meta.url),'utf8'));}

test('C# generated synthetic response matches the TypeScript boundary without recalculating prices',()=>{
 const f=fixture();assert.equal(f.synthetic,true);
 const r=parseCustomerInvoicesRead(f.response);
 assert.equal(r.currency,'IRT');assert.equal(r.summary.paidInvoiceTotal,'380000');
 assert.equal(r.summary.pendingInvoiceTotal,'680000');assert.equal(r.summary.confirmedPartialPayments,'50000');
 assert.equal(r.summary.outstanding,'630000');
 assert.equal(r.invoices.find(i=>i.source==='inventory').lines[0].volumeMl,'1.5');
});
test('read boundary rejects numeric money, foreign currency and unsupported schema',()=>{
 for(const change of [r=>r.invoices[0].total=530000,r=>r.currency='IRR',r=>r.schemaVersion=2]){
  const r=fixture().response;change(r);assert.throws(()=>parseCustomerInvoicesRead(r));
 }
});
test('read boundary rejects duplicate IDs and contradictory pagination',()=>{
 for(const change of [r=>r.invoices[1].id=r.invoices[0].id,r=>r.hasMore=true,r=>r.pageSize=1,r=>r.summary.paidCount=50]){
  const r=fixture().response;change(r);assert.throws(()=>parseCustomerInvoicesRead(r));
 }
});
test('extra customer identities, bank and location fields are not forwarded into component props',()=>{
 const r=fixture().response;r.customerId='not-a-client-claim';r.internalLocation='private';r.bank='private';
 r.invoices[0].purchaseCost='private';r.invoices[0].lines[0].physicalLocation='private';
 const output=JSON.stringify(parseCustomerInvoicesRead(r));
 assert.doesNotMatch(output,/private|not-a-client-claim/);
});
test('decimal string formatter preserves precision beyond Number safe integer and handles half units exactly',()=>{
 assert.equal(formatExactToman('9007199254740993.25'),'۹٬۰۰۷٬۱۹۹٬۲۵۴٬۷۴۰٬۹۹۳٫۲۵ تومان');
 assert.equal(formatExactToman('0.5'),'۰٫۵ تومان');assert.equal(formatExactToman('150000'),'۱۵۰٬۰۰۰ تومان');
 for(const value of ['1e6','NaN','-1','۱۰۰','1,000','',9007199254740992])assert.throws(()=>formatExactToman(value));
});
