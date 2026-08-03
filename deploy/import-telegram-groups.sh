#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$script_dir/.env.production"
api_base="${1:-}"
csv_file="${2:-}"
mode="${3:-}"
response_file="$(mktemp)"
admin_header_file="$(mktemp)"
trap 'rm -f "$response_file" "$admin_header_file"' EXIT

fail() {
  printf 'Telegram group import failed: %s\n' "$1" >&2
  exit 1
}

[[ -f "$env_file" ]] || fail 'deploy/.env.production does not exist.'
[[ "$api_base" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || \
  fail 'Pass the public API origin, for example https://api.example.com.'
[[ -f "$csv_file" && -r "$csv_file" ]] || fail 'The CSV file does not exist or is not readable.'
csv_size="$(stat -c '%s' "$csv_file" 2>/dev/null || true)"
[[ "$csv_size" =~ ^[0-9]+$ && "$csv_size" -gt 0 && "$csv_size" -le 10485760 ]] || \
  fail 'The CSV file must be between 1 byte and 10 MB.'
command -v curl >/dev/null 2>&1 || fail 'curl is required.'
command -v python3 >/dev/null 2>&1 || fail 'python3 is required.'

dry_run=true
if [[ "$mode" == '--apply' ]]; then
  [[ "${CONFIRM_TELEGRAM_GROUP_IMPORT:-}" == 'YES' ]] || \
    fail 'Set CONFIRM_TELEGRAM_GROUP_IMPORT=YES for a real import.'
  dry_run=false
elif [[ -n "$mode" ]]; then
  fail 'The optional third argument can only be --apply.'
fi

admin_api_key="$(sed -n 's/^ApiKeys__Admin=//p' "$env_file" | tail -n 1)"
[[ -n "$admin_api_key" && "$admin_api_key" != *REPLACE* ]] || fail 'ApiKeys__Admin is not configured.'
chmod 600 "$admin_header_file" "$response_file"
printf 'X-Api-Key: %s\n' "$admin_api_key" > "$admin_header_file"
unset admin_api_key

status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' \
  --request POST \
  --header "@$admin_header_file" \
  --form "file=@${csv_file};type=text/csv" \
  "${api_base%/}/api/telegram-groups/import-csv?dryRun=${dry_run}")"
[[ "$status" == '200' ]] || {
  sed -n '1,30p' "$response_file" >&2
  fail "API returned HTTP $status."
}

python3 - "$response_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding='utf-8') as source:
    report = json.load(source)
print('Mode:', 'DRY RUN' if report.get('dryRun') else 'APPLIED')
for key in ('totalRows', 'selectedRows', 'created', 'updated', 'unchanged', 'issueCount'):
    print(f'{key}:', report.get(key))
issues = report.get('issues') or []
if issues:
    print('\nFirst issues:')
    for issue in issues[:20]:
        print(f"- row={issue.get('rowNumber')} code={issue.get('code')} username={issue.get('customerUsername')} chatId={issue.get('chatId')}")
PY

if [[ "$dry_run" == true ]]; then
  printf '\nNo database changes were made. Review the report before using --apply.\n'
else
  printf '\nImport completed. Imported groups remain inactive until Telegram confirms bot membership.\n'
fi
