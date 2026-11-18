import {test} from 'node:test';
import assert from 'node:assert/strict';
import {assertIsolation} from './check-isolation.mjs';
import {account} from '../src/lib/account-fixture.ts';
import {createRequire} from 'node:module';

test('Next production compilation is allowed but live ERP environment is refused',()=>{
 assert.doesNotThrow(()=>assertIsolation({NODE_ENV:'production'}));
 for(const key of ['APP_ENV','ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT'])
  assert.throws(()=>assertIsolation({[key]:'Production'}));
});
test('integration values and local environment files fail closed without disclosing values',()=>{
 for(const key of ['DATABASE_URL','ConnectionStrings__ERP','NEXT_PUBLIC_API_URL','ZIBASHE_WEB_WRITES'])
  assert.throws(()=>assertIsolation({[key]:'not-to-be-logged'}),e=>!e.message.includes('not-to-be-logged'));
 for(const name of ['.env','.env.local','.env.production'])assert.throws(()=>assertIsolation({},[name]));
 assert.doesNotThrow(()=>assertIsolation({},['.env.example']));
});
test('typed synthetic finance reconciles partial payment and preserves inventory volume',()=>{
 assert.equal(account.synthetic,true);
 for(const i of account.invoices){
  assert.equal(i.lines.reduce((s,l)=>s+l.perfumeAmount+l.bottleAmount,0),i.total);
  assert.equal(i.total-i.confirmedPaid,i.outstanding);
 }
 assert.equal(account.invoices.filter(i=>i.status==='paid').reduce((s,i)=>s+i.total,0),account.summary.paidInvoiceTotal);
 assert.equal(account.invoices.reduce((s,i)=>s+i.outstanding,0),account.summary.outstanding);
 assert.equal(account.parcels[0].items.find(i=>i.kind==='inventory').volumeMl,'۱.۵');
});
test('React migration retains the approved prototype account snapshot exactly',()=>{
 const require=createRequire(import.meta.url);
 assert.deepEqual(account,require('../../preview/account.js').snapshot);
});
