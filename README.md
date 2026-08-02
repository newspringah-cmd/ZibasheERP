# ZibasheERP

بک‌اند عملیاتی اکوسیستم زیباشه برای مدیریت مشتری، فروش دکانت عطر، سفارش، اعتبار، پرداخت، فاکتور، موجودی، ارسال و اعلان‌ها.

رابط اصلی مشتری در نسخه فعلی ربات تلگرام است. تمام عملیات روی مدل مشترک `Customer` انجام می‌شوند تا وب‌سایت و اپلیکیشن آینده بتوانند با همان حساب، موبایل، TelegramId و username کار کنند.

## معماری

پروژه بر اساس لایه‌های زیر سازمان‌دهی شده است:

- `ZibasheERP.Domain`: موجودیت‌ها و وضعیت‌های اصلی کسب‌وکار
- `ZibasheERP.Application`: use caseها، اعتبارسنجی، قرارداد repositoryها و MediatR
- `ZibasheERP.Infrastructure`: SQL Server، Entity Framework Core، repositoryها و migrationها
- `ZibasheERP.API`: API، احراز هویت، webhook و worker تلگرام، health check و مدیریت خطا
- `ZibasheERP.Application.Tests`: تست‌های مستقل گردش‌کارهای کسب‌وکار

## قابلیت‌های فعلی

- کاتالوگ عطر، بچ خرید، شیشه و لیست فروش
- ثبت سفارش دکانت و کنترل ظرفیت/اعتبار
- هویت مشترک مشتری با موبایل، username و TelegramId یکتا
- کیف پول، سقف اعتبار و بدهی مشتری
- صدور فاکتور و ثبت، تأیید، رد و بازپرداخت پرداخت
- انتخاب آدرس، آدرس پیش‌فرض و حذف امن آدرس
- دکانت، آماده‌سازی، ارسال، تحویل و رهگیری مرسوله
- اعلان قابل‌بازیابی با Outbox، claim چندسروره، lease و retry نمایی
- webhook امن تلگرام با secret، rate limit و deduplication پایدار
- optimistic concurrency برای Customer، SalesList، Batch، Order و Payment
- گزارش کسب‌وکار، موجودی و مدیریت اعلان‌های ناموفق

## پیش‌نیازها

- .NET SDK 10
- SQL Server یا SQL Server Express
- در صورت فعال‌کردن تلگرام: Bot Token و یک دامنه HTTPS عمومی

## راه‌اندازی محلی

از ریشه repository اجرا کنید:

```powershell
dotnet restore ZibasheERP.slnx
dotnet build ZibasheERP.slnx --no-restore
dotnet run --project ZibasheERP.API\ZibasheERP.API.csproj
```

آدرس‌های پیش‌فرض اجرای محلی:

- `https://localhost:7189`
- `http://localhost:5143`
- Swagger فقط در محیط Development: `https://localhost:7189/swagger`

در Development داده اولیه توسط `SeedData` ساخته می‌شود. در محیط‌های دیگر migrationها هنگام شروع برنامه اجرا می‌شوند.

## تنظیمات

مقادیر محرمانه را داخل Git یا `appsettings.json` واقعی ذخیره نکنید. برای توسعه می‌توان از Secret Manager استفاده کرد:

```powershell
dotnet user-secrets init --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost\SQLEXPRESS;Database=ZibasheERPDb;Trusted_Connection=True;TrustServerCertificate=True;" --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "ApiKeys:Admin" "A_RANDOM_SECRET_WITH_AT_LEAST_32_CHARACTERS" --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "ApiKeys:TelegramBot" "ANOTHER_RANDOM_SECRET_WITH_AT_LEAST_32_CHARACTERS" --project ZibasheERP.API\ZibasheERP.API.csproj
```

برای فعال‌کردن تلگرام:

```powershell
dotnet user-secrets set "Telegram:Enabled" "true" --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "Telegram:BotToken" "BOT_TOKEN_FROM_BOTFATHER" --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "Telegram:WebhookSecret" "A_LONG_RANDOM_WEBHOOK_SECRET" --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "Telegram:AdminChatId" "ADMIN_PRIVATE_OR_GROUP_CHAT_ID" --project ZibasheERP.API\ZibasheERP.API.csproj
```

برای فعال‌کردن خروجی امن n8n:

```powershell
dotnet user-secrets set "N8n:Enabled" "true" --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "N8n:WebhookUrl" "https://N8N_DOMAIN/webhook/zibashe-events" --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "N8n:WebhookSecret" "A_RANDOM_SECRET_WITH_AT_LEAST_32_CHARACTERS" --project ZibasheERP.API\ZibasheERP.API.csproj
dotnet user-secrets set "ApiKeys:N8n" "A_DIFFERENT_RANDOM_KEY_WITH_AT_LEAST_32_CHARACTERS" --project ZibasheERP.API\ZibasheERP.API.csproj
```

هر event ارسالی به n8n این هدرها را دارد:

- `X-Zibashe-Event-Id`: شناسه یکتای event برای idempotency در workflow
- `X-Zibashe-Timestamp`: Unix timestamp زمان ارسال
- `X-Zibashe-Signature`: امضای `sha256=<hex>`

امضا برابر `HMAC-SHA256(secret, timestamp + "." + rawBody)` است. workflow باید قبل از هر پردازش، timestamp، امضا و تکراری‌نبودن EventId را بررسی کند. بدنه شامل `eventId`، `eventType`، `occurredAt`، `customerId`، `orderId` و `data` است.

برای eventهای عملیاتی مانند `InvoiceIssued`، `OrderDecanted` و `OrderShipped`، بخش `data.Delivery` مقصد مجاز ارسال را مشخص می‌کند:

```json
{
  "Channel": "TelegramGroup",
  "ChatId": "-1001234567890",
  "Title": "Customer group",
  "Username": null
}
```

اگر گروه فعال و متصل وجود نداشته باشد، `data.Delivery` برابر `null` است و workflow نباید فایل را به TelegramId شخصی یا مقصد دیگری ارسال کند. مانده‌حساب، آدرس‌ها و عملیات تعاملی مشتری همچنان فقط در گفت‌وگوی خصوصی ربات انجام می‌شوند.

پس از تولید و ارسال فایل، workflow نتیجه را با هدر `X-Api-Key` مربوط به نقش N8n به `POST /api/integrations/n8n/order-artifacts` برمی‌گرداند. نوع فایل یکی از `InvoicePdf`، `DecantPhoto` یا `PostalReceipt` است و `SourceEventId` باید همان EventId دریافتی باشد.

اگر ارسال فایل به گروه شکست قطعی خورد، workflow باید `SourceEventId`، `ChatId` و متن خطا را به `POST /api/integrations/n8n/delivery-failures` بفرستد. ERP مقصد را با event اصلی تطبیق می‌دهد، گزارش را با SourceEventId یکتا ثبت می‌کند، گروه را غیرفعال می‌کند و هشدار ادمین را در Outbox قرار می‌دهد. callback تکراری پاسخ idempotent می‌گیرد و هشدار تکراری ایجاد نمی‌کند.

تنظیمات worker تلگرام:

- `Telegram:PollIntervalSeconds`: فاصله بررسی Outbox، بین ۱ تا ۳۰۰ ثانیه
- `Telegram:BatchSize`: تعداد اعلان هر batch، بین ۱ تا ۱۰۰
- `Telegram:MaxAttempts`: سقف تلاش ارسال، بین ۱ تا ۲۰

در Production دو API key باید متفاوت و حداقل ۳۲ کاراکتر باشند.

## ثبت webhook تلگرام

پس از استقرار HTTPS، webhook را با secret یکسان تنظیم کنید:

```text
POST https://api.telegram.org/bot<BOT_TOKEN>/setWebhook
```

پارامترهای درخواست:

```json
{
  "url": "https://YOUR_DOMAIN/api/telegram/webhook",
  "secret_token": "THE_SAME_TELEGRAM_WEBHOOK_SECRET",
  "allowed_updates": ["message", "callback_query", "my_chat_member"]
}
```

تلگرام secret را در هدر `X-Telegram-Bot-Api-Secret-Token` ارسال می‌کند. webhook بدون secret معتبر پردازش نمی‌شود. `my_chat_member` برای فعال یا غیرفعال‌کردن امن مقصد گروه‌ها الزامی است.

## فرمان‌های مشتری در تلگرام

- `/start`: اتصال حساب و نمایش منوی اصلی
- `/help`: راهنما و منوی اصلی
- `/lists`: لیست‌های فروش فعال
- `/orders`: سفارش‌های مشتری
- `/balance`: کیف پول، اعتبار و بدهی
- `/addresses`: مشاهده و مدیریت آدرس‌ها
- `/addaddress`: ثبت آدرس جدید با قالب راهنما
- `/pay`: ثبت شناسه تراکنش پرداخت
- `/track`: رهگیری با شماره سفارش
- `/cancel`: لغو پیش‌نویس سفارش

اطلاعات شخصی فقط در گفت‌وگوی private نمایش داده می‌شوند. اتصال حساب ابتدا با username و در صورت نیاز با Contact متعلق به خود کاربر انجام می‌شود.

## احراز هویت API

endpointهای مدیریتی با هدر زیر محافظت می‌شوند:

```http
X-Api-Key: ADMIN_API_KEY
```

دو نقش API وجود دارد:

- `Admin`: عملیات مدیریتی و گزارش‌ها
- `TelegramBot`: عملیات محدود موردنیاز کانال تلگرام

## دیتابیس و migration

بررسی وضعیت مدل:

```powershell
dotnet ef migrations has-pending-model-changes --project ZibasheERP.Infrastructure\ZibasheERP.Infrastructure.csproj --startup-project ZibasheERP.API\ZibasheERP.API.csproj
```

اعمال migration به‌صورت دستی:

```powershell
dotnet ef database update --project ZibasheERP.Infrastructure\ZibasheERP.Infrastructure.csproj --startup-project ZibasheERP.API\ZibasheERP.API.csproj
```

قبل از اجرای migrationهای یکتایی روی دیتابیس قدیمی، از دیتابیس backup بگیرید. وجود هویت تکراری باعث توقف migration می‌شود تا ادغام مشتری‌ها آگاهانه انجام شود.

## اتصال گروه‌های مشتریان تلگرام

فهرست گروه‌ها با ابزار `tools/telegram-groups` و حساب ادمین استخراج می‌شود. شناسه گروه در `CustomerTelegramGroup` نگهداری می‌شود و با TelegramId شخصی مشتری متفاوت است.

ورود CSV همیشه ابتدا در حالت پیش‌نمایش انجام می‌شود:

```http
POST /api/telegram-groups/import-csv?dryRun=true
X-Api-Key: ADMIN_API_KEY
Content-Type: multipart/form-data
```

فایل باید ستون‌های `chat_id`، `customer_username` و یکی از `title` یا `group_name` را داشته باشد. ستون‌های `username` و `group_type` اختیاری‌اند. در صورت وجود چند گروه برای یک مشتری، فقط وقتی دقیقاً یک Supergroup وجود داشته باشد همان گروه انتخاب می‌شود؛ موارد مبهم، username خالی، مشتری پیدا‌نشده و اتصال تکراری فقط در گزارش خطا می‌آیند.

پس از بررسی کامل نتیجه Dry Run، همان درخواست با `dryRun=false` اطلاعات معتبر را به‌صورت idempotent ایجاد یا به‌روزرسانی می‌کند. گروه جدید ابتدا غیرفعال است و فقط پس از دریافت رویداد `my_chat_member` و تأیید عضویت و امکان ارسال ربات فعال می‌شود. حذف یا مسدودشدن ربات نیز گروه را دوباره غیرفعال می‌کند. فایل حداکثر ۱۰ مگابایت و ۱۰ هزار ردیف می‌تواند داشته باشد و endpoint فقط برای نقش `Admin` قابل استفاده است.

گزارش `GET /api/telegram-groups/readiness` تعداد مشتریان نگاشت‌شده، گروه‌های فعال و غیرفعال، گروه‌هایی که هنوز توسط ربات دیده نشده‌اند و درصد آمادگی ارسال را نشان می‌دهد. پیش از فعال‌کردن workflowهای ارسال خودکار n8n روی سرور تولید، این گزارش باید بررسی شود.

برای یک گروه فعال، `POST /api/telegram-groups/{id}/test-delivery` یک پیام آزمایشی بدون اطلاعات مشتری را در Outbox قرار می‌دهد. نتیجه ارسال از همان مسیر retry و گزارش اعلان‌های ناموفق عبور می‌کند؛ بنابراین برای تست واقعی دسترسی ربات، ارسال مستقیم و خارج از Outbox انجام نمی‌شود.

خطاهای موقت شبکه با backoff مجدداً تلاش می‌شوند. خطاهای قطعی دسترسی گروه، مانند حذف ربات، پیدا نشدن chat یا نداشتن اجازه ارسال، اعلان را فوراً Failed و نگاشت گروه را غیرفعال می‌کنند تا مقصد نامعتبر به n8n ارائه نشود.

اگر `Telegram:AdminChatId` تنظیم شده باشد، همان خطای قطعی یک هشدار عملیاتی شامل شناسه مشتری، گروه، اعلان و علت خطا را از طریق Outbox برای ادمین می‌فرستد. نبود این تنظیم در log هشدار ثبت می‌شود و هیچ اطلاعات محرمانه‌ای در تنظیمات مخزن قرار نمی‌گیرد.

## تست

```powershell
dotnet build ZibasheERP.slnx
dotnet run --project ZibasheERP.Application.Tests\ZibasheERP.Application.Tests.csproj --no-build
```

runner تست با exit code غیرصفر شکست را اعلام می‌کند و برای CI قابل استفاده است.

## Health check

- `GET /health/live`: زنده‌بودن process
- `GET /health/ready`: آمادگی سرویس و اتصال دیتابیس

در load balancer یا orchestrator، readiness را به `/health/ready` و liveness را به `/health/live` متصل کنید.

## چک‌لیست استقرار

1. Connection string تولید را از secret store تنظیم کنید.
2. برای Admin و TelegramBot دو کلید تصادفی و متفاوت بسازید.
3. Bot Token و Webhook Secret را فقط در secret store قرار دهید.
4. از دیتابیس backup بگیرید و migrationها را بررسی کنید.
5. API را با `ASPNETCORE_ENVIRONMENT=Production` اجرا کنید.
6. نتیجه `/health/ready` را کنترل کنید.
7. webhook تلگرام را روی دامنه HTTPS ثبت کنید.
8. جریان `/start`، مشاهده لیست، ثبت سفارش و اعلان را end-to-end آزمایش کنید.

## مسیر توسعه بعدی

وب‌سایت و اپلیکیشن باید به همین API و مدل `Customer` متصل شوند. موبایل شناسه اصلی ورود در کانال‌های جدید خواهد بود و پس از احراز، همان Customer دارای TelegramId/username بازیابی می‌شود؛ بنابراین حساب مالی، سفارش‌ها، آدرس‌ها و سوابق بین همه کانال‌ها مشترک باقی می‌مانند.
