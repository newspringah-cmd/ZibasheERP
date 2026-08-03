# استقرار API روی VPS

این پیکربندی API را به‌صورت non-root، فقط روی `127.0.0.1:8080` و پشت reverse proxy اجرا می‌کند. SQL Server و n8n در این Compose ایجاد نمی‌شوند تا پس از مشخص‌شدن منابع VPS، محل دیتابیس و معماری backup آگاهانه انتخاب شوند.

## آماده‌سازی

روی VPS لینوکسی:

```bash
cd deploy
chmod 700 initialize-production-env.sh
./initialize-production-env.sh
chmod 700 preflight.sh
chmod 700 smoke-test.sh
chmod 700 register-telegram-webhook.sh
chmod 700 verify-no-secrets.sh
./verify-no-secrets.sh
```

پیش از انتقال به سرور، تمام Gateهای محلی را با یک فرمان اجرا کنید. اگر Docker روی سیستم فعلی نصب نیست، فقط بررسی Compose را صریحاً رد کنید؛ روی CI و VPS این بخش نباید رد شود:

```bash
chmod 700 verify-release.sh
./verify-release.sh
# فقط روی سیستم توسعه بدون Docker:
./verify-release.sh --skip-containers
```

خروجی نهایی `GO` فقط آمادگی artifactهای نرم‌افزاری را تأیید می‌کند و جایگزین smoke test واقعی VPS و پایلوت تلگرام/n8n نیست.

این ابزار `.env.production` و `.env.n8n` را فقط در صورت نبودن فایل‌ها می‌سازد و Secretهای داخلی مستقل تولید می‌کند، اما مقادیر خارجی مانند اتصال SQL Server، Bot Token، AdminChatId و دامنه‌ها را تغییر نمی‌دهد. همه مقادیر باقی‌مانده `REPLACE_...` و `CHANGE_ME` را تکمیل کنید. فایل‌های واقعی env نباید وارد Git، پیام‌رسان یا backup بدون رمز شوند.

## Build و اجرا

```bash
./preflight.sh
docker compose -f docker-compose.production.yml build --pull
docker compose -f docker-compose.production.yml up -d
docker compose -f docker-compose.production.yml ps
```

## تست قابل مشاهده پس از استقرار

ابتدا تست امن را اجرا کنید؛ این تست سلامت API و دیتابیس، ردشدن API key نامعتبر و پذیرش کلید ادمین را بررسی می‌کند و چیزی به تلگرام نمی‌فرستد:

```bash
./smoke-test.sh https://API_DOMAIN
```

خروجی هر مرحله باید `PASS` باشد و گزارش آمادگی گروه‌ها نیز نمایش داده می‌شود. برای ارسال یک پیام واقعی و غیرمحرمانه به گروه آزمایشی، شناسه UUID همان گروه در ERP را از `GET /api/telegram-groups` بردارید و اجرا کنید:

```bash
./smoke-test.sh https://API_DOMAIN TELEGRAM_GROUP_UUID
```

اسکریپت پس از پاسخ `HTTP 202` حداکثر ۳۰ ثانیه وضعیت Outbox را بررسی می‌کند و فقط وقتی worker پیام را با وضعیت `Processed` ثبت کرده باشد موفق می‌شود؛ سپس پیام تأیید اتصال را نیز داخل همان گروه تلگرام مشاهده کنید. این آزمون را فقط روی گروهی اجرا کنید که ربات قبلاً عضویت و اجازه ارسال آن را تأیید کرده است.

در CI نیز پس از موفقیت Build و تست‌های .NET، Dockerfile با Buildx ساخته می‌شود ولی تا زمان تعریف Registry و سیاست انتشار VPS هیچ imageای push یا deploy نمی‌شود.

در Production، API پیش از پذیرش درخواست‌ها migrationهای EF Core را اعمال می‌کند. قبل از اولین اجرا و هر ارتقا از دیتابیس backup بگیرید.

## Reverse proxy

دامنه HTTPS باید در reverse proxy به `http://127.0.0.1:8080` متصل شود. فقط پورت‌های `22`، `80` و `443` در Firewall عمومی باز می‌شوند؛ پورت API و دیتابیس نباید مستقیماً روی اینترنت منتشر شوند.

قالب Caddy آماده است و TLS را به‌صورت خودکار مدیریت می‌کند. پس از آنکه DNS هر دو دامنه به IP سرور اشاره کرد، فایل نهایی را بدون واردکردن scheme یا مسیر بسازید:

```bash
chmod 700 render-caddyfile.sh
./render-caddyfile.sh api.your-domain.example n8n.your-domain.example ops@your-domain.example
sudo caddy validate --config "$PWD/Caddyfile" --adapter caddyfile
sudo install -o root -g root -m 600 Caddyfile /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

فایل تولیدی `deploy/Caddyfile` وارد Git نمی‌شود. Caddy فقط به پورت‌های loopback سرویس‌ها متصل است و WebSocket موردنیاز n8n را نیز از طریق `reverse_proxy` عبور می‌دهد.

پس از تنظیم دامنه، موارد زیر بررسی می‌شوند:

```text
GET https://API_DOMAIN/health/live
GET https://API_DOMAIN/health/ready
```

سپس webhook تلگرام با `allowed_updates` شامل `message`، `callback_query` و `my_chat_member` ثبت می‌شود.

ثبت و بررسی webhook بدون نمایش Bot Token یا secret:

```bash
./register-telegram-webhook.sh https://API_DOMAIN
```

چهار بررسی خروجی باید `PASS` باشند: ثبت و بررسی webhook و ثبت و بررسی منوی فارسی فرمان‌های ربات. این فرمان updateهای در انتظار قبلی را حذف نمی‌کند.

## استقرار مستقل n8n

n8n و PostgreSQL آن در Compose جدا اجرا می‌شوند و SQL Server مربوط به ERP را تغییر نمی‌دهند. imageها برای Production روی n8n `2.30.5`، PostgreSQL `16.10` و Gotenberg Chromium `8.34.0` pin شده‌اند:

```bash
chmod 700 preflight-n8n.sh backup-n8n.sh
./preflight-n8n.sh
docker compose --env-file .env.n8n -f docker-compose.n8n.production.yml pull
docker compose --env-file .env.n8n -f docker-compose.n8n.production.yml up -d
docker compose --env-file .env.n8n -f docker-compose.n8n.production.yml ps
```

بعد از اتصال HTTPS دامنه n8n، سلامت همه سرویس‌ها و تبدیل واقعی HTML فارسی به PDF را آزمایش کنید:

```bash
chmod 700 smoke-test-n8n.sh
./smoke-test-n8n.sh https://N8N_DOMAIN
```

تمام خطوط باید `PASS` باشند؛ این تست هیچ پیام تلگرامی یا فایل مشتری ایجاد نمی‌کند.

دامنه `N8N_DOMAIN` در reverse proxy با HTTPS به `http://127.0.0.1:5678` متصل می‌شود. پورت PostgreSQL عمومی نیست. Secret امضای ERP و API key نقش n8n را پس از ورود اولیه، داخل Credentialهای رمزگذاری‌شده n8n تنظیم کنید؛ آن‌ها را داخل workflow JSON قرار ندهید.

Credential ورودی Webhook از نوع Header Auth با نام `X-Zibashe-Webhook-Token` و مقدار `N8n__WebhookSecret` ساخته می‌شود. ذخیره payload اجرای workflowها در Production غیرفعال است تا هدر احراز هویت و اطلاعات مشتری در execution history باقی نماند.

Gotenberg فقط داخل شبکه Docker در `http://gotenberg:3000/forms/chromium/convert/html` در دسترس n8n است و هیچ پورت عمومی ندارد. workflow فاکتور باید فایل `index.html` با CSS و محتوای escape‌شده تولید و به‌صورت multipart به این مسیر ارسال کند؛ JavaScript در موتور PDF غیرفعال است. PDF خروجی سپس با Telegram Send Document به `data.Delivery.ChatId` فرستاده می‌شود.

پیش از تغییر نسخه یا workflowها از دیتابیس n8n در یک مسیر محافظت‌شده خارج repository نسخه پشتیبان بگیرید:

```bash
sudo install -d -m 700 /var/backups/zibashe/n8n
./backup-n8n.sh /var/backups/zibashe/n8n
```

اسکریپت یک dump دیتابیس و یک آرشیو volume فایل‌های n8n می‌سازد. برای بازیابی Credentialهای رمزگذاری‌شده، همان `N8N_ENCRYPTION_KEY` نیز باید در secret manager یا backup رمزگذاری‌شده مستقل نگهداری شود. هر دو فایل backup با هم نگهداری می‌شوند و بازیابی ابتدا روی محیط جدا آزمایش می‌شود.

## ورود موجودی گروه‌های تلگرام

ابتدا فایل تازه استخراج‌شده را فقط dry-run کنید؛ این فرمان دیتابیس را تغییر نمی‌دهد و تعداد نگاشت‌ها و اولین خطاها را نمایش می‌دهد:

```bash
chmod 700 import-telegram-groups.sh
./import-telegram-groups.sh https://API_DOMAIN /protected/path/telegram-groups-current.csv
```

پس از نگهداری خروجی و بررسی موارد مبهم/بدون مشتری، ورود واقعی با تأیید صریح انجام می‌شود:

```bash
CONFIRM_TELEGRAM_GROUP_IMPORT=YES ./import-telegram-groups.sh \
  https://API_DOMAIN /protected/path/telegram-groups-current.csv --apply
```

گروه‌های واردشده عمداً غیرفعال می‌مانند تا عضویت و اجازه ارسال ربات از طریق `my_chat_member` تأیید شود؛ بنابراین import به‌تنهایی هیچ پیام گروهی ارسال نمی‌کند.

## نگهداری

- زمان داخل سرویس‌ها و دیتابیس UTC است؛ نمایش برای مدیر با منطقه زمانی تهران انجام می‌شود.
- logها نباید شامل Bot Token، API Key، Webhook Secret یا اطلاعات ورود دیتابیس باشند.
- backup دیتابیس باید رمزگذاری، زمان‌بندی و با آزمون بازیابی دوره‌ای کنترل شود.
- پس از انتشار نسخه جدید، health check و گزارش `/api/telegram-groups/readiness` بررسی می‌شوند.
