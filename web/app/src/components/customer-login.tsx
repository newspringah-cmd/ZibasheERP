'use client';
import {useState} from 'react';
import {normalizeLoginMobile} from '../lib/login-mobile';
import {normalizeLoginTelegram} from '../lib/login-telegram';

export function CustomerLogin() {
 const [method,setMethod]=useState<'sms'|'telegram'>('sms');
 const [identifier,setIdentifier]=useState('');
 const [touched,setTouched]=useState(false);
 const isSms=method==='sms';
 const invalid=touched&&!(isSms?normalizeLoginMobile(identifier):normalizeLoginTelegram(identifier));
 const choose=(next:'sms'|'telegram')=>{if(next!==method){setMethod(next);setIdentifier('');setTouched(false);}};
 const field=isSms?'mobile':'telegram';
 return <><div className="safety">محیط آزمایشی مستقل · ورود واقعی هنوز فعال نیست</div><main className="login-page"><section className="login-card">
 <span className="brand-mark" aria-hidden="true">➤</span><h1>ورود به زیباشی</h1><p className="description">روش ورود خود را انتخاب کنید.</p>
 <div className="login-methods" role="group" aria-label="روش ورود"><button type="button" data-login-method="sms" aria-pressed={isSms} onClick={()=>choose('sms')}>شماره موبایل</button><button type="button" data-login-method="telegram" aria-pressed={!isSms} onClick={()=>choose('telegram')}>آیدی تلگرام</button></div>
 <form id="customer-login" onSubmit={e=>e.preventDefault()}>
  <label htmlFor={field}>{isSms?'شماره موبایل':'آیدی تلگرام'}</label><input key={field} id={field} name={field} type={isSms?'tel':'text'} inputMode={isSms?'tel':'text'} autoComplete={isSms?'tel':'username'} autoCapitalize="none" spellCheck={false} maxLength={isSms?40:65} value={identifier} onChange={e=>setIdentifier(e.target.value)} onBlur={()=>setTouched(true)} aria-invalid={invalid} aria-describedby="login-help login-error" placeholder={isSms?'۰۹۱۲۱۲۳۴۵۶۷':'@your_username'} required/>
  <p id="login-help" className="account-note">{isSms?'شماره‌ای را وارد کنید که به حساب زیباشی شما متصل و تأیید شده است.':'آیدی خود را با یا بدون @ وارد کنید؛ شناسهٔ عددی تلگرام هم پذیرفته می‌شود.'}</p>
  <p id="login-error" className="login-error" role="status">{invalid?(isSms?'شماره موبایل معتبر وارد کنید؛ مثل ۰۹۱۲۱۲۳۴۵۶۷.':'آیدی معتبر تلگرام وارد کنید؛ مثل @your_username.') : ''}</p>
  <button type="submit" className="login-submit" disabled>{isSms?'دریافت کد پیامکی · به‌زودی':'دریافت کد در تلگرام · به‌زودی'}</button>
 </form>
 <div className="callout" role="status">{isSms?'پنل پیامکی هنوز راه‌اندازی نشده است؛ فعلاً کدی ارسال نمی‌شود و اطلاعاتی ثبت نمی‌شود.':'آیدی به‌تنهایی کافی نیست؛ ورود باید با کد یا لینک یک‌بارمصرف در حساب تلگرام متصل تأیید شود. ارسال واقعی هنوز فعال نیست.'}</div>
 <p className="account-note">هر دو روش باید به همان حساب مشتری متصل باشند تا سوابق خرید حفظ شود. در زمان قطع تلگرام، روش موبایل مسیر مستقل ورود خواهد بود.</p>
 <a className="login-return" href="/">بازگشت به پیش‌نمایش زیباشی</a>
 </section></main></>;
}
