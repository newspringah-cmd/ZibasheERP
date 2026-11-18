import {test} from 'node:test';
import assert from 'node:assert/strict';
import {assertIsolation} from './check-isolation.mjs';
import {parcels} from '../src/lib/parcel-fixture.ts';
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
test('React retains the approved prototype parcel details',()=>{
 const require=createRequire(import.meta.url);
 assert.deepEqual(parcels,require('../../preview/account.js').snapshot.parcels);
});
