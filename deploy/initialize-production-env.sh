#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
production_example="$script_dir/.env.production.example"
n8n_example="$script_dir/.env.n8n.example"
production_env="$script_dir/.env.production"
n8n_env="$script_dir/.env.n8n"
production_temp=""
n8n_temp=""

fail() {
  printf 'Environment initialization failed: %s\n' "$1" >&2
  exit 1
}

cleanup() {
  [[ -z "$production_temp" ]] || rm -f "$production_temp"
  [[ -z "$n8n_temp" ]] || rm -f "$n8n_temp"
}
trap cleanup EXIT

[[ -f "$production_example" && -f "$n8n_example" ]] || fail 'Environment example files are missing.'
[[ ! -e "$production_env" ]] || fail 'deploy/.env.production already exists; it was not changed.'
[[ ! -e "$n8n_env" ]] || fail 'deploy/.env.n8n already exists; it was not changed.'
command -v openssl >/dev/null 2>&1 || fail 'openssl is required.'

secret() {
  openssl rand -hex 32
}

admin_key="$(secret)"
telegram_api_key="$(secret)"
n8n_api_key="$(secret)"
telegram_webhook_secret="$(secret)"
n8n_webhook_secret="$(secret)"
n8n_postgres_password="$(secret)"
n8n_encryption_key="$(secret)"
n8n_runner_token="$(secret)"

umask 077
production_temp="$(mktemp "$script_dir/.env.production.XXXXXX")"
n8n_temp="$(mktemp "$script_dir/.env.n8n.XXXXXX")"

while IFS= read -r line || [[ -n "$line" ]]; do
  case "$line" in
    ApiKeys__Admin=*) printf 'ApiKeys__Admin=%s\n' "$admin_key" ;;
    ApiKeys__TelegramBot=*) printf 'ApiKeys__TelegramBot=%s\n' "$telegram_api_key" ;;
    ApiKeys__N8n=*) printf 'ApiKeys__N8n=%s\n' "$n8n_api_key" ;;
    Telegram__WebhookSecret=*) printf 'Telegram__WebhookSecret=%s\n' "$telegram_webhook_secret" ;;
    N8n__WebhookSecret=*) printf 'N8n__WebhookSecret=%s\n' "$n8n_webhook_secret" ;;
    *) printf '%s\n' "$line" ;;
  esac
done < "$production_example" > "$production_temp"

while IFS= read -r line || [[ -n "$line" ]]; do
  case "$line" in
    N8N_POSTGRES_PASSWORD=*) printf 'N8N_POSTGRES_PASSWORD=%s\n' "$n8n_postgres_password" ;;
    N8N_ENCRYPTION_KEY=*) printf 'N8N_ENCRYPTION_KEY=%s\n' "$n8n_encryption_key" ;;
    N8N_RUNNERS_AUTH_TOKEN=*) printf 'N8N_RUNNERS_AUTH_TOKEN=%s\n' "$n8n_runner_token" ;;
    *) printf '%s\n' "$line" ;;
  esac
done < "$n8n_example" > "$n8n_temp"

chmod 600 "$production_temp" "$n8n_temp"
mv "$production_temp" "$production_env"
production_temp=""
mv "$n8n_temp" "$n8n_env"
n8n_temp=""
unset admin_key telegram_api_key n8n_api_key telegram_webhook_secret
unset n8n_webhook_secret n8n_postgres_password n8n_encryption_key n8n_runner_token

printf 'Production environment files were created with protected random secrets.\n'
printf 'Complete the remaining REPLACE/CHANGE_ME values, domains, IDs, and connection string before preflight.\n'
