import {test} from 'node:test';
import assert from 'node:assert/strict';
import {normalizeLoginTelegram} from '../src/lib/login-telegram.ts';

test('Telegram login normalizes usernames and stable numeric IDs without number precision loss',()=>{
 for(const value of [' @Customer_Name ','customer_name'])assert.deepEqual(normalizeLoginTelegram(value),{kind:'username',value:'customer_name'});
 assert.deepEqual(normalizeLoginTelegram('۷۴۱۴۷۱۹۲۸'),{kind:'numeric-id',value:'741471928'});
 assert.deepEqual(normalizeLoginTelegram('9223372036854775807'),{kind:'numeric-id',value:'9223372036854775807'});
 assert.deepEqual(normalizeLoginTelegram('00012345'),{kind:'numeric-id',value:'12345'});
});
test('Telegram login rejects links, markup, zero and overflowing numeric IDs',()=>{
 for(const value of ['','@','@@customer','@12345','https://t.me/customer','نام فارسی','a b','<script>','0','-42','9223372036854775808','a'.repeat(33)])assert.equal(normalizeLoginTelegram(value),null);
});
