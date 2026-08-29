#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$script_dir/.env.n8n"
compose_file="$script_dir/docker-compose.n8n.production.yml"

fail() {
  printf 'n8n preflight failed: %s\n' "$1" >&2
  exit 1
}

[[ -f "$env_file" ]] || fail 'deploy/.env.n8n does not exist. Copy it from .env.n8n.example first.'
permissions="$(stat -c '%a' "$env_file" 2>/dev/null || true)"
[[ "$permissions" == "600" ]] || fail "deploy/.env.n8n permissions must be 600 (current: ${permissions:-unknown})."

value_of() {
  sed -n "s/^$1=//p" "$env_file" | tail -n 1
}

require_secret() {
  local key="$1"
  local value
  value="$(value_of "$key")"
  [[ -n "$value" ]] || fail "$key is missing or empty."
  [[ "$value" != *REPLACE* ]] || fail "$key still contains a placeholder."
  (( ${#value} >= 32 )) || fail "$key must be at least 32 characters long."
  [[ "$value" =~ ^[^[:space:]]{32,256}$ ]] || \
    fail "$key must not contain whitespace and must be at most 256 characters."
}

n8n_domain="$(value_of 'N8N_DOMAIN')"
[[ "$n8n_domain" =~ ^[A-Za-z0-9.-]+$ && "$n8n_domain" == *.* ]] || fail 'N8N_DOMAIN must be a hostname without https:// or a path.'
[[ "$n8n_domain" != 'n8n.example.com' ]] || fail 'N8N_DOMAIN still contains the example hostname.'
require_secret 'N8N_POSTGRES_PASSWORD'
require_secret 'N8N_ENCRYPTION_KEY'
require_secret 'N8N_RUNNERS_AUTH_TOKEN'

postgres_password="$(value_of 'N8N_POSTGRES_PASSWORD')"
encryption_key="$(value_of 'N8N_ENCRYPTION_KEY')"
runner_token="$(value_of 'N8N_RUNNERS_AUTH_TOKEN')"
[[ "$postgres_password" != "$encryption_key" &&
   "$postgres_password" != "$runner_token" &&
   "$encryption_key" != "$runner_token" ]] || fail 'All n8n secrets must be different.'

command -v docker >/dev/null 2>&1 || fail 'Docker is not installed or is not available in PATH.'
docker compose version >/dev/null 2>&1 || fail 'Docker Compose v2 is not available.'
docker compose --env-file "$env_file" -f "$compose_file" config --quiet
printf 'n8n preflight passed.\n'
