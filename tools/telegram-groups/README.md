# استخراج فهرست گروه‌های تلگرام

این ابزار با حساب کاربری ادمینی که عضو گروه‌هاست، همه گروه‌ها و سوپرگروه‌های قابل مشاهده (از جمله Archive) را به CSV تبدیل می‌کند. ربات تلگرام امکان دریافت فهرست گروه‌های قدیمی را ندارد؛ به همین دلیل این مرحله با ورود حساب کاربری انجام می‌شود.

## آماده‌سازی اولیه

1. در `https://my.telegram.org` برای حساب ادمین یک API application بسازید و `api_id` و `api_hash` را دریافت کنید.
2. در PowerShell و داخل همین پوشه اجرا کنید:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
$env:TELEGRAM_API_ID="YOUR_API_ID"
$env:TELEGRAM_API_HASH="YOUR_API_HASH"
$env:TELEGRAM_PHONE="+98..."
```

`api_hash`، کد ورود، رمز دومرحله‌ای و فایل session را برای هیچ فردی ارسال نکنید. این موارد نباید در Git یا Google Sheet قرار گیرند.

## استخراج جدید

```powershell
python export_groups.py
```

در اجرای اول، تلگرام کد ورود و در صورت فعال بودن رمز دومرحله‌ای، رمز را درخواست می‌کند. خروجی در `output/telegram-groups-current.csv` ساخته می‌شود.

## مقایسه با Google Sheet قبلی

ابتدا Sheet قبلی را با فرمت CSV دانلود کنید. ستون شناسه گروه باید `chat_id` نام داشته باشد. سپس اجرا کنید:

```powershell
python export_groups.py --previous "C:\path\telegram-groups-old.csv"
```

ستون `status` در خروجی یکی از مقادیر زیر است:

- `new`: گروهی که در خروجی قبلی وجود نداشته است.
- `existing`: گروه قبلی بدون تغییر نام یا username.
- `changed`: شناسه یکسان است ولی نام، username یا نوع گروه تغییر کرده است.
- `missing`: فقط در فایل `telegram-groups-missing.csv`؛ در فهرست قبلی بوده ولی اکنون برای حساب قابل مشاهده نیست.

شناسه `chat_id` مبنای ادغام است؛ نام گروه به‌تنهایی شناسه مطمئنی نیست و ممکن است تکراری یا قابل تغییر باشد.

ستون `customer_username` از اولین username موجود در عنوان گروه استخراج می‌شود. این مقدار فقط یک نامزد برای تطبیق است و پیش از ورود به ERP باید در برابر username واقعی مشتری اعتبارسنجی شود؛ گروه‌های بدون username یا مشتری‌های دارای چند گروه در گزارش بررسی دستی باقی می‌مانند.
