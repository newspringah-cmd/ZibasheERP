# قرارداد n8n زیباشه

این پوشه قرارداد قابل‌نسخه‌بندی بین ERP و n8n را نگه می‌دارد. n8n فقط مقصدی را مجاز می‌داند که در `data.Delivery` همان رویداد آمده باشد؛ اگر مقدار آن `null` بود، ارسال به شناسه شخصی مشتری یا مقصد جایگزین ممنوع است.

## دریافت امن رویداد

Webhook رویداد علاوه بر JSON بدنه، این هدرها را دریافت می‌کند:

- `X-Zibashe-Event-Id`: باید با `eventId` بدنه برابر باشد.
- `X-Zibashe-Timestamp`: Unix time زمان ارسال است؛ اختلاف بیش از پنج دقیقه رد می‌شود.
- `X-Zibashe-Signature`: مقدار `sha256=<hex>` از `HMAC-SHA256(secret, timestamp + "." + rawBody)` است.
- `X-Zibashe-Webhook-Token`: مقدار محرمانه‌ای است که Webhook node با Header Auth و Credential رمزگذاری‌شده n8n بررسی می‌کند.

Webhook node باید پیش از اجرای workflow با Header Auth روی `X-Zibashe-Webhook-Token` محافظت شود؛ Secret فقط داخل Credential رمزگذاری‌شده n8n قرار می‌گیرد. بلافاصله بعد از آن `code/validate-event-metadata.js`، بازه پنج‌دقیقه‌ای timestamp، تطابق EventId، قالب امضای HMAC، نوع رویداد و مقصد را کنترل می‌کند. امضای HMAC برای gateway یا اعتبارسنجی تکمیلی raw-body نیز همراه درخواست حفظ شده است. EventId قبل از هر side effect در یک Data Table یا دیتابیس با constraint یکتا ثبت می‌شود تا retry باعث ارسال دوباره نشود. Secret در workflow JSON، execution output یا log ذخیره نمی‌شود.

در Credential نوع Header Auth، نام هدر دقیقاً `X-Zibashe-Webhook-Token` و مقدار آن دقیقاً `N8n__WebhookSecret` از تنظیمات ERP است. Compose تولیدی ذخیره payload اجرای موفق، ناموفق و دستی را غیرفعال می‌کند تا هدرها و اطلاعات مشتری در execution history باقی نمانند؛ وضعیت تحویل و خطا در ERP ثبت می‌شود.

Schema ورودی در `contracts/event-envelope.schema.json` و نمونه واقعی ساختار فاکتور در `samples/invoice-issued.json` قرار دارد.

## مسیرهای workflow

### InvoiceIssued

1. اعتبارسنجی امضا، timestamp، EventId و Schema.
2. توقف امن اگر `data.Delivery` برابر `null` است.
3. ساخت HTML فاکتور با داده escape‌شده و تبدیل آن به PDF توسط Gotenberg داخلی در `http://gotenberg:3000/forms/chromium/convert/html`.
4. ارسال با Telegram «Send Document» فقط به `data.Delivery.ChatId`.
5. ثبت نتیجه در `POST /api/integrations/n8n/order-artifacts` با نوع `InvoicePdf`.

کد آماده تولید HTML در `code/build-invoice-html.js` قرار دارد. آن را در Code node با حالت Run Once for All Items قرار دهید، خروجی `invoiceHtml` را با Convert to File به binary با نام `index.html` تبدیل کنید و HTTP Request را به‌صورت multipart با فیلد `files` به Gotenberg بفرستید. نام PDF نهایی در `invoiceFileName` آماده است.

### OrderDecanted

1. اعتبارسنجی و ثبت یکتای رویداد.
2. ایجاد کار منتظر ورودی اپراتور برای عکس همان سفارش؛ عکس واقعی قابل تولید خودکار نیست.
3. کنترل نوع و اندازه فایل و نمایش شماره سفارش برای جلوگیری از انتخاب اشتباه.
4. ارسال با Telegram «Send Photo» به گروه مجاز.
5. ثبت callback با نوع `DecantPhoto`.

### OrderShipped

1. اعتبارسنجی و ثبت یکتای رویداد.
2. انتظار برای تصویر رسید پستی همان سفارش و کنترل تطابق کد رهگیری.
3. ارسال تصویر یا سند به گروه مجاز.
4. ثبت callback با نوع `PostalReceipt`.

Telegram node رسمی n8n عملیات Send Document و Send Photo را پشتیبانی می‌کند. ربات باید از قبل عضو گروه و مجاز به ارسال باشد.

## callback موفق

درخواست با `X-Api-Key` نقش N8n به مسیر زیر ارسال می‌شود:

```text
POST https://API_DOMAIN/api/integrations/n8n/order-artifacts
```

بدنه باید با `contracts/artifact-callback.schema.json` منطبق باشد. `sourceEventId` همان EventId ورودی و `orderId` همان سفارش است. ERP نوع فایل را با نوع رویداد مبدأ تطبیق می‌دهد و callback تکراری را idempotent پاسخ می‌دهد.

## callback شکست دائمی

اگر Telegram اعلام کرد ربات از گروه حذف شده، گروه وجود ندارد یا ارسال برای همیشه غیرممکن است، درخواست مطابق `contracts/delivery-failure.schema.json` به مسیر زیر فرستاده می‌شود:

```text
POST https://API_DOMAIN/api/integrations/n8n/delivery-failures
```

ERP تطابق `chatId` با مقصد رویداد را بررسی، گروه را غیرفعال و برای ادمین هشدار ایجاد می‌کند. خطای شبکه و rate limit شکست دائمی نیستند و ابتدا باید با backoff retry شوند.

## معیار پذیرش workflow

- رویداد با امضای اشتباه، timestamp قدیمی یا EventId ناهماهنگ هیچ side effect ندارد.
- اجرای دوباره یک EventId فایل یا پیام تکراری ایجاد نمی‌کند.
- مقصد فقط `data.Delivery.ChatId` است.
- نبود مقصد، ورودی اپراتور یا فایل معتبر به‌صورت قابل‌پیگیری متوقف می‌شود.
- callback موفق و callback شکست با API key اختصاصی n8n ارسال می‌شوند.
- خطای دائمی Telegram در گزارش ادمین دیده می‌شود.
- یک اجرای آزمایشی برای هر سه event در محیط تست ثبت و شواهد آن نگهداری می‌شود.
