#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$script_dir/.env.production"
api_base="${1:-}"
body_file="$(mktemp)"
config_file="$(mktemp)"
response_file="$(mktemp)"
trap 'rm -f "$body_file" "$config_file" "$response_file"' EXIT

fail() {
  printf 'Telegram webhook registration failed: %s\n' "$1" >&2
  exit 1
}

[[ -f "$env_file" ]] || fail 'deploy/.env.production does not exist.'
[[ "$api_base" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || \
  fail 'Pass the public API origin, for example https://api.example.com.'
command -v curl >/dev/null 2>&1 || fail 'curl is required.'
command -v python3 >/dev/null 2>&1 || fail 'python3 is required.'

value_of() {
  sed -n "s/^$1=//p" "$env_file" | tail -n 1
}

bot_token="$(value_of 'Telegram__BotToken')"
webhook_secret="$(value_of 'Telegram__WebhookSecret')"
[[ -n "$bot_token" && "$bot_token" != *REPLACE* ]] || fail 'Telegram__BotToken is not configured.'
[[ "$webhook_secret" =~ ^[A-Za-z0-9_-]{32,256}$ ]] || \
  fail 'Telegram__WebhookSecret must be 32-256 characters using only letters, numbers, underscore, or hyphen.'

webhook_url="${api_base%/}/api/telegram/webhook"
chmod 600 "$body_file" "$config_file" "$response_file"
printf '{"url":"%s","secret_token":"%s","allowed_updates":["message","callback_query","my_chat_member"],"max_connections":40,"drop_pending_updates":false}\n' \
  "$webhook_url" "$webhook_secret" > "$body_file"

printf 'url = "https://api.telegram.org/bot%s/setWebhook"\n' "$bot_token" > "$config_file"
printf 'request = "POST"\n' >> "$config_file"
printf 'header = "Content-Type: application/json"\n' >> "$config_file"
printf 'data-binary = "@%s"\n' "$body_file" >> "$config_file"
curl --silent --show-error --config "$config_file" --output "$response_file"

python3 - "$response_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    response = json.load(source)
if response.get('ok') is not True:
    raise SystemExit('Telegram rejected setWebhook: ' + str(response.get('description', 'unknown error')))
print('PASS  Telegram accepted the webhook registration.')
PY

printf 'url = "https://api.telegram.org/bot%s/getWebhookInfo"\n' "$bot_token" > "$config_file"
printf 'request = "GET"\n' >> "$config_file"
: > "$response_file"
curl --silent --show-error --config "$config_file" --output "$response_file"

python3 - "$response_file" "$webhook_url" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    response = json.load(source)
if response.get('ok') is not True:
    raise SystemExit('Telegram rejected getWebhookInfo: ' + str(response.get('description', 'unknown error')))
info = response.get('result') or {}
if info.get('url') != sys.argv[2]:
    raise SystemExit('Webhook verification returned a different URL.')
required = {'message', 'callback_query', 'my_chat_member'}
configured = set(info.get('allowed_updates') or [])
if not required.issubset(configured):
    raise SystemExit('Webhook verification is missing required update types.')
print('PASS  Webhook URL and allowed updates were verified.')
print('INFO  Pending Telegram updates:', info.get('pending_update_count', 0))
if info.get('last_error_message'):
    print('WARN  Telegram last webhook error:', info['last_error_message'])
PY

cat > "$body_file" <<'JSON'
{"commands":[{"command":"start","description":"شروع و اتصال حساب"},{"command":"help","description":"راهنمای استفاده از ربات"},{"command":"lists","description":"لیست‌های فروش فعال"},{"command":"orders","description":"سفارش‌های من"},{"command":"balance","description":"موجودی، اعتبار و بدهی"},{"command":"addresses","description":"مدیریت آدرس‌ها"},{"command":"addaddress","description":"افزودن آدرس جدید"},{"command":"pay","description":"ثبت پرداخت سفارش"},{"command":"track","description":"رهگیری سفارش"},{"command":"cancel","description":"لغو پیش‌نویس سفارش"}]}
JSON
printf 'url = "https://api.telegram.org/bot%s/setMyCommands"\n' "$bot_token" > "$config_file"
printf 'request = "POST"\n' >> "$config_file"
printf 'header = "Content-Type: application/json"\n' >> "$config_file"
printf 'data-binary = "@%s"\n' "$body_file" >> "$config_file"
: > "$response_file"
curl --silent --show-error --config "$config_file" --output "$response_file"

python3 - "$response_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    response = json.load(source)
if response.get('ok') is not True:
    raise SystemExit('Telegram rejected setMyCommands: ' + str(response.get('description', 'unknown error')))
print('PASS  Telegram accepted the Persian bot command menu.')
PY

printf 'url = "https://api.telegram.org/bot%s/getMyCommands"\n' "$bot_token" > "$config_file"
printf 'request = "GET"\n' >> "$config_file"
: > "$response_file"
curl --silent --show-error --config "$config_file" --output "$response_file"

python3 - "$response_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    response = json.load(source)
if response.get('ok') is not True:
    raise SystemExit('Telegram rejected getMyCommands: ' + str(response.get('description', 'unknown error')))
expected = {'start', 'help', 'lists', 'orders', 'balance', 'addresses',
            'addaddress', 'pay', 'track', 'cancel'}
actual = {item.get('command') for item in response.get('result') or []}
missing = expected - actual
if missing:
    raise SystemExit('Telegram command verification is missing: ' + ', '.join(sorted(missing)))
print('PASS  Persian bot command menu was verified.')
PY

unset bot_token webhook_secret
printf 'Telegram webhook and command menu are ready.\n'
