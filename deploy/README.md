# استقرار API روی VPS

این پیکربندی API را به‌صورت non-root، فقط روی `127.0.0.1:8080` و پشت reverse proxy اجرا می‌کند. SQL Server و n8n در این Compose ایجاد نمی‌شوند تا پس از مشخص‌شدن منابع VPS، محل دیتابیس و معماری backup آگاهانه انتخاب شوند.

## آماده‌سازی

روی VPS لینوکسی:

```bash
cd deploy
cp .env.production.example .env.production
chmod 600 .env.production
```

همه مقادیر `REPLACE_...` و `CHANGE_ME` را با Secretهای تصادفی جایگزین کنید. فایل واقعی `.env.production` نباید وارد Git، پیام‌رسان یا backup بدون رمز شود.

## Build و اجرا

```bash
docker compose -f docker-compose.production.yml config
docker compose -f docker-compose.production.yml build --pull
docker compose -f docker-compose.production.yml up -d
docker compose -f docker-compose.production.yml ps
```

در Production، API پیش از پذیرش درخواست‌ها migrationهای EF Core را اعمال می‌کند. قبل از اولین اجرا و هر ارتقا از دیتابیس backup بگیرید.

## Reverse proxy

دامنه HTTPS باید در reverse proxy به `http://127.0.0.1:8080` متصل شود. فقط پورت‌های `22`، `80` و `443` در Firewall عمومی باز می‌شوند؛ پورت API و دیتابیس نباید مستقیماً روی اینترنت منتشر شوند.

پس از تنظیم دامنه، موارد زیر بررسی می‌شوند:

```text
GET https://API_DOMAIN/health/live
GET https://API_DOMAIN/health/ready
```

سپس webhook تلگرام با `allowed_updates` شامل `message`، `callback_query` و `my_chat_member` ثبت می‌شود.

## نگهداری

- زمان داخل سرویس‌ها و دیتابیس UTC است؛ نمایش برای مدیر با منطقه زمانی تهران انجام می‌شود.
- logها نباید شامل Bot Token، API Key، Webhook Secret یا اطلاعات ورود دیتابیس باشند.
- backup دیتابیس باید رمزگذاری، زمان‌بندی و با آزمون بازیابی دوره‌ای کنترل شود.
- پس از انتشار نسخه جدید، health check و گزارش `/api/telegram-groups/readiness` بررسی می‌شوند.
