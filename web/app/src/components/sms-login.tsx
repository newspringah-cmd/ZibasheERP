'use client';
import {useState} from 'react';
import {normalizeLoginMobile} from '../lib/login-mobile';

export function SmsLogin() {
 const [mobile,setMobile]=useState('');
 const [touched,setTouched]=useState(false);
 const invalid=touched&&!normalizeLoginMobile(mobile);
 return <><div className="safety">محیط آزمایشی مستقل · ارسال پیامک هنوز فعال نیست</div><main className="login-page"><section className="login-card">
 <span className="brand-mark" aria-hidden="true">➤</span><h1>ورود به زیباشی</h1><p className="description">با شماره موبایل و کد یک‌بارمصرف پیامکی وارد شوید.</p>
 <form id="sms-login" onSubmit={e=>e.preventDefault()}>
  <label htmlFor="mobile">شماره موبایل</label><input id="mobile" name="mobile" type="tel" inputMode="tel" autoComplete="tel" maxLength={40} value={mobile} onChange={e=>setMobile(e.target.value)} onBlur={()=>setTouched(true)} aria-invalid={invalid} aria-describedby="mobile-help mobile-error" placeholder="۰۹۱۲۱۲۳۴۵۶۷" required/>
  <p id="mobile-help" className="account-note">شماره‌ای را وارد کنید که به حساب زیباشی شما متصل و تأیید شده است.</p>
  <p id="mobile-error" className="login-error" role="status">{invalid?'شماره موبایل معتبر وارد کنید؛ مثل ۰۹۱۲۱۲۳۴۵۶۷.':''}</p>
  <button type="submit" className="login-submit" disabled>دریافت کد پیامکی · به‌زودی</button>
 </form>
 <div className="callout" role="status">پنل پیامکی هنوز راه‌اندازی نشده است؛ فعلاً کدی ارسال نمی‌شود و اطلاعاتی ثبت نمی‌شود.</div>
 <p className="account-note">اگر قبلاً از تلگرام خرید داشته‌اید، اتصال اولیهٔ شماره موبایل به همان حساب باید تأیید شود تا سوابق خریدتان حفظ شود.</p>
 <a className="login-return" href="/">بازگشت به پیش‌نمایش زیباشی</a>
 </section></main></>;
}
