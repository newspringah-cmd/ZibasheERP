#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "${BASH_SOURCE[0]%/*}" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
skip_containers='false'
[[ "${1:-}" != '--skip-containers' ]] || skip_containers='true'
[[ $# -le 1 ]] || { printf 'Usage: %s [--skip-containers]\n' "$0" >&2; exit 2; }
cd "$repo_root"

pass() { printf 'PASS  %s\n' "$1"; }
fail() { printf 'FAIL  %s\n' "$1" >&2; exit 1; }
require() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required."; }

require git
require dotnet
require python3
require node

printf 'ZibasheERP release gate\nRepository: %s\n\n' "$repo_root"

bash "$script_dir/verify-no-secrets.sh"
pass 'repository secret safety'

while IFS= read -r script; do
  bash -n "$script"
done < <(find deploy -maxdepth 1 -type f -name '*.sh' -print | sort)
pass 'deployment script syntax'

python3 -m json.tool integrations/n8n/contracts/event-envelope.schema.json >/dev/null
python3 -m json.tool integrations/n8n/contracts/artifact-callback.schema.json >/dev/null
python3 -m json.tool integrations/n8n/contracts/delivery-failure.schema.json >/dev/null
python3 integrations/n8n/tests/contracts.test.py
python3 deploy/tests/production-target.test.py
node --check integrations/n8n/code/build-invoice-html.js
node --check integrations/n8n/code/validate-event-metadata.js
node integrations/n8n/tests/build-invoice-html.test.js
node integrations/n8n/tests/validate-event-metadata.test.js
pass 'production inventory, n8n contracts, and executable Code nodes'

dotnet restore ZibasheERP.slnx -p:NuGetAudit=false --ignore-failed-sources
dotnet build ZibasheERP.slnx --configuration Release --no-restore --warnaserror
dotnet run \
  --project ZibasheERP.Application.Tests/ZibasheERP.Application.Tests.csproj \
  --configuration Release --no-build --no-restore
pass '.NET Release build and automated tests'

dotnet ef migrations has-pending-model-changes \
  --project ZibasheERP.Infrastructure/ZibasheERP.Infrastructure.csproj \
  --startup-project ZibasheERP.API/ZibasheERP.API.csproj \
  --no-build --configuration Release
pass 'EF Core migration snapshot'

if [[ "$skip_containers" == 'true' ]]; then
  printf 'SKIP  Compose validation was explicitly skipped.\n'
else
  require docker
  docker compose version >/dev/null 2>&1 || fail 'Docker Compose v2 is required.'
  docker compose --env-file deploy/.env.production.example \
    -f deploy/docker-compose.production.yml config --quiet
  docker compose --env-file deploy/.env.n8n.example \
    -f deploy/docker-compose.n8n.production.yml config --quiet
  pass 'production Compose configuration'
fi

if ! git diff --check; then
  fail 'Git detected whitespace errors.'
fi
pass 'Git whitespace check'

printf '\nGO  All selected release gates passed. This does not replace VPS smoke tests or a real Telegram/n8n pilot.\n'
