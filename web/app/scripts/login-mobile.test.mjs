import {test} from 'node:test';
import assert from 'node:assert/strict';
import {normalizeLoginMobile} from '../src/lib/login-mobile.ts';

test('SMS form accepts Persian, Arabic and international-prefix Iranian numbers',()=>{
 for(const value of ['۰۹۱۲۰۰۰۰۰۰۰','٠٩١٢٠٠٠٠٠٠٠','+98 912-000-0000','00989120000000','09120000000'])
  assert.equal(normalizeLoginMobile(value),'09120000000');
});
test('SMS form rejects usernames, landlines and markup instead of treating them as login identifiers',()=>{
 for(const value of ['','@customer','02112345678','+449120000000','09120000000<script>','0912+0000000'])
  assert.equal(normalizeLoginMobile(value),null);
});
