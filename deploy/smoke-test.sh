#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$script_dir/.env.production"
base_url="${1:-http://127.0.0.1:8080}"
test_group_id="${2:-}"
response_file="$(mktemp)"
admin_header_file="$(mktemp)"
trap 'rm -f "$response_file" "$admin_header_file"' EXIT

fail() {
  printf 'Smoke test failed: %s\n' "$1" >&2
  exit 1
}

[[ -f "$env_file" ]] || fail 'deploy/.env.production does not exist.'
command -v curl >/dev/null 2>&1 || fail 'curl is required.'

value_of() {
  sed -n "s/^$1=//p" "$env_file" | tail -n 1
}

admin_api_key="$(value_of 'ApiKeys__Admin')"
[[ -n "$admin_api_key" ]] || fail 'ApiKeys__Admin is missing or empty.'
chmod 600 "$admin_header_file"
printf 'X-Api-Key: %s\n' "$admin_api_key" > "$admin_header_file"
unset admin_api_key
base_url="${base_url%/}"

request() {
  local expected_status="$1"
  local label="$2"
  shift 2
  local actual_status
  actual_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' "$@")"
  if [[ "$actual_status" != "$expected_status" ]]; then
    printf 'FAIL  %s (expected HTTP %s, received %s)\n' "$label" "$expected_status" "$actual_status" >&2
    sed -n '1,20p' "$response_file" >&2
    exit 1
  fi
  printf 'PASS  %s (HTTP %s)\n' "$label" "$actual_status"
}

printf 'ZibasheERP production smoke test\n'
printf 'Target: %s\n\n' "$base_url"

request 200 'API process is live' "$base_url/health/live"
request 200 'Database and API are ready' "$base_url/health/ready"
request 401 'Admin endpoint rejects an invalid API key' \
  --header 'X-Api-Key: invalid-smoke-test-key' \
  "$base_url/api/telegram-groups/readiness"
request 200 'Production readiness report is available' \
  --header "@$admin_header_file" \
  "$base_url/api/system/readiness"
if command -v python3 >/dev/null 2>&1; then
  python3 - "$response_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    report = json.load(source)
required = ('environment', 'databaseReachable', 'telegramConfigured',
            'n8nConfigured', 'apiKeysConfigured', 'readyForPilot')
missing = [name for name in required if name not in report]
if missing:
    raise SystemExit('Smoke test failed: readiness fields missing: ' + ', '.join(missing))
print('INFO  Ready for pilot:', report['readyForPilot'])
PY
fi
request 200 'Admin API key is accepted' \
  --header "@$admin_header_file" \
  "$base_url/api/telegram-groups/readiness"

printf '\nTelegram group readiness report:\n'
if command -v python3 >/dev/null 2>&1; then
  python3 -m json.tool "$response_file"
else
  sed -n '1,40p' "$response_file"
fi

if [[ -n "$test_group_id" ]]; then
  [[ "$test_group_id" =~ ^[0-9a-fA-F-]{36}$ ]] || fail 'The test group ID must be an ERP group UUID.'
  printf '\nQueuing a real Telegram delivery test...\n'
  request 202 'Test message was queued for the selected Telegram group' \
    --request POST \
    --header "@$admin_header_file" \
    "$base_url/api/telegram-groups/$test_group_id/test-delivery"
  command -v python3 >/dev/null 2>&1 || fail 'python3 is required to verify real Telegram delivery.'
  notification_id="$(python3 - "$response_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    value = json.load(source).get('notificationId', '')
if not value:
    raise SystemExit('Smoke test failed: delivery response has no notificationId.')
print(value)
PY
)"

  delivered='false'
  for _ in $(seq 1 30); do
    request 200 'Delivery status is queryable' \
      --header "@$admin_header_file" \
      "$base_url/api/notifications/$notification_id"
    status="$(python3 - "$response_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    print(json.load(source).get('status', ''))
PY
)"
    if [[ "$status" == 'Processed' ]]; then
      delivered='true'
      break
    fi
    if [[ "$status" == 'Failed' ]]; then
      fail "Telegram delivery reached terminal status Failed (notification $notification_id)."
    fi
    sleep 1
  done
  [[ "$delivered" == 'true' ]] || fail "Telegram delivery was not processed within 30 seconds (notification $notification_id)."
  printf 'PASS  Telegram worker processed notification %s.\n' "$notification_id"
  printf 'Visually confirm the non-sensitive test message in the selected Telegram group.\n'
else
  printf '\nSafe checks passed. No Telegram message was sent.\n'
  printf 'For a real delivery test, run: ./smoke-test.sh %s TELEGRAM_GROUP_UUID\n' "$base_url"
fi
