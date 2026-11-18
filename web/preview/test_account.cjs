'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const account=require('./account.js');

test('synthetic financial snapshot reconciles partial payments and invoice line totals',()=>{
 const {snapshot:s}=account;
 assert.equal(s.synthetic,true);assert.equal(s.currency,'IRT');
 for(const i of s.invoices){
  assert.equal(i.lines.reduce((sum,l)=>sum+l.perfumeAmount+l.bottleAmount,0),i.total);
  assert.equal(i.total-i.confirmedPaid,i.outstanding);
 }
 assert.equal(s.invoices.filter(i=>i.status==='paid').reduce((n,i)=>n+i.total,0),s.summary.paidInvoiceTotal);
 assert.equal(s.invoices.filter(i=>i.status==='pending').reduce((n,i)=>n+i.outstanding,0),s.summary.outstanding);
 assert.equal(s.invoices.filter(i=>i.status==='pending').reduce((n,i)=>n+i.confirmedPaid,0),s.summary.confirmedPartialPayments);
});
test('filtering and repeated detail reads do not change invoice money or fixture',()=>{
 const before=JSON.stringify(account.snapshot);
 assert.match(account.render('pending'),/DEMO-002/);assert.match(account.render('pending'),/DEMO-003/);
 assert.doesNotMatch(account.render('pending'),/DEMO-001/);
 assert.doesNotMatch(account.render('paid'),/DEMO-002/);
 for(let n=0;n<3;n++){account.render('paid');account.invoiceDetail('sample-inventory');}
 assert.equal(JSON.stringify(account.snapshot),before);
 assert.throws(()=>{account.snapshot.invoices[0].total=1;},TypeError);
 assert.equal(account.invoiceDetail('missing'),null);assert.equal(account.parcelDetail('missing'),null);
});
test('untrusted filter cannot become markup and financial writes stay disabled',()=>{
 assert.doesNotMatch(account.render('<img src=x onerror=alert(1)>'),/onerror/);
 assert.match(account.invoiceDetail('sample-inventory').html,/disabled/);
 assert.doesNotMatch(account.invoiceDetail('sample-inventory').html,/جایگاه|مخزن|شماره کارت/);
});
test('parcel snapshot includes received gift, excludes waiting invoice, preserves half ml',()=>{
 const ready=account.snapshot.parcels.find(p=>p.id==='sample-ready');
 assert.equal(ready.items.filter(i=>i.kind==='gift-received').length,1);
 assert.equal(ready.items.filter(i=>i.kind==='gift-given').length,0);
 assert.equal(ready.items.find(i=>i.kind==='inventory').volumeMl,'۱.۵');
 assert.match(account.parcelDetail(ready.id).html,/هدیهٔ دریافتی/);
 assert.doesNotMatch(account.parcelDetail(ready.id).html,/DEMO-003/);
});
