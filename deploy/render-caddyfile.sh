#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "${BASH_SOURCE[0]%/*}" && pwd)"
template="$script_dir/Caddyfile.example"
output="$script_dir/Caddyfile"
api_domain="${1:-}"
n8n_domain="${2:-}"
tls_email="${3:-}"

fail() { printf 'Caddy configuration failed: %s\n' "$1" >&2; exit 1; }
valid_domain() {
  [[ "$1" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] &&
    [[ "$1" == *.* ]] && [[ "$1" != *..* ]]
}

[[ -f "$template" ]] || fail 'Caddyfile.example is missing.'
valid_domain "$api_domain" || fail 'first argument must be the API hostname without scheme or path.'
valid_domain "$n8n_domain" || fail 'second argument must be the n8n hostname without scheme or path.'
[[ "${api_domain,,}" != "${n8n_domain,,}" ]] || fail 'API and n8n hostnames must be different.'
[[ "$tls_email" =~ ^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$ ]] || fail 'third argument must be a valid TLS notification email.'
[[ ! -e "$output" ]] || fail 'deploy/Caddyfile already exists; remove it explicitly before regenerating.'

sed \
  -e "s/REPLACE_API_DOMAIN/${api_domain,,}/g" \
  -e "s/REPLACE_N8N_DOMAIN/${n8n_domain,,}/g" \
  -e "s/REPLACE_TLS_EMAIL/$tls_email/g" \
  "$template" > "$output"
chmod 600 "$output"

if command -v caddy >/dev/null 2>&1; then
  caddy validate --config "$output" --adapter caddyfile
  printf 'PASS  Caddy configuration syntax validated.\n'
else
  printf 'INFO  Caddy is not installed here; run caddy validate on the VPS before reload.\n'
fi
printf 'Created %s with permissions 600.\n' "$output"
