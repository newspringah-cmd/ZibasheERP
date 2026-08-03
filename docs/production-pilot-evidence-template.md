# شواهد پایلوت Production زیباشه

> این فایل یک قالب است. نسخه تکمیل‌شده ممکن است حاوی شناسه‌های عملیاتی باشد و باید خارج از repository عمومی، در محل کنترل‌شده نگهداری شود. Token، API key، شماره موبایل، نشانی، مبلغ/مانده حساب، Bot Token، Connection String و headerهای احراز هویت هرگز ثبت نمی‌شوند.

## مشخصات اجرا

| مورد | مقدار |
|---|---|
| Commit SHA | `RECORD_COMMIT_SHA` |
| شروع پایلوت (UTC) | `YYYY-MM-DDTHH:mm:ssZ` |
| پایان پایلوت (UTC) | `YYYY-MM-DDTHH:mm:ssZ` |
| API | `https://erp.zibashe.ir` |
| n8n | `https://n8n.zibashe.ir` |
| اپراتور | `INTERNAL_OPERATOR_REFERENCE` |
| گروه آزمایشی ERP UUID | `REDACTED_OR_INTERNAL_REFERENCE` |

## Gate زیرساخت

| بررسی | زمان UTC | نتیجه | مرجع خروجی غیرمحرمانه |
|---|---:|---|---|
| `verify-release.sh` بدون skip | | PASS / FAIL | |
| `audit-vps.sh` | | GO / NO-GO | |
| backup دیتابیس پیش از migration | | PASS / FAIL | شناسه داخلی backup |
| restore آزمایشی دیتابیس جداگانه | | PASS / FAIL | شناسه اجرای restore |
| `health/live` | | HTTP 200 / FAIL | correlation ID |
| `health/ready` | | HTTP 200 / FAIL | correlation ID |
| `smoke-test-n8n.sh` | | PASS / FAIL | |
| `smoke-test.sh` امن | | PASS / FAIL | |

## Gate تلگرام و گروه

| بررسی | زمان UTC | نتیجه | شاهد |
|---|---:|---|---|
| ثبت و بازخوانی Webhook | | PASS / FAIL | خروجی بدون Token |
| فهرست فرمان‌های فارسی | | PASS / FAIL | تصویر Redact‌شده |
| `/start` و اتصال username | | PASS / FAIL | تصویر Redact‌شده |
| `/lists` و ساخت سفارش آزمایشی | | PASS / FAIL | OrderNumber آزمایشی |
| عضویت ربات و `my_chat_member` | | PASS / FAIL | Group UUID داخلی |
| تست Outbox تا `Processed` | | PASS / FAIL | NotificationId داخلی |
| مشاهده پیام در گروه درست | | PASS / FAIL | تصویر Redact‌شده |
| نبود پیام در گروه کنترل | | PASS / FAIL | تأیید دو نفره |

## Gate n8n و فایل‌ها

| رویداد | EventId داخلی | مقصد تطبیق داده شد | اجرای تکراری بدون duplicate | callback | مشاهده در گروه |
|---|---|---|---|---|---|
| `InvoiceIssued` / PDF | | PASS / FAIL | PASS / FAIL | HTTP status | PASS / FAIL |
| `OrderDecanted` / Photo | | PASS / FAIL | PASS / FAIL | HTTP status | PASS / FAIL |
| `OrderShipped` / Receipt | | PASS / FAIL | PASS / FAIL | HTTP status | PASS / FAIL |

## آزمون شکست کنترل‌شده

| سناریو | انتظار | نتیجه | شاهد |
|---|---|---|---|
| Webhook بدون secret | رد درخواست و بدون side effect | PASS / FAIL | HTTP status |
| رویداد n8n با timestamp قدیمی | بدون side effect | PASS / FAIL | execution reference |
| رویداد با EventId ناهماهنگ | بدون side effect | PASS / FAIL | execution reference |
| Delivery برابر `null` | عدم ارسال خصوصی/جایگزین | PASS / FAIL | execution reference |
| callback تکراری | عدم ایجاد artifact تکراری | PASS / FAIL | ArtifactId داخلی |
| شکست دائمی گروه آزمایشی | غیرفعال‌سازی گروه و هشدار ادمین | PASS / FAIL | FailureId داخلی |

## پایلوت ۲۴ ساعته

| شاخص | شروع | پایان | شرط پذیرش |
|---|---:|---:|---|
| unresolved delivery failures | | | `0` |
| failed notifications | | | `0` |
| pending/processing غیرعادی | | | `0` یا دارای توضیح |
| ارسال به مقصد اشتباه | | | `0` |
| duplicate artifact/message | | | `0` |
| رخداد بحرانی امنیت/داده | | | `0` |

## تصمیم نهایی

- [ ] تمام موارد الزامی PASS هستند.
- [ ] `GET /api/system/readiness` مقدار `readyForPilot: true` دارد.
- [ ] شواهد توسط حداقل دو نفر بررسی شده‌اند.
- [ ] هیچ داده حساس در شواهد عمومی وجود ندارد.
- [ ] Rollout بعدی فقط برای ۱۰ گروه هماهنگ‌شده تأیید شد.

**تصمیم:** `GO / NO-GO`

**زمان تصمیم (UTC):** `YYYY-MM-DDTHH:mm:ssZ`

**ارجاع داخلی تأییدکنندگان:** `RECORD_INTERNAL_APPROVAL_REFERENCES`
