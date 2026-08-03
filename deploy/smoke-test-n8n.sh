#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$script_dir/.env.n8n"
compose_file="$script_dir/docker-compose.n8n.production.yml"
n8n_base_url="${1:-}"

fail() {
  printf 'n8n smoke test failed: %s\n' "$1" >&2
  exit 1
}

[[ -f "$env_file" ]] || fail 'deploy/.env.n8n does not exist.'
[[ "$n8n_base_url" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || \
  fail 'Pass the public n8n origin, for example https://n8n.example.com.'
command -v docker >/dev/null 2>&1 || fail 'Docker is not available.'
command -v curl >/dev/null 2>&1 || fail 'curl is required.'

compose=(docker compose --env-file "$env_file" -f "$compose_file")
running="$("${compose[@]}" ps --status running --services)"
for service in n8n-postgres n8n n8n-runner gotenberg; do
  grep -Fxq "$service" <<< "$running" || fail "$service is not running."
  printf 'PASS  %s is running.\n' "$service"
done

"${compose[@]}" exec -T n8n-postgres pg_isready --username=n8n --dbname=n8n >/dev/null
printf 'PASS  n8n PostgreSQL accepts connections.\n'

status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' "${n8n_base_url%/}/healthz")"
[[ "$status" == '200' ]] || fail "public n8n health endpoint returned HTTP $status."
printf 'PASS  public n8n health endpoint is ready (HTTP 200).\n'

"${compose[@]}" exec -T n8n node --input-type=module - <<'NODE'
const html = '<!doctype html><html lang="fa" dir="rtl"><meta charset="utf-8"><body><h1>آزمایش PDF زیباشه</h1></body></html>';
const form = new FormData();
form.append('files', new Blob([html], { type: 'text/html' }), 'index.html');
const response = await fetch('http://gotenberg:3000/forms/chromium/convert/html', {
  method: 'POST',
  body: form,
  signal: AbortSignal.timeout(60000)
});
if (!response.ok) throw new Error(`Gotenberg returned HTTP ${response.status}`);
const pdf = new Uint8Array(await response.arrayBuffer());
const signature = new TextDecoder().decode(pdf.slice(0, 5));
if (signature !== '%PDF-' || pdf.length < 1000) throw new Error('Gotenberg output is not a valid non-empty PDF.');
console.log(`PASS  Persian HTML was converted to a ${pdf.length}-byte PDF.`);
NODE

printf 'n8n infrastructure smoke test passed.\n'
