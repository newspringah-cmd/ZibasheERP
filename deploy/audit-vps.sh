#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "${BASH_SOURCE[0]%/*}" && pwd)"
api_domain="${1:-}"
n8n_domain="${2:-}"
failures=0

pass() { printf 'PASS  %s\n' "$1"; }
info() { printf 'INFO  %s\n' "$1"; }
fail() { printf 'FAIL  %s\n' "$1" >&2; failures=$((failures + 1)); }
valid_domain() {
  [[ "$1" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] &&
    [[ "$1" == *.* ]] && [[ "$1" != *..* ]]
}

[[ "$(uname -s)" == 'Linux' ]] || { printf 'This audit must run on the Linux VPS.\n' >&2; exit 2; }
valid_domain "$api_domain" || { printf 'Usage: %s API_DOMAIN N8N_DOMAIN\n' "$0" >&2; exit 2; }
valid_domain "$n8n_domain" || { printf 'Usage: %s API_DOMAIN N8N_DOMAIN\n' "$0" >&2; exit 2; }
[[ "${api_domain,,}" != "${n8n_domain,,}" ]] || { printf 'Domains must be different.\n' >&2; exit 2; }

printf 'ZibasheERP VPS read-only audit\nHost: %s\n\n' "$(hostname)"

if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  info "Operating system: ${PRETTY_NAME:-unknown}"
  if [[ "${ID:-}" == 'ubuntu' && "${VERSION_ID:-}" == '24.04' ]]; then
    pass 'host operating system is Ubuntu 24.04 LTS'
  else
    fail 'host operating system is not the registered Ubuntu 24.04 LTS target'
  fi
else
  fail '/etc/os-release is not readable.'
fi

available_kb="$(df -Pk "$script_dir" | awk 'NR == 2 {print $4}')"
if [[ "$available_kb" =~ ^[0-9]+$ ]] && (( available_kb >= 10485760 )); then
  pass 'at least 10 GiB disk space is available'
else
  fail 'less than 10 GiB disk space is available'
fi

memory_kb="$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)"
if [[ "$memory_kb" =~ ^[0-9]+$ ]] && (( memory_kb >= 7340032 )); then
  pass 'at least 7 GiB usable RAM is installed'
else
  fail 'less than 7 GiB usable RAM is installed'
fi

swap_kb="$(awk '/^SwapTotal:/ {print $2}' /proc/meminfo)"
if [[ "$swap_kb" =~ ^[0-9]+$ ]] && (( swap_kb >= 2000000 )); then
  pass 'at least 2 GiB swap is configured'
else
  fail 'less than 2 GiB swap is configured'
fi

if command -v timedatectl >/dev/null 2>&1 &&
   timedatectl show -p NTPSynchronized --value 2>/dev/null | grep -qx 'yes'; then
  pass 'system clock is synchronized'
else
  fail 'system clock synchronization is not confirmed'
fi

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  pass 'Docker daemon is available'
else
  fail 'Docker daemon is unavailable to the current user'
fi
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  pass 'Docker Compose v2 is available'
else
  fail 'Docker Compose v2 is unavailable'
fi

if command -v caddy >/dev/null 2>&1; then
  pass 'Caddy reverse proxy is installed'
elif command -v nginx >/dev/null 2>&1 && systemctl is-active --quiet nginx; then
  pass 'nginx reverse proxy is installed and active'
else
  fail 'neither Caddy nor an active nginx reverse proxy is available'
fi

if command -v getent >/dev/null 2>&1; then
  for domain in "$api_domain" "$n8n_domain"; do
    addresses="$(getent ahostsv4 "$domain" 2>/dev/null | awk '{print $1}' | sort -u | paste -sd, -)"
    if [[ -n "$addresses" ]]; then
      pass "DNS resolves $domain -> $addresses"
    else
      fail "DNS does not resolve $domain to an IPv4 address"
    fi
  done
else
  fail 'getent is required for DNS verification'
fi

if command -v ss >/dev/null 2>&1; then
  exposed="$({ ss -H -ltn 2>/dev/null || true; } | awk '
    $4 ~ /(^|:)(1433|5432|5678|8000|8080|9000|9443)$/ &&
    $4 !~ /^(127\.0\.0\.1|\[::1\]):/ { print $4 }' | sort -u)"
  if [[ -z "$exposed" ]]; then
    pass 'database, API, n8n, and Portainer internal ports are not publicly bound'
  else
    fail "internal ports have non-loopback listeners: $exposed"
  fi
else
  fail 'ss is required for listening-port verification'
fi

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  docker_exposed="$(docker ps --format '{{.Ports}}' | tr ',' '\n' | sed 's/^[[:space:]]*//' | \
    grep -E '(^|[[:space:]])(0\.0\.0\.0|\[::\]):(1433|5432|5678|8080|8000|9000|9443)->' || true)"
  if [[ -z "$docker_exposed" ]]; then
    pass 'Docker does not publish database, application, n8n, or management ports publicly'
  else
    fail "Docker has public internal-port bindings: $docker_exposed"
  fi
fi

if [[ -f "$script_dir/Caddyfile" ]] && command -v caddy >/dev/null 2>&1; then
  if caddy validate --config "$script_dir/Caddyfile" --adapter caddyfile >/dev/null; then
    pass 'generated Caddyfile is valid'
  else
    fail 'generated Caddyfile is invalid'
  fi
elif command -v nginx >/dev/null 2>&1; then
  nginx_command=(nginx)
  if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
    nginx_command=(sudo -n nginx)
  fi

  if "${nginx_command[@]}" -t >/dev/null 2>&1; then
    pass 'nginx configuration syntax is valid'
  else
    fail 'nginx configuration syntax is invalid'
  fi

  nginx_configuration="$("${nginx_command[@]}" -T 2>/dev/null || true)"
  if grep -Fq "server_name $api_domain" <<<"$nginx_configuration" &&
     grep -Fq 'proxy_pass http://127.0.0.1:8080;' <<<"$nginx_configuration"; then
    pass "nginx routes $api_domain to the loopback API"
  else
    fail "nginx does not route $api_domain to http://127.0.0.1:8080"
  fi
  if grep -Fq "server_name $n8n_domain" <<<"$nginx_configuration" &&
     grep -Fq 'proxy_pass http://127.0.0.1:5678;' <<<"$nginx_configuration"; then
    pass "nginx routes $n8n_domain to the loopback n8n service"
  else
    fail "nginx does not route $n8n_domain to http://127.0.0.1:5678"
  fi
else
  fail 'reverse proxy configuration has not been generated yet'
fi

printf '\n'
if (( failures > 0 )); then
  printf 'NO-GO  VPS audit found %d issue(s). No system settings were changed.\n' "$failures" >&2
  exit 1
fi
printf 'GO  VPS prerequisites passed. No system settings were changed.\n'
