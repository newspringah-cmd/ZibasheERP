#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$script_dir/.env.production"
compose_file="$script_dir/docker-compose.production.yml"

fail() {
  printf 'Preflight failed: %s\n' "$1" >&2
  exit 1
}

[[ -f "$env_file" ]] || fail "deploy/.env.production does not exist. Copy it from .env.production.example first."

permissions="$(stat -c '%a' "$env_file" 2>/dev/null || true)"
[[ "$permissions" == "600" ]] || fail "deploy/.env.production permissions must be 600 (current: ${permissions:-unknown})."

value_of() {
  sed -n "s/^$1=//p" "$env_file" | tail -n 1
}

require_value() {
  local key="$1"
  local value
  value="$(value_of "$key")"
  [[ -n "$value" ]] || fail "$key is missing or empty."
  [[ "$value" != *REPLACE* && "$value" != *CHANGE_ME* ]] || fail "$key still contains a placeholder."
}

require_min_length() {
  local key="$1"
  local minimum="$2"
  local value
  require_value "$key"
  value="$(value_of "$key")"
  (( ${#value} >= minimum )) || fail "$key must be at least $minimum characters long."
}

require_value 'ConnectionStrings__DefaultConnection'
require_min_length 'ApiKeys__Admin' 32
require_min_length 'ApiKeys__TelegramBot' 32
require_min_length 'ApiKeys__N8n' 32
require_value 'Telegram__BotToken'
require_min_length 'Telegram__WebhookSecret' 32
require_value 'Telegram__AdminChatId'
require_value 'N8n__WebhookUrl'
require_min_length 'N8n__WebhookSecret' 32

admin_key="$(value_of 'ApiKeys__Admin')"
telegram_key="$(value_of 'ApiKeys__TelegramBot')"
n8n_key="$(value_of 'ApiKeys__N8n')"
[[ "$admin_key" != "$telegram_key" && "$admin_key" != "$n8n_key" && "$telegram_key" != "$n8n_key" ]] || \
  fail 'All API keys must be different.'

admin_chat_id="$(value_of 'Telegram__AdminChatId')"
[[ "$admin_chat_id" =~ ^-?[1-9][0-9]*$ ]] || fail 'Telegram__AdminChatId must be a non-zero numeric chat ID.'

n8n_url="$(value_of 'N8n__WebhookUrl')"
[[ "$n8n_url" == https://* ]] || fail 'N8n__WebhookUrl must use HTTPS.'

command -v docker >/dev/null 2>&1 || fail 'Docker is not installed or is not available in PATH.'
docker compose version >/dev/null 2>&1 || fail 'Docker Compose v2 is not available.'

docker compose --env-file "$env_file" -f "$compose_file" config --quiet
printf 'Preflight passed. Production configuration is ready for Docker build.\n'
