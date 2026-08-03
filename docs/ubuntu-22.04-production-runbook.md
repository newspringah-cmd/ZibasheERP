# Runbook آماده‌سازی Production روی Ubuntu 22.04

این Runbook برای VPS فرانسه با ۸ GiB RAM و دامنه‌های `api.zibashe.ir` و `n8n.zibashe.ir` است. فرمان‌های این سند باید داخل یک نشست SSH پایدار و مرحله‌به‌مرحله اجرا شوند؛ هیچ Secretی در history شل، Git یا پیام‌رسان قرار نمی‌گیرد.

## وضعیت پیش از شروع

- `n8n.zibashe.ir` در بررسی ۲۰۲۶-۰۸-۰۳ دارای رکورد A و پشت Cloudflare بود.
- `api.zibashe.ir` در همان بررسی رکورد A نداشت؛ پیش از صدور TLS باید DNS آن ساخته شود.
- IP عمومی، کاربر SSH، CPU و فضای دیسک هنوز در repository ثبت نشده‌اند.
- محل SQL Server تا بررسی دیسک و سیاست backup با وضعیت `pending` باقی می‌ماند.

## ۱. ممیزی اولیه بدون تغییر

پس از Clone مخزن روی سرور:

```bash
cd ZibasheERP/deploy
chmod 700 audit-vps.sh
./audit-vps.sh api.zibashe.ir n8n.zibashe.ir
```

در سرور خام ممکن است نبود Docker، Caddy یا Caddyfile باعث `NO-GO` شود. خروجی را نگه دارید؛ این فرمان چیزی را تغییر نمی‌دهد.

## ۲. به‌روزرسانی پایه

پیش از upgrade از snapshot یا backup پنل VPS مطمئن شوید:

```bash
sudo apt update
sudo apt upgrade
sudo apt install -y ca-certificates curl git jq python3 ufw
sudo systemctl is-system-running
```

اگر upgrade نیازمند reboot بود، ابتدا از امکان ورود مجدد با SSH مطمئن شوید و سپس در زمان کنترل‌شده reboot کنید.

## ۳. نصب Docker از repository رسمی

از convenience script استفاده نمی‌شود. مطابق repository رسمی Docker:

```bash
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
  -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc

sudo tee /etc/apt/sources.list.d/docker.sources >/dev/null <<'EOF'
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: jammy
Components: stable
Architectures: amd64
Signed-By: /etc/apt/keyrings/docker.asc
EOF

sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io \
  docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
sudo docker version
sudo docker compose version
```

معماری VPS باید پیش از استفاده از `amd64` با `dpkg --print-architecture` کنترل شود. دسترسی بدون sudo به Docker معادل دسترسی سطح root است؛ افزودن کاربر عمومی به گروه `docker` تصمیم پیش‌فرض این Runbook نیست.

## ۴. نصب Caddy از repository رسمی

```bash
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | \
  sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | \
  sudo tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
sudo chmod o+r /usr/share/keyrings/caddy-stable-archive-keyring.gpg
sudo chmod o+r /etc/apt/sources.list.d/caddy-stable.list
sudo apt update
sudo apt install -y caddy
sudo systemctl enable --now caddy
```

پس از تکمیل DNS هر دو دامنه:

```bash
cd /PROTECTED_PATH/ZibasheERP/deploy
./render-caddyfile.sh api.zibashe.ir n8n.zibashe.ir YOUR_TLS_EMAIL
sudo caddy validate --config "$PWD/Caddyfile" --adapter caddyfile
sudo install -o root -g root -m 600 Caddyfile /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

## ۵. Firewall با حفظ دسترسی SSH

قبل از فعال‌سازی UFW، یک نشست SSH دوم باز کنید و ورود آن را آزمایش کنید. اگر SSH روی پورتی غیر از ۲۲ است، همان پورت واقعی جایگزین شود:

```bash
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw limit 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw --dry-run enable
sudo ufw enable
sudo ufw status verbose
```

Docker ممکن است برای پورت‌های منتشرشده قواعدی خارج از انتظار UFW بسازد؛ به همین دلیل Compose این پروژه `8080` و `5678` را صریحاً فقط روی `127.0.0.1` bind می‌کند و PostgreSQL n8n هیچ پورت host ندارد. پس از بالا آمدن سرویس‌ها `audit-vps.sh` باید این invariant را دوباره بررسی کند.

## ۶. Secretها و فایل‌های محیطی

```bash
cd /PROTECTED_PATH/ZibasheERP/deploy
chmod 700 initialize-production-env.sh preflight.sh preflight-n8n.sh
./initialize-production-env.sh
chmod 600 .env.production .env.n8n
```

مقادیر `REPLACE_...` فقط داخل همان نشست امن سرور تکمیل می‌شوند. Bot Token، API keyها، رمز دیتابیس و کلید رمزگذاری n8n نباید در command line، Git یا اسکرین‌شات دیده شوند.

## ۷. ترتیب استقرار و Gateها

1. تعیین محل SQL Server و آزمون backup/restore.
2. اجرای `./preflight.sh` و ساخت API.
3. اجرای `./preflight-n8n.sh` و بالا آوردن n8n/PostgreSQL/Gotenberg.
4. اجرای `./smoke-test-n8n.sh https://n8n.zibashe.ir`.
5. اجرای `./smoke-test.sh https://api.zibashe.ir` بدون Group UUID.
6. ثبت Webhook با `./register-telegram-webhook.sh https://api.zibashe.ir`.
7. Import گروه‌ها ابتدا به‌صورت dry-run.
8. فعال‌سازی یک گروه داخلی و اجرای smoke test واقعی با UUID همان گروه.
9. ساخت و آزمایش سه workflow n8n و سپس پایلوت ۲۴ ساعته.

در پایان:

```bash
./audit-vps.sh api.zibashe.ir n8n.zibashe.ir
curl --fail --silent --show-error https://api.zibashe.ir/health/live
curl --fail --silent --show-error https://api.zibashe.ir/health/ready
```

خروجی `GO` شرط لازم است، اما مشاهده پیام واقعی گروه، PDF فاکتور، عکس دکانت و رسید پستی همچنان بخش اجباری پذیرش End-to-End است.
